using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kharos.Audio.Dsp;

/// <summary>
/// Отклик свёртки, разрезанный на блоки по кванту, в частотной области (равномерное разбиение, overlap-save, БПФ на
/// 2·128). Спектр блока упакован: re[0] — постоянная, im[0] — Найквист, дальше бины 1…127. Считается один раз на запись
/// и нормировку (в потоке игры) и делится между узлами.
/// </summary>
internal sealed class ConvolutionKernel
{
    public const int Block = AudioFormat.Quantum;
    public const int Size = 2 * Block;

    private static readonly ConditionalWeakTable<SampleBuffer, ConvolutionKernel?[]> Cache = new();

    private ConvolutionKernel(SampleBuffer buffer, bool normalize)
    {
        Channels = buffer.ChannelCount;
        Partitions = Math.Max(1, (buffer.Length + Block - 1) / Block);
        var scale = normalize ? NormalizationScale(buffer) : 1f;
        var fft = new Fft(Size);
        Re = new float[Channels][];
        Im = new float[Channels][];
        var re = new float[Size];
        var im = new float[Size];
        for (var c = 0; c < Channels; c++)
        {
            Re[c] = new float[Partitions * Block];
            Im[c] = new float[Partitions * Block];
            var data = buffer.Channel(c);
            for (var p = 0; p < Partitions; p++)
            {
                Array.Clear(re);
                Array.Clear(im);
                var start = p * Block;
                var count = Math.Min(Block, data.Length - start);
                for (var i = 0; i < count; i++)
                    re[i] = data[start + i] * scale;
                fft.Forward(re, im);
                Pack(re, im, Re[c].AsSpan(p * Block, Block), Im[c].AsSpan(p * Block, Block));
            }
        }
    }

    public int Channels { get; }

    public int Partitions { get; }

    /// <summary>Спектры блоков: [канал][блок·128 + бин].</summary>
    public float[][] Re { get; }

    public float[][] Im { get; }

    /// <summary>Ядро записи (общее для всех узлов с той же записью и нормировкой).</summary>
    public static ConvolutionKernel For(SampleBuffer buffer, bool normalize)
    {
        var slots = Cache.GetValue(buffer, _ => new ConvolutionKernel?[2]);
        var index = normalize ? 1 : 0;
        lock (slots)
            return slots[index] ??= new ConvolutionKernel(buffer, normalize);
    }

    /// <summary>Нормировка ConvolverNode по спецификации (calculateNormalizationScale).</summary>
    public static float NormalizationScale(SampleBuffer buffer)
    {
        const double gainCalibration = 0.00125;
        const double gainCalibrationSampleRate = 44100;
        const double minPower = 0.000125;
        double power = 0;
        for (var c = 0; c < buffer.ChannelCount; c++)
            foreach (var sample in buffer.Channel(c))
                power += sample * (double)sample;
        power = Math.Sqrt(power / (buffer.ChannelCount * (double)Math.Max(1, buffer.Length)));
        if (!double.IsFinite(power) || power < minPower)
            power = minPower;
        var scale = 1 / power * gainCalibration * gainCalibrationSampleRate / buffer.SampleRate;
        if (buffer.ChannelCount == 4)
            scale *= 0.5;
        return (float)scale;
    }

    /// <summary>Упаковать спектр действительного сигнала (бины 0…N/2).</summary>
    public static void Pack(ReadOnlySpan<float> re, ReadOnlySpan<float> im, Span<float> packedRe, Span<float> packedIm)
    {
        re[..Block].CopyTo(packedRe);
        im[..Block].CopyTo(packedIm);
        packedIm[0] = re[Block];
    }

    /// <summary>Распаковать в полный спектр по сопряжённой симметрии.</summary>
    public static void Unpack(ReadOnlySpan<float> packedRe, ReadOnlySpan<float> packedIm, Span<float> re, Span<float> im)
    {
        packedRe.CopyTo(re);
        packedIm.CopyTo(im);
        im[0] = 0;
        re[Block] = packedIm[0];
        im[Block] = 0;
        for (var k = 1; k < Block; k++)
        {
            re[Size - k] = re[k];
            im[Size - k] = -im[k];
        }
    }
}

/// <summary>
/// ConvolverNode: свёртка с откликом 1, 2 или 4 каналов. Моно-отклик: вход по каналам; стерео: L = вход·h0, R = вход·h1
/// (стереовход — L·h0 и R·h1); четыре канала — полное стерео L = L·h0 + R·h2, R = L·h1 + R·h3. Задержки нет: блок равен
/// кванту.
/// </summary>
internal sealed class ConvolverNode : Node
{
    private const int B = ConvolutionKernel.Block;
    private const int N = ConvolutionKernel.Size;

    private readonly ConvolutionKernel _kernel;
    private readonly Fft _fft = new(N);
    private readonly float[][] _historyRe = new float[2][];
    private readonly float[][] _historyIm = new float[2][];
    private readonly float[][] _previous = [new float[B], new float[B]];
    private readonly float[] _re = new float[N];
    private readonly float[] _im = new float[N];
    private readonly float[] _accRe = new float[B];
    private readonly float[] _accIm = new float[B];
    private int _head;
    private int _inputs;
    private long _silentFrames = long.MaxValue / 2;

