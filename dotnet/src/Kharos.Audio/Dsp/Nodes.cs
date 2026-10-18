using Kharos.Audio.Graph;

namespace Kharos.Audio.Dsp;

/// <summary>GainNode: выход = вход · gain (a-rate).</summary>
internal sealed class GainNode(float gain) : Node
{
    public readonly Param Gain = new(gain, -float.MaxValue, float.MaxValue);

    public override Param? GetParam(ParamId id) => id == ParamId.Gain ? Gain : null;

    public override ReadOnlySpan<Param> Params => _params ??= [Gain];

    private Param[]? _params;

    public override void Process(long frame)
    {
        var channels = Input(out var l, out var r);
        if (channels == 0 || Gain.Steady && Gain.Constant == 0f)
        {
            Channels = 0;
            return;
        }
        Channels = channels;
        if (Gain.Steady)
        {
            Mix.Scale(l, L, Gain.Constant);
            if (channels == 2)
                Mix.Scale(r, R, Gain.Constant);
        }
        else
        {
            Mix.Multiply(l, Gain.Values, L);
            if (channels == 2)
                Mix.Multiply(r, Gain.Values, R);
        }
    }
}

/// <summary>ConstantSourceNode: моно-выход offset (a-rate) от start до stop.</summary>
internal sealed class ConstNode(float offset) : SourceNode
{
    public readonly Param Offset = new(offset, -float.MaxValue, float.MaxValue);

    public override Param? GetParam(ParamId id) => id == ParamId.Offset ? Offset : null;

    public override ReadOnlySpan<Param> Params => _params ??= [Offset];

    private Param[]? _params;

    public override void Process(long frame)
    {
        if (!Active(frame, out var from, out var to))
        {
            Channels = 0;
            return;
        }
        Channels = 1;
        Array.Clear(L);
        for (var i = from; i < to; i++)
            L[i] = Offset[i];
    }
}

/// <summary>
/// OscillatorNode: sine, square, sawtooth, triangle с фазой и знаком по ряду Фурье спецификации (все начинаются с 0,
/// пик 1). Скачки пилы и квадрата сглажены PolyBLEP, изломы треугольника — PolyBLAMP: алиасинг подавлен без
/// таблиц волн, как у браузеров. Частота и detune — a-rate. Depth не null — это колебание для параметра (lfo).
/// </summary>
internal sealed class OscNode : SourceNode
{
    private readonly Wave _wave;
    private double _phase;

    public OscNode(Wave wave, float frequency, float detune, float? depth = null)
    {
        _wave = wave;
        Frequency = new Param(frequency, -AudioFormat.Nyquist, AudioFormat.Nyquist);
        Detune = new Param(detune, -153600f, 153600f);
        Depth = depth is { } value ? new Param(value, -float.MaxValue, float.MaxValue) : null;
        _params = Depth is null ? [Frequency, Detune] : [Frequency, Detune, Depth];
    }

    private readonly Param[] _params;

    public readonly Param Frequency;
    public readonly Param Detune;
    public readonly Param? Depth;

    public override Param? GetParam(ParamId id) => id switch
    {
        ParamId.Frequency => Frequency,
        ParamId.Detune => Detune,
        ParamId.Gain => Depth,
        _ => null,
    };

    public override ReadOnlySpan<Param> Params => _params;

    public override void Process(long frame)
    {
        if (!Active(frame, out var from, out var to))
        {
            Channels = 0;
            return;
        }
        Channels = 1;
        var output = L;
        if (from > 0)
            Array.Clear(output, 0, from);
        if (to < Q)
            Array.Clear(output, to, Q - to);
        var steady = Frequency.Steady && Detune.Steady;
        var step = steady ? Step(Frequency.Constant, Detune.Constant) : 0;
        var phase = _phase;
        for (var i = from; i < to; i++)
        {
            if (!steady)
                step = Step(Frequency.Values[i], Detune[i]);
            output[i] = (float)Sample(_wave, phase, Math.Min(Math.Abs(step), 0.5));
            phase += step;
            phase -= Math.Floor(phase);
        }
        _phase = phase;
        if (Depth is { } depth)
        {
            if (depth.Steady)
                Mix.Scale(output, output, depth.Constant);
            else
                Mix.Multiply(output, depth.Values, output);
        }
    }

