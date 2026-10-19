namespace Kharos.Audio.Tests;

/// <summary>Офлайн-рендер и замеры для проверок: без устройства, без звука.</summary>
internal static class Offline
{
    /// <summary>Моно-звук на панораме 0 (place): в каждый канал идёт cos(π/4).</summary>
    public const float Center = 0.70710677f;

    /// <summary>Движок без компрессора и с общей громкостью 1: путь линеен.</summary>
    public static AudioEngine Linear(uint seed = 1) => new(new AudioEngineOptions { Seed = seed, Master = 1f, Compressor = false });

    /// <summary>Амплитуда синусоиды частоты hz в отрезке (Гёрцель; в отрезке — целое число периодов).</summary>
    public static double Amplitude(ReadOnlySpan<float> x, double hz)
    {
        var w = 2 * Math.PI * hz / AudioFormat.SampleRate;
        var coefficient = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        foreach (var value in x)
        {
            var s0 = value + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        var power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
        return 2 * Math.Sqrt(Math.Max(power, 0)) / x.Length;
    }

    public static double Peak(ReadOnlySpan<float> x)
    {
        double peak = 0;
        foreach (var value in x)
            peak = Math.Max(peak, Math.Abs(value));
        return peak;
    }

    /// <summary>Переходы через ноль снизу вверх.</summary>
    public static int RisingCrossings(ReadOnlySpan<float> x)
    {
        var count = 0;
        for (var i = 1; i < x.Length; i++)
            if (x[i - 1] < 0 && x[i] >= 0)
                count++;
        return count;
    }

    /// <summary>
    /// Короткий путь для проверок и золотых записей банка: движок без компрессора (или с ним), setup заводит звуки,
    /// рендер seconds секунд. Без устройства, детерминированно при том же зерне.
    /// </summary>
    public static RenderedAudio Render(Action<AudioEngine> setup, double seconds, uint seed = 1, bool compressor = false)
    {
        var engine = new AudioEngine(new AudioEngineOptions { Seed = seed, Master = 1f, Compressor = compressor });
        setup(engine);
        return OfflineRender.Seconds(engine, seconds);
    }

    /// <summary>Левый канал следующих frames кадров.</summary>
    public static float[] Left(AudioEngine engine, int frames) => engine.Render(frames).Left;
}
