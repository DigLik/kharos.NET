using Kharos.Mod.Sdk.Client;

namespace Base.Client.Sounds;

/// <summary>
/// Синтез звука в сэмплах — перенос mods/base/lib/dsp.luau (а он — synth.ts): звуки игры считаются кодом один раз при
/// загрузке. Счёт в double, хранение в float, как buffer.writef32. Мод внутри wasm не зависит от Kharos.Audio, поэтому
/// всё нужное здесь.
/// </summary>
internal static class Dsp
{
    /// <summary>Средняя громкость петли шума: громкость слоя задаёт тот, кто её играет.</summary>
    public const double NoiseLevel = 0.25;

    private const double TwoPi = 2 * Math.PI;

    /// <summary>Случайные числа [0, 1) с зерном: один вариант звука всегда одинаков. xorshift32, как в TS.</summary>
    public static Func<double> Random(double seed)
    {
        var product = Math.Floor(seed * 2654435761.0);
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

    /// <summary>Фильтр нижних частот с одним полюсом, срез постоянный.</summary>
    public static Func<double, double> Lowpass(double rate, double cutoff)
    {
        var k = 1 - Math.Exp(-TwoPi * cutoff / rate);
        var last = 0.0;
        return input => last += (input - last) * k;
    }

    /// <summary>Фильтр нижних частот: срез меняется на ходу.</summary>
    public static Func<double, double> Lowpass(double rate, Func<double> cutoff)
    {
        var last = 0.0;
        return input => last += (input - last) * (1 - Math.Exp(-TwoPi * cutoff() / rate));
    }

    /// <summary>Фильтр верхних частот: вход минус его низы.</summary>
    public static Func<double, double> Highpass(double rate, double cutoff)
    {
        var low = Lowpass(rate, cutoff);
        return input => input - low(input);
    }

    /// <summary>Резонатор: полоса шириной frequency / q вокруг frequency, на ней усиление — единица.</summary>
    public static Func<double, double> Bandpass(double rate, double frequency, double q)
    {
        var w = TwoPi * frequency / rate;
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

    /// <summary>Затухание: за time секунд звук стихает в e раз.</summary>
    public static double Decay(double t, double time) => Math.Exp(-t / time);

    /// <summary>Быстрая атака за time секунд, чтобы не щёлкало начало.</summary>
    public static double Attack(double t, double time) => Math.Min(1, t / time);

    /// <summary>Удары раз в period секунд со сдвигом offset: огибающая каждого гаснет за time.</summary>
    public static double Pulse(double t, double period, double time, double offset = 0)
    {
        var local = ((t - offset) % period + period) % period;
        return Math.Exp(-local / time);
    }

    /// <summary>Знак, как Math.sign.</summary>
    public static double Signum(double value) => value > 0 ? 1 : value < 0 ? -1 : 0;

    /// <summary>Округление как math.round: половина — от нуля.</summary>
    public static int RoundHalf(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static void Normalize(float[] data)
    {
        double peak = 0;
        foreach (var value in data)
            peak = Math.Max(peak, Math.Abs(value));
        if (peak > 0)
            for (var i = 0; i < data.Length; i++)
                data[i] = (float)(data[i] / peak);
    }

    /// <summary>
    /// Звук длиной seconds: sample(t, noise) — значение в миг t; noise — белый шум этого варианта. Конец гаснет без
    /// щелчка, пик — единица: громкость задаёт тот, кто играет.
    /// </summary>
    public static float[] Render(double rate, double seconds, int variant, Func<double, Func<double>, double> sample)
    {
        var next = Random(variant + 1);
        double Noise() => next() * 2 - 1;
        var length = (int)Math.Ceiling(rate * seconds);
        var data = new float[length];
        for (var i = 0; i < length; i++)
            data[i] = (float)sample(i / rate, Noise);
        var tail = Math.Min(length, RoundHalf(rate * 0.01));
        for (var i = 0; i < tail; i++)
        {
            var at = length - 1 - i;
            data[at] = (float)((double)data[at] * i / tail);
        }
        Normalize(data);
        return data;
    }

    /// <summary>Петля длиной seconds: как Render, но конец плавно переходит в начало без щелчка на стыке.</summary>
    public static float[] Loop(double rate, double seconds, Func<double, Func<double>, double> sample)
    {
        var fade = RoundHalf(rate * 0.25);
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

    /// <summary>Петля шума длиной seconds без шва: белый — шипение, розовый — шелест, бурый — гул.</summary>
    public static float[] NoiseLoop(double rate, double seconds, double seed, NoiseColor color)
    {
        var next = Random(seed);
        var length = (int)Math.Ceiling(rate * seconds);
        var fade = Math.Min(length, RoundHalf(rate * 0.25));
        var raw = new float[length + fade];
        double pink0 = 0, pink1 = 0, pink2 = 0, brown = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var white = next() * 2 - 1;
            double value;
            switch (color)
            {
                case NoiseColor.White:
                    value = white;
                    break;
                case NoiseColor.Pink:
                    pink0 = 0.99765 * pink0 + white * 0.099046;
                    pink1 = 0.963 * pink1 + white * 0.2965164;
                    pink2 = 0.57 * pink2 + white * 1.0526913;
                    value = pink0 + pink1 + pink2 + white * 0.1848;
                    break;
                default:
                    brown = (brown + white * 0.02) / 1.02;
                    value = brown;
                    break;
            }
            raw[i] = (float)value;
        }
        // Шов: начало петли — её продолжение за концом, которое плавно уступает собственному началу.
        var data = raw[..length];
        for (var i = 0; i < fade; i++)
        {
            var k = (double)i / fade * Math.PI / 2;
            data[i] = (float)(raw[i] * Math.Sin(k) + raw[length + i] * Math.Cos(k));
        }
        double mean = 0;
        for (var i = 0; i < length; i++)
            mean += data[i] / (double)length;
        double energy = 0;
        for (var i = 0; i < length; i++)
        {
            var value = data[i] - mean;
            data[i] = (float)value;
            energy += value * value;
        }
        var rms = Math.Sqrt(energy / length);
        var scale = NoiseLevel / (rms > 0 ? rms : 1);
        for (var i = 0; i < length; i++)
            data[i] = (float)(data[i] * scale);
        return data;
    }

    /// <summary>Отклик для эха: стерео шум, гаснущий за seconds в тысячу раз и темнеющий к хвосту — пустой простор.</summary>
    public static float[][] ReverbImpulse(double rate, double seconds, double seed)
    {
        var channels = new float[2][];
        for (var channel = 0; channel < 2; channel++)
        {
            var next = Random(seed * 2 + channel + 1);
            var t = 0.0;
            var tone = Lowpass(rate, () => 500 + 6000 * Math.Exp(-t / (seconds * 0.2)));
            var data = new float[(int)Math.Ceiling(rate * seconds)];
            // Первое отражение приходит не сразу: стены далеко.
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

    /// <summary>Плавное число от 0 до 1, которое бродит: за единицу аргумента — шаг к новому случайному значению.</summary>
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
}