    private static double Step(float frequency, float detune) =>
        (detune == 0f ? frequency : frequency * Math.Pow(2, detune / 1200.0)) / AudioFormat.SampleRate;

    /// <summary>Отсчёт волны в фазе phase ∈ [0, 1) при шаге dt (доля периода на кадр).</summary>
    public static double Sample(Wave wave, double phase, double dt)
    {
        switch (wave)
        {
            case Wave.Square:
            {
                var value = phase < 0.5 ? 1.0 : -1.0;
                var half = phase + 0.5;
                half -= Math.Floor(half);
                return value + Blep(phase, dt) - Blep(half, dt);
            }
            case Wave.Sawtooth:
            {
                var shifted = phase + 0.5;
                shifted -= Math.Floor(shifted);
                return 2 * shifted - 1 - Blep(shifted, dt);
            }
            case Wave.Triangle:
            {
                var corner = phase + 0.75;
                corner -= Math.Floor(corner);
                var value = 4 * Math.Abs(corner - 0.5) - 1;
                var trough = corner + 0.5;
                trough -= Math.Floor(trough);
                return value + 4 * dt * (Blamp(trough, dt) - Blamp(corner, dt));
            }
            default:
                return Math.Sin(2 * Math.PI * phase);
        }
    }

    /// <summary>Поправка PolyBLEP к скачку на половину высоты 1 в фазе 0.</summary>
    private static double Blep(double t, double dt)
    {
        if (dt <= 0)
            return 0;
        if (t < dt)
        {
            var x = t / dt;
            return x + x - x * x - 1;
        }
        if (t > 1 - dt)
        {
            var x = (t - 1) / dt;
            return x * x + x + x + 1;
        }
        return 0;
    }

    /// <summary>Поправка PolyBLAMP к излому в фазе 0 (интеграл BLEP).</summary>
    private static double Blamp(double t, double dt)
    {
        if (dt <= 0)
            return 0;
        if (t < dt)
        {
            var x = 1 - t / dt;
            return x * x * x / 3;
        }
        if (t > 1 - dt)
        {
            var x = (t - 1) / dt + 1;
            return x * x * x / 3;
        }
        return 0;
    }
}

/// <summary>
/// AudioBufferSourceNode: запись с петлёй (вся запись), смещением и скоростью. playbackRate и detune — k-rate, как в
/// спецификации; между отсчётами — линейная интерполяция (как в Chromium).
/// </summary>
internal sealed class BufferNode : SourceNode
{
    private readonly SampleBuffer _buffer;
    private readonly bool _loop;
    private double _position;
    private bool _started;
    private readonly double _offset;

    public BufferNode(SampleBuffer buffer, bool loop, double offset, float rate, float detune)
    {
        _buffer = buffer;
        _loop = loop;
        var duration = buffer.Duration;
        _offset = loop && duration > 0 ? (offset % duration + duration) % duration : Math.Max(0, offset);
        Rate = new Param(rate, -float.MaxValue, float.MaxValue, kRate: true);
        Detune = new Param(detune, -float.MaxValue, float.MaxValue, kRate: true);
        _params = [Rate, Detune];
    }

    private readonly Param[] _params;

    public readonly Param Rate;
    public readonly Param Detune;

    public SampleBuffer Buffer => _buffer;

    public override Param? GetParam(ParamId id) => id switch
    {
        ParamId.Rate => Rate,
        ParamId.Detune => Detune,
        _ => null,
    };

    public override ReadOnlySpan<Param> Params => _params;

