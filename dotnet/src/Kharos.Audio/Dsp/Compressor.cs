using Kharos.Audio.Graph;

namespace Kharos.Audio.Dsp;

/// <summary>
/// DynamicsCompressorNode. Спецификация задаёт каркас (кривая сжатия с коленом, детектор, огибающая атаки и отпускания,
/// предзадержка, компенсирующее усиление (1/curve(1))^0.6) и оставляет формы кривых браузеру — здесь они как в
/// Chromium/WebKit (DynamicsCompressorKernel): экспоненциальное колено с подбором k под наклон 1/ratio, адаптивное
/// отпускание по четырём зонам, предзадержка 6 мс, деления по 32 кадра, синусная деформация усиления.
/// Параметры k-rate; <see cref="Reduction"/> — сжатие в дБ (≤ 0).
/// </summary>
internal sealed class CompressorNode : Node
{
    private const int Division = 32;
    private const int MaxPreDelay = 1024;
    private const float PreDelayTime = 0.006f;
    private const float SatReleaseTime = 0.0025f;
    private const float MeteringReleaseTime = 0.325f;
    private const float Zone1 = 0.09f, Zone2 = 0.16f, Zone3 = 0.42f, Zone4 = 0.98f;

    private readonly float[][] _delay = [new float[MaxPreDelay], new float[MaxPreDelay]];
    private readonly Param[] _params;
    private readonly float _meteringReleaseK;
    private int _readIndex;
    private int _writeIndex;
    private float _detectorAverage;
    private float _compressorGain = 1f;
    private float _maxAttackDiffDb = -1f;
    private float _meteringGain = 1f;
    private int _channels;
    private long _silent = long.MaxValue / 2;

    // Кривая: пересчитывается при смене порога, колена или отношения.
    private float _dbThreshold = float.NaN, _dbKnee = float.NaN, _ratio = float.NaN;
    private float _linearThreshold, _kneeThreshold, _kneeThresholdDb, _yKneeThresholdDb, _k, _slope;

    public CompressorNode(float threshold, float knee, float ratio, float attack, float release)
    {
        Threshold = new Param(threshold, -100f, 0f, kRate: true);
        Knee = new Param(knee, 0f, 40f, kRate: true);
        Ratio = new Param(ratio, 1f, 20f, kRate: true);
        Attack = new Param(attack, 0f, 1f, kRate: true);
        Release = new Param(release, 0f, 1f, kRate: true);
        _params = [Threshold, Knee, Ratio, Attack, Release];
        _meteringReleaseK = 1f - MathF.Exp(-1f / (AudioFormat.SampleRate * MeteringReleaseTime));
        var preDelay = (int)(PreDelayTime * AudioFormat.SampleRate);
        _readIndex = 0;
        _writeIndex = preDelay;
    }

    public readonly Param Threshold;
    public readonly Param Knee;
    public readonly Param Ratio;
    public readonly Param Attack;
    public readonly Param Release;

    /// <summary>Сжатие в дБ (reduction).</summary>
    public float Reduction => _meteringGain;

    public override Param? GetParam(ParamId id) => id switch
    {
        ParamId.Threshold => Threshold,
        ParamId.Knee => Knee,
        ParamId.Ratio => Ratio,
        ParamId.Attack => Attack,
        ParamId.Release => Release,
        _ => null,
    };

    public override ReadOnlySpan<Param> Params => _params;

    public override long TailFrames => MaxPreDelay;

