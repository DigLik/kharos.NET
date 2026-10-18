namespace Kharos.Audio.Synthesis;

/// <summary>
/// Синтез записей в сэмплах — перенос packages/client/src/audio/synth.ts и mods/base/lib/dsp.luau: звуки игры
/// считаются кодом один раз при загрузке. Счёт — в double, хранение — float, как Float32Array в TS: результат совпадает
/// с TS до округления exp/sin библиотеки.
/// </summary>
public static class Synth
{
    /// <summary>Средняя громкость петли шума.</summary>
    public const double NoiseLevel = 0.25;

    /// <summary>Случайные числа [0, 1) с зерном: xorshift32, как random в synth.ts.</summary>
    public static Func<double> Random(double seed)
    {
        var product = seed * 2654435761.0;
        product -= Math.Floor(product / 4294967296.0) * 4294967296.0;
        var state = (uint)product;
        if (state == 0)
            state = 1;
        return () =>
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state / 4294967296.0;
        };
    }

    /// <summary>Фильтр нижних частот с одним полюсом.</summary>
    public static Func<double, double> Lowpass(double rate, double cutoff)
    {
        var k = 1 - Math.Exp(-2 * Math.PI * cutoff / rate);
        var last = 0.0;
        return input => last += (input - last) * k;
    }

    /// <summary>Фильтр нижних частот с меняющимся срезом.</summary>
    public static Func<double, double> Lowpass(double rate, Func<double> cutoff)
    {
        var last = 0.0;
        return input => last += (input - last) * (1 - Math.Exp(-2 * Math.PI * cutoff() / rate));
    }

    /// <summary>Фильтр верхних частот: вход минус его низы.</summary>
    public static Func<double, double> Highpass(double rate, double cutoff)
    {
        var low = Lowpass(rate, cutoff);
        return input => input - low(input);
    }

    /// <summary>Резонатор: полоса шириной frequency / q, на ней усиление — единица.</summary>
    public static Func<double, double> Bandpass(double rate, double frequency, double q)
    {
        var w = 2 * Math.PI * frequency / rate;
        var alpha = Math.Sin(w) / (2 * q);
        var a0 = 1 + alpha;
        var a1 = -2 * Math.Cos(w) / a0;
        var a2 = (1 - alpha) / a0;
        var b = alpha / a0;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        return input =>
        {
            var output = b * (input - x2) - a1 * y1 - a2 * y2;
            x2 = x1;
            x1 = input;
            y2 = y1;
            y1 = output;
            return output;
        };
    }

    /// <summary>Затухание: за time секунд в e раз.</summary>
    public static double Decay(double t, double time) => Math.Exp(-t / time);

    /// <summary>Атака за time секунд.</summary>
    public static double Attack(double t, double time) => Math.Min(1, t / time);

    /// <summary>Удары раз в period со сдвигом offset, каждый гаснет за time.</summary>
    public static double Pulse(double t, double period, double time, double offset = 0)
    {
        var local = ((t - offset) % period + period) % period;
        return Math.Exp(-local / time);
    }

    /// <summary>
    /// Звук длиной seconds: sample(t, noise) — значение в миг t; noise — белый шум варианта. Конец гаснет за 10 мс, пик —
    /// единица.
    /// </summary>
    public static float[] Render(double rate, double seconds, int variant, Func<double, Func<double>, double> sample)
    {
        var next = Random(variant + 1);
        double Noise() => next() * 2 - 1;
        var data = new float[(int)Math.Ceiling(rate * seconds)];
        for (var i = 0; i < data.Length; i++)
            data[i] = (float)sample(i / rate, Noise);
        var tail = Math.Min(data.Length, (int)Math.Round(rate * 0.01, MidpointRounding.AwayFromZero));
        for (var i = 0; i < tail; i++)
            data[data.Length - 1 - i] = (float)(data[data.Length - 1 - i] * ((double)i / tail));
        Normalize(data);
        return data;
    }

    /// <summary>Петля длиной seconds: конец плавно переходит в начало (dsp.loop).</summary>
    public static float[] Loop(double rate, double seconds, Func<double, Func<double>, double> sample)
    {
        var fade = (int)Math.Round(rate * 0.25, MidpointRounding.AwayFromZero);
        var length = (int)Math.Ceiling(rate * seconds);
        var next = Random(7);
        double Noise() => next() * 2 - 1;
        var raw = new float[length + fade];
        for (var i = 0; i < raw.Length; i++)
            raw[i] = (float)sample(i / rate, Noise);
        var data = raw[..length];
        for (var i = 0; i < fade; i++)
        {
            var k = (double)i / fade;
            data[i] = (float)(data[i] * k + raw[length + i] * (1 - k));
        }
        Normalize(data);
        return data;
    }

    /// <summary>Петля шума без шва: белый — шипение, розовый — шелест, бурый — гул (noiseLoop).</summary>
    public static float[] NoiseLoop(double rate, double seconds, double seed, Graph.NoiseColor color)
    {
        var next = Random(seed);
        var length = (int)Math.Ceiling(rate * seconds);
        var fade = Math.Min(length, (int)Math.Round(rate * 0.25, MidpointRounding.AwayFromZero));
        var raw = new float[length + fade];
        double pink0 = 0, pink1 = 0, pink2 = 0, brown = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var white = next() * 2 - 1;
            switch (color)
            {
                case Graph.NoiseColor.White:
                    raw[i] = (float)white;
                    break;
                case Graph.NoiseColor.Pink:
                    pink0 = 0.99765 * pink0 + white * 0.099046;
                    pink1 = 0.963 * pink1 + white * 0.2965164;
                    pink2 = 0.57 * pink2 + white * 1.0526913;
                    raw[i] = (float)(pink0 + pink1 + pink2 + white * 0.1848);
                    break;
                default:
                    brown = (brown + white * 0.02) / 1.02;
                    raw[i] = (float)brown;
                    break;
            }
        }
        var data = raw[..length];
        for (var i = 0; i < fade; i++)
        {
            var k = (double)i / fade * Math.PI / 2;
            data[i] = (float)(raw[i] * Math.Sin(k) + raw[length + i] * Math.Cos(k));
        }
        double mean = 0;
        foreach (var value in data)
            mean += value / (double)length;
        double energy = 0;
        for (var i = 0; i < length; i++)
        {
            data[i] = (float)(data[i] - mean);
            energy += data[i] * (double)data[i];
        }
        var power = energy / length;
        var scale = NoiseLevel / Math.Sqrt(power == 0 || double.IsNaN(power) ? 1 : power);
        for (var i = 0; i < length; i++)
            data[i] = (float)(data[i] * scale);
        return data;
    }

    /// <summary>Отклик для эха: стерео шум, гаснущий за seconds в тысячу раз и темнеющий к хвосту (reverbImpulse).</summary>
    public static float[][] ReverbImpulse(double rate, double seconds, double seed)
    {
        var channels = new float[2][];
        for (var channel = 0; channel < 2; channel++)
        {
            var next = Random(seed * 2 + channel + 1);
            var t = 0.0;
            var tone = Lowpass(rate, () => 500 + 6000 * Decay(t, seconds * 0.2));
            var data = new float[(int)Math.Ceiling(rate * seconds)];
            const double delay = 0.02;
            for (var i = 0; i < data.Length; i++)
            {
                t = i / rate;
                var sample = tone(next() * 2 - 1);
                data[i] = t < delay ? 0f : (float)(sample * Math.Exp(-6.9 * (t - delay) / seconds));
            }
            Normalize(data);
            channels[channel] = data;
        }
        return channels;
    }

    /// <summary>Плавное число от 0 до 1, которое бродит: за единицу аргумента — шаг к новому значению.</summary>
    public static Func<double, double> Wanderer(double seed)
    {
        double Value(double step) => Random(seed * 7919 + step + 1)();
        return t =>
        {
            var step = Math.Floor(t);
            var f = t - step;
            var a = Value(step);
            return a + (Value(step + 1) - a) * f * f * (3 - 2 * f);
        };
    }

    private static void Normalize(float[] data)
    {
        double peak = 0;
        foreach (var value in data)
            peak = Math.Max(peak, Math.Abs(value));
        if (peak > 0)
            for (var i = 0; i < data.Length; i++)
                data[i] = (float)(data[i] / peak);
    }
}