    public override void Process(long frame)
    {
        if (Ended || _buffer.Length == 0 || !Active(frame, out var from, out var to))
        {
            Channels = 0;
            return;
        }
        if (!_started)
        {
            _started = true;
            _position = _offset * _buffer.SampleRate;
        }
        var stereo = _buffer.ChannelCount >= 2;
        Channels = stereo ? 2 : 1;
        Array.Clear(L);
        if (stereo)
            Array.Clear(R);
        var rate = Rate.Constant * (Detune.Constant == 0f ? 1.0 : Math.Pow(2, Detune.Constant / 1200.0));
        var step = rate * _buffer.SampleRate / AudioFormat.SampleRate;
        var left = _buffer.Data(0);
        var right = stereo ? _buffer.Data(1) : left;
        var length = _buffer.Length;
        var position = _position;
        for (var i = from; i < to; i++)
        {
            if (!_loop && (position >= length || position < 0))
            {
                Ended = true;
                break;
            }
            var index = (int)position;
            var fraction = (float)(position - index);
            var next = index + 1;
            if (next >= length)
                next = _loop ? 0 : -1;
            if (fraction == 0f)
            {
                L[i] = left[index];
                if (stereo)
                    R[i] = right[index];
            }
            else
            {
                var l1 = next < 0 ? 0f : left[next];
                L[i] = left[index] + (l1 - left[index]) * fraction;
                if (stereo)
                {
                    var r1 = next < 0 ? 0f : right[next];
                    R[i] = right[index] + (r1 - right[index]) * fraction;
                }
            }
            position += step;
            if (_loop)
            {
                if (position >= length)
                    position -= length * Math.Floor(position / length);
                else if (position < 0)
                    position += length * Math.Ceiling(-position / length);
            }
        }
        _position = position;
    }
}

/// <summary>
/// BiquadFilterNode: коэффициенты по формулам спецификации (<see cref="Biquad"/>), состояние в double, по каналу.
/// Параметры a-rate: при автоматизации коэффициенты пересчитываются на каждый кадр.
/// </summary>
internal sealed class FilterNode : Node
{
    private readonly FilterType _type;
    private readonly double[] _state = new double[4];
    private Biquad.Coefficients _coefficients;
    private float _lastFrequency = float.NaN, _lastQ, _lastGain, _lastDetune;
    private int _tailChannels;

    public FilterNode(FilterType type, float frequency, float q, float gain, float detune)
    {
        _type = type;
        Frequency = new Param(frequency, 0f, AudioFormat.Nyquist);
        Resonance = new Param(q, -float.MaxValue, float.MaxValue);
        Gain = new Param(gain, -float.MaxValue, 1541f);
        Detune = new Param(detune, -153600f, 153600f);
        _params = [Frequency, Resonance, Gain, Detune];
    }

    private readonly Param[] _params;

    public readonly Param Frequency;
    public readonly Param Resonance;
    public readonly Param Gain;
    public readonly Param Detune;

    public override Param? GetParam(ParamId id) => id switch
    {
        ParamId.Frequency => Frequency,
        ParamId.Q => Resonance,
        ParamId.Gain => Gain,
        ParamId.Detune => Detune,
        _ => null,
    };

    public override ReadOnlySpan<Param> Params => _params;

    public override void Process(long frame)
    {
        var channels = Input(out var l, out var r);
        if (channels == 0)
        {
            if (_tailChannels == 0 || Quiet())
            {
                Array.Clear(_state);
                _tailChannels = 0;
                Channels = 0;
                return;
            }
            channels = _tailChannels;
            Array.Clear(L);
            Array.Clear(R);
            l = L;
            r = channels == 2 ? R : L;
            Run(l, L, 0, zeroInput: true);
            if (channels == 2)
                Run(r, R, 2, zeroInput: true);
            Channels = channels;
            return;
        }
        if (channels > _tailChannels)
        {
            // Моно → стерео: правый канал продолжает состояние левого.
            if (_tailChannels == 1)
            {
                _state[2] = _state[0];
                _state[3] = _state[1];
            }
            _tailChannels = channels;
        }
        if (_tailChannels == 2 && channels == 1)
            channels = 2;
        Channels = channels;
        Run(l, L, 0, zeroInput: false);
        if (channels == 2)
            Run(r, R, 2, zeroInput: false);
    }

    private bool Quiet() => Math.Abs(_state[0]) + Math.Abs(_state[1]) + Math.Abs(_state[2]) + Math.Abs(_state[3]) < 1e-10;