    public override void Process(long frame)
    {
        var channels = Input(out var l, out var r);
        if (channels == 0)
        {
            _silent += Q;
            if (_silent > MaxPreDelay + Q && _compressorGain >= 0.999f || _channels == 0)
            {
                Channels = 0;
                return;
            }
            Array.Clear(L);
            l = r = L;
        }
        else
        {
            _silent = 0;
            _channels = Math.Max(_channels, channels);
        }
        var outputs = Math.Max(_channels, 1);
        Channels = outputs;
        var k = UpdateCurve(Threshold.Constant, Knee.Constant, Ratio.Constant);
        var fullRangeGain = Saturate(1f, k);
        var makeup = MathF.Pow(1f / fullRangeGain, 0.6f);
        var attackFrames = MathF.Max(0.001f, Attack.Constant) * AudioFormat.SampleRate;
        var releaseFrames = Release.Constant * AudioFormat.SampleRate;
        var satReleaseFrames = SatReleaseTime * AudioFormat.SampleRate;
        float y1 = releaseFrames * Zone1, y2 = releaseFrames * Zone2, y3 = releaseFrames * Zone3, y4 = releaseFrames * Zone4;
        var kA = 0.9999999999999998f * y1 + 1.8432219684323923e-16f * y2 - 1.9373394351676423e-16f * y3 + 8.824516011816245e-18f * y4;
        var kB = -1.5788320352845888f * y1 + 2.3305837032074286f * y2 - 0.9141194204840429f * y3 + 0.1623677525612032f * y4;
        var kC = 0.5334142869106424f * y1 - 1.272736789213631f * y2 + 0.9258856042207512f * y3 - 0.18656310191776226f * y4;
        var kD = 0.08783463138207234f * y1 - 0.1694162967925622f * y2 + 0.08588057951595272f * y3 - 0.00429891410546283f * y4;
        var kE = -0.042416883008123074f * y1 + 0.1115693827987602f * y2 - 0.09764676325265872f * y3 + 0.028494263462021576f * y4;

        var index = 0;
        for (var division = 0; division < Q / Division; division++)
        {
            if (!float.IsFinite(_detectorAverage))
                _detectorAverage = 1f;
            var desiredGain = _detectorAverage;
            var scaledDesiredGain = MathF.Asin(desiredGain) / (0.5f * MathF.PI);
            float envelopeRate;
            var releasing = scaledDesiredGain > _compressorGain;
            var compressionDiffDb = ToDb(_compressorGain / scaledDesiredGain);
            if (releasing)
            {
                _maxAttackDiffDb = -1f;
                if (!float.IsFinite(compressionDiffDb))
                    compressionDiffDb = -1f;
                var x = 0.25f * (Math.Clamp(compressionDiffDb, -12f, 0f) + 12f);
                float x2 = x * x, x3 = x2 * x, x4 = x2 * x2;
                var frames = kA + kB * x + kC * x2 + kD * x3 + kE * x4;
                const float spacingDb = 5f;
                envelopeRate = ToLinear(spacingDb / frames);
            }
            else
            {
                if (!float.IsFinite(compressionDiffDb))
                    compressionDiffDb = 1f;
                if (_maxAttackDiffDb == -1f || _maxAttackDiffDb < compressionDiffDb)
                    _maxAttackDiffDb = compressionDiffDb;
                var effAttenDiffDb = MathF.Max(0.5f, _maxAttackDiffDb);
                envelopeRate = 1f - MathF.Pow(0.25f / effAttenDiffDb, 1f / attackFrames);
            }

            var detectorAverage = _detectorAverage;
            var compressorGain = _compressorGain;
            for (var n = 0; n < Division; n++, index++)
            {
                var inL = l[index];
                var inR = outputs == 2 ? r[index] : inL;
                _delay[0][_writeIndex] = inL;
                _delay[1][_writeIndex] = inR;
                var input = MathF.Max(MathF.Abs(inL), outputs == 2 ? MathF.Abs(inR) : 0f);
                var shaped = Saturate(input, k);
                var attenuation = input <= 0.0001f ? 1f : shaped / input;
                var attenuationDb = MathF.Max(2f, -ToDb(attenuation));
                var satReleaseRate = ToLinear(attenuationDb / satReleaseFrames) - 1f;
                var rate = attenuation > detectorAverage ? satReleaseRate : 1f;
                detectorAverage += (attenuation - detectorAverage) * rate;
                detectorAverage = MathF.Min(1f, detectorAverage);
                if (!float.IsFinite(detectorAverage))
                    detectorAverage = 1f;
                if (envelopeRate < 1f)
                    compressorGain += (scaledDesiredGain - compressorGain) * envelopeRate;
                else
                    compressorGain = MathF.Min(1f, compressorGain * envelopeRate);
                var warped = MathF.Sin(0.5f * MathF.PI * compressorGain);
                var total = makeup * warped;
                var dbRealGain = 20f * MathF.Log10(warped);
                if (dbRealGain < _meteringGain)
                    _meteringGain = dbRealGain;
                else
                    _meteringGain += (dbRealGain - _meteringGain) * _meteringReleaseK;
                L[index] = _delay[0][_readIndex] * total;
                if (outputs == 2)
                    R[index] = _delay[1][_readIndex] * total;
                _readIndex = (_readIndex + 1) & (MaxPreDelay - 1);
                _writeIndex = (_writeIndex + 1) & (MaxPreDelay - 1);
            }
            _detectorAverage = Flush(detectorAverage);
            _compressorGain = Flush(compressorGain);
        }
        if (channels == 0 && _silent > MaxPreDelay + Q)
            _channels = 0;
    }

    private static float Flush(float value) => MathF.Abs(value) < 1e-30f ? 0f : value;

    private static float ToDb(float linear) => 20f * MathF.Log10(linear);

    private static float ToLinear(float db) => MathF.Pow(10f, 0.05f * db);

    private float KneeCurve(float x, float k) =>
        x < _linearThreshold ? x : _linearThreshold + (1f - MathF.Exp(-k * (x - _linearThreshold))) / k;

    private float Saturate(float x, float k)
    {
        if (x < _kneeThreshold)
            return KneeCurve(x, k);
        var xDb = ToDb(x);
        var yDb = _yKneeThresholdDb + _slope * (xDb - _kneeThresholdDb);
        return ToLinear(yDb);
    }

    private float SlopeAt(float x, float k)
    {
        if (x < _linearThreshold)
            return 1f;
        var x2 = x * 1.001f;
        float xDb = ToDb(x), x2Db = ToDb(x2);
        float yDb = ToDb(KneeCurve(x, k)), y2Db = ToDb(KneeCurve(x2, k));
        return (y2Db - yDb) / (x2Db - xDb);
    }

    private float KAtSlope(float desiredSlope)
    {
        var x = ToLinear(_dbThreshold + _dbKnee);
        float minK = 0.1f, maxK = 10000f, k = 5f;
        for (var i = 0; i < 15; i++)
        {
            if (SlopeAt(x, k) < desiredSlope)
                maxK = k;
            else
                minK = k;
            k = MathF.Sqrt(minK * maxK);
        }
        return k;
    }

    private float UpdateCurve(float dbThreshold, float dbKnee, float ratio)
    {
        if (dbThreshold == _dbThreshold && dbKnee == _dbKnee && ratio == _ratio)
            return _k;
        _dbThreshold = dbThreshold;
        _linearThreshold = ToLinear(dbThreshold);
        _dbKnee = dbKnee;
        _ratio = ratio;
        _slope = 1f / ratio;
        var k = KAtSlope(1f / ratio);
        _kneeThresholdDb = dbThreshold + dbKnee;
        _kneeThreshold = ToLinear(_kneeThresholdDb);
        _yKneeThresholdDb = ToDb(KneeCurve(_kneeThreshold, k));
        _k = k;
        return k;
    }
}