    public ConvolverNode(SampleBuffer buffer, bool normalize)
    {
        _kernel = ConvolutionKernel.For(buffer, normalize);
        var size = _kernel.Partitions * B;
        _historyRe[0] = new float[size];
        _historyIm[0] = new float[size];
    }

    public override long TailFrames => (long)_kernel.Partitions * B + B;

    public override void Process(long frame)
    {
        var channels = Input(out var l, out var r);
        if (channels == 0)
        {
            _silentFrames += Q;
            if (_silentFrames > TailFrames || _inputs == 0)
            {
                _inputs = 0;
                Channels = 0;
                return;
            }
        }
        else
        {
            _silentFrames = 0;
            var wanted = _kernel.Channels == 4 ? 2 : channels;
            if (wanted > _inputs)
            {
                if (wanted == 2 && _historyRe[1] is null)
                {
                    _historyRe[1] = new float[_historyRe[0].Length];
                    _historyIm[1] = new float[_historyIm[0].Length];
                }
                if (_inputs == 1)
                {
                    _historyRe[0].CopyTo(_historyRe[1], 0);
                    _historyIm[0].CopyTo(_historyIm[1], 0);
                    _previous[0].CopyTo(_previous[1], 0);
                }
                _inputs = wanted;
            }
        }
        _head = _head + 1 == _kernel.Partitions ? 0 : _head + 1;
        for (var c = 0; c < _inputs; c++)
        {
            var source = channels == 0 ? null : c == 1 && channels == 2 ? r : l;
            Push(c, source);
        }
        switch (_kernel.Channels)
        {
            case 1:
                Channels = _inputs;
                Convolve(0, 0, L, accumulate: false);
                if (_inputs == 2)
                    Convolve(1, 0, R, accumulate: false);
                break;
            case 2:
                Channels = 2;
                Convolve(0, 0, L, accumulate: false);
                Convolve(_inputs == 2 ? 1 : 0, 1, R, accumulate: false);
                break;
            default:
                Channels = 2;
                Convolve(0, 0, L, accumulate: false);
                Convolve(1, 2, L, accumulate: true);
                Convolve(0, 1, R, accumulate: false);
                Convolve(1, 3, R, accumulate: true);
                break;
        }
    }

    private void Push(int channel, float[]? input)
    {
        var previous = _previous[channel];
        previous.CopyTo(_re, 0);
        if (input is null)
            Array.Clear(_re, B, B);
        else
            input.CopyTo(_re, B);
        Array.Clear(_im);
        _re.AsSpan(B, B).CopyTo(previous);
        _fft.Forward(_re, _im);
        ConvolutionKernel.Pack(_re, _im, _historyRe[channel].AsSpan(_head * B, B), _historyIm[channel].AsSpan(_head * B, B));
    }

    private void Convolve(int input, int response, float[] output, bool accumulate)
    {
        var partitions = _kernel.Partitions;
        var xr = _historyRe[input];
        var xi = _historyIm[input];
        var hr = _kernel.Re[response];
        var hi = _kernel.Im[response];
        var accRe = _accRe.AsSpan();
        var accIm = _accIm.AsSpan();
        accRe.Clear();
        accIm.Clear();
        float dc = 0, nyquist = 0;
        var ar = MemoryMarshal.Cast<float, Vector<float>>(accRe);
        var ai = MemoryMarshal.Cast<float, Vector<float>>(accIm);
        for (var j = 0; j < partitions; j++)
        {
            var slot = _head - j;
            if (slot < 0)
                slot += partitions;
            var x0 = slot * B;
            var h0 = j * B;
            dc += xr[x0] * hr[h0];
            nyquist += xi[x0] * hi[h0];
            var vxr = MemoryMarshal.Cast<float, Vector<float>>(xr.AsSpan(x0, B));
            var vxi = MemoryMarshal.Cast<float, Vector<float>>(xi.AsSpan(x0, B));
            var vhr = MemoryMarshal.Cast<float, Vector<float>>(hr.AsSpan(h0, B));
            var vhi = MemoryMarshal.Cast<float, Vector<float>>(hi.AsSpan(h0, B));
            for (var v = 0; v < vxr.Length; v++)
            {
                var a = vxr[v];
                var b = vxi[v];
                var c = vhr[v];
                var d = vhi[v];
                ar[v] += a * c - b * d;
                ai[v] += a * d + b * c;
            }
        }
        accRe[0] = dc;
        accIm[0] = nyquist;
        ConvolutionKernel.Unpack(accRe, accIm, _re, _im);
        _fft.Inverse(_re, _im);
        const float scale = 1f / N;
        if (accumulate)
            for (var i = 0; i < B; i++)
                output[i] += _re[B + i] * scale;
        else
            for (var i = 0; i < B; i++)
                output[i] = _re[B + i] * scale;
    }
}