    private void Run(float[] input, float[] output, int at, bool zeroInput)
    {
        double s1 = _state[at], s2 = _state[at + 1];
        if (Frequency.Steady && Resonance.Steady && Gain.Steady && Detune.Steady)
        {
            Update(Frequency.Constant, Resonance.Constant, Gain.Constant, Detune.Constant);
            var c = _coefficients;
            for (var i = 0; i < Q; i++)
            {
                double x = zeroInput ? 0 : input[i];
                var y = c.B0 * x + s1;
                s1 = c.B1 * x - c.A1 * y + s2;
                s2 = c.B2 * x - c.A2 * y;
                output[i] = (float)y;
            }
        }
        else
        {
            for (var i = 0; i < Q; i++)
            {
                Update(Frequency[i], Resonance[i], Gain[i], Detune[i]);
                var c = _coefficients;
                double x = zeroInput ? 0 : input[i];
                var y = c.B0 * x + s1;
                s1 = c.B1 * x - c.A1 * y + s2;
                s2 = c.B2 * x - c.A2 * y;
                output[i] = (float)y;
            }
        }
        _state[at] = s1;
        _state[at + 1] = s2;
    }

    private void Update(float frequency, float q, float gain, float detune)
    {
        if (frequency == _lastFrequency && q == _lastQ && gain == _lastGain && detune == _lastDetune)
            return;
        (_lastFrequency, _lastQ, _lastGain, _lastDetune) = (frequency, q, gain, detune);
        var hz = detune == 0f ? frequency : frequency * Math.Pow(2, detune / 1200.0);
        _coefficients = Biquad.Compute(_type, hz, q, gain, AudioFormat.SampleRate);
    }
}

/// <summary>
/// DelayNode: кольцевой буфер на канал, задержка a-rate с линейной интерполяцией. В петле графа задержка не меньше
/// кванта: выход читается до обработки остальных узлов, вход пишется после.
/// </summary>
internal sealed class DelayNode : Node
{
    private readonly float[] _left;
    private readonly float[] _right;
    private readonly int _size;
    private readonly int _maxFrames;
    private long _written;
    private long _lastInput = long.MinValue / 2;
    private long _frame;
    private int _channels;

    public DelayNode(float time, float maxTime)
    {
        _maxFrames = (int)Math.Ceiling(maxTime * AudioFormat.SampleRate);
        _size = _maxFrames + 2 * Q + 2;
        _left = new float[_size];
        _right = new float[_size];
        DelayTime = new Param(time, 0f, maxTime);
    }

    public readonly Param DelayTime;

    /// <summary>Задержка разрывает петлю графа.</summary>
    public bool InCycle;

    public override Param? GetParam(ParamId id) => id == ParamId.DelayTime ? DelayTime : null;

    public override ReadOnlySpan<Param> Params => _params ??= [DelayTime];

    private Param[]? _params;

    public override long TailFrames => _maxFrames + Q;

    public override void Process(long frame)
    {
        _frame = frame;
        if (!InCycle)
            Write(frame);
        Read(frame);
    }

    public override void AfterQuantum()
    {
        if (InCycle)
            Write(_frame);
    }

    private void Write(long frame)
    {
        var channels = Input(out var l, out var r);
        var start = (int)(_written % _size);
        if (channels > 0)
        {
            _lastInput = frame;
            _channels = Math.Max(_channels, channels);
        }
        for (var i = 0; i < Q; i++)
        {
            var at = start + i;
            if (at >= _size)
                at -= _size;
            _left[at] = channels > 0 ? l[i] : 0f;
            _right[at] = channels > 0 ? r[i] : 0f;
        }
        _written += Q;
    }

    private void Read(long frame)
    {
        if (frame - _lastInput > _maxFrames + 2 * Q)
        {
            _channels = 0;
            Channels = 0;
            return;
        }
        // Номер кадра в кольце, который соответствует кадру frame кванта (вход этого кванта уже записан, если не в петле).
        var basis = InCycle ? _written : _written - Q;
        var min = InCycle ? Q : 0;
        var stereo = _channels == 2;
        for (var i = 0; i < Q; i++)
        {
            var delay = Math.Clamp(DelayTime[i] * (double)AudioFormat.SampleRate, min, _maxFrames);
            // Время во float не попадает в кадр точно (0,01 с — 479,99998): почти целое — целое, без размытия.
            var nearest = Math.Round(delay);
            if (Math.Abs(delay - nearest) < 1e-3)
                delay = nearest;
            var position = basis + i - delay;
            var whole = (long)Math.Floor(position);
            var fraction = (float)(position - whole);
            var a = (int)(((whole % _size) + _size) % _size);
            var b = a + 1 == _size ? 0 : a + 1;
            L[i] = _left[a] + (_left[b] - _left[a]) * fraction;
            if (stereo)
                R[i] = _right[a] + (_right[b] - _right[a]) * fraction;
        }
        Channels = _channels == 0 ? 1 : _channels;
    }
}

/// <summary>WaveShaperNode без передискретизации: кривая на [−1, 1] с линейной интерполяцией (спецификация).</summary>
internal sealed class ShapeNode(float[] curve) : Node
{
    private readonly float[] _curve = curve;

    public override void Process(long frame)
    {
        var channels = Input(out var l, out var r);
        Channels = channels;
        if (channels == 0)
            return;
        Apply(l, L);
        if (channels == 2)
            Apply(r, R);
    }

    private void Apply(float[] input, float[] output)
    {
        var curve = _curve;
        var last = curve.Length - 1;
        for (var i = 0; i < Q; i++)
        {
            var v = last * (input[i] + 1f) * 0.5f;
            if (!(v > 0f))
                output[i] = curve[0];
            else if (v >= last)
                output[i] = curve[last];
            else
            {
                var k = (int)v;
                var f = v - k;
                output[i] = (1f - f) * curve[k] + f * curve[k + 1];
            }
        }
    }
}

/// <summary>StereoPannerNode: равная мощность по формулам спецификации для моно- и стереовхода; pan — a-rate.</summary>
internal sealed class PanNode(float pan) : Node
{
    public readonly Param Pan = new(pan, -1f, 1f);

    public override Param? GetParam(ParamId id) => id == ParamId.Pan ? Pan : null;

    public override ReadOnlySpan<Param> Params => _params ??= [Pan];

    private Param[]? _params;

    public override void Process(long frame)
    {
        var channels = Input(out var l, out var r);
        if (channels == 0)
        {
            Channels = 0;
            return;
        }
        Channels = 2;
        if (Pan.Steady)
        {
            var (gl, gr) = Gains(Pan.Constant, channels);
            var pan = Pan.Constant;
            if (channels == 1)
            {
                Mix.Scale(l, R, gr);
                Mix.Scale(l, L, gl);
            }
            else if (pan <= 0f)
            {
                for (var i = 0; i < Q; i++)
                {
                    var inL = l[i];
                    var inR = r[i];
                    L[i] = inL + inR * gl;
                    R[i] = inR * gr;
                }
            }
            else
            {
                for (var i = 0; i < Q; i++)
                {
                    var inL = l[i];
                    var inR = r[i];
                    L[i] = inL * gl;
                    R[i] = inR + inL * gr;
                }
            }
            return;
        }
        for (var i = 0; i < Q; i++)
        {
            var pan = Pan.Values[i];
            var (gl, gr) = Gains(pan, channels);
            var inL = l[i];
            var inR = r[i];
            if (channels == 1)
            {
                L[i] = inL * gl;
                R[i] = inL * gr;
            }
            else if (pan <= 0f)
            {
                L[i] = inL + inR * gl;
                R[i] = inR * gr;
            }
            else
            {
                L[i] = inL * gl;
                R[i] = inR + inL * gr;
            }
        }
    }

    /// <summary>Усиления левого и правого по спецификации.</summary>
    public static (float Left, float Right) Gains(float pan, int channels)
    {
        double x = channels == 1 ? (pan + 1) / 2 : pan <= 0 ? pan + 1 : pan;
        return ((float)Math.Cos(x * Math.PI / 2), (float)Math.Sin(x * Math.PI / 2));
    }
}
