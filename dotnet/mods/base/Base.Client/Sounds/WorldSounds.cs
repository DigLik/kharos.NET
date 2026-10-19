using static Base.Client.Sounds.Dsp;
using static System.Math;

namespace Base.Client.Sounds;

/// <summary>
/// Звуки зданий и мира — перенос mods/base/sounds/world.luau (worldSynth.ts): стройка, разворачивание, выезд из
/// завода, перегруз сети, отсчёт и уведомления.
/// </summary>
internal static partial class WorldSounds
{
    private const double PI2 = 2 * PI;

    /// <summary>Металл: звонкие неровные обертоны от основы basis, гаснут за time; возвращает сэмпл от времени удара.</summary>
    internal static Func<double, double> Metal(double basis, double time, double seed)
    {
        var next = Random(seed);
        var ratios = new[] { 1, 2.32 + next() * 0.2, 3.89 + next() * 0.3, 5.61 + next() * 0.4, 7.13 + next() * 0.5 };
        return t =>
        {
            double output = 0;
            for (var i = 0; i < ratios.Length; i++)
                output += Sin(PI2 * basis * ratios[i] * t) * Decay(t, time / (1 + i * 0.6)) / (1 + i * 0.7);
            return output;
        };
    }

    /// <summary>Лязг: удар металла о металл в момент at секунд, громкостью level.</summary>
    internal static Func<double, double, double> ClangAt(double rate, double at, double basis, double level, double seed)
    {
        var ring = Metal(basis, 0.25, seed);
        var thud = Lowpass(rate, 220.0);
        return (t, n) => t < at ? thud(0) : ring(t - at) * level + thud(n * Decay(t - at, 0.02)) * level * 3;
    }

    /// <summary>Здание достроено: последний удар — затянутый болт, глухой удар и короткий звонкий аккорд металла.</summary>
    public static float[] Built(double rate, int variant)
    {
        var ratchet = Bandpass(rate, 3000, 4);
        var clang = ClangAt(rate, 0.22, 160, 0.8, variant + 1501);
        return Render(rate, 1, variant, (t, noise) =>
        {
            var n = noise();
            var clicks = t < 0.2 ? Pulse(t, 0.035, 0.003) : 0;
            return ratchet(n * clicks) * 3 + clang(t, n);
        });
    }

    /// <summary>MCV разворачивается: гидравлика воет и шипит, опоры встают с ударами, щёлкают замки.</summary>
    public static float[] Deploy(double rate, int variant)
    {
        var hiss = Bandpass(rate, 2200, 1);
        double[] moments = [0.5, 1.4, 2.2, 3];
        var clangs = new Func<double, double, double>[moments.Length];
        for (var i = 0; i < moments.Length; i++)
            clangs[i] = ClangAt(rate, moments[i], 90 + i * 25, 0.7, variant + 1601 + i);
        var phase = 0.0;
        return Render(rate, 3.4, variant, (t, noise) =>
        {
            var n = noise();
            phase += PI2 * (180 + 90 * Sin(PI * Min(1, t / 3))) / rate;
            var servo = (phase / PI2 % 1 * 2 - 1) * 0.12 * Attack(t, 0.1) * Min(1, (3.2 - t) / 0.2);
            var output = servo + hiss(n) * Pulse(t, 0.9, 0.25, 0.3) * 0.5;
            foreach (var clang in clangs)
                output += clang(t, n);
            return output;
        });
    }

    /// <summary>Машина выехала из завода: шипит воротина, лязгает и катится рольставня.</summary>
    public static float[] Rollout(double rate, int variant)
    {
        var hiss = Highpass(rate, 2000);
        var rattle = Bandpass(rate, 1100, 3);
        var clang = ClangAt(rate, 0.5, 130, 0.5, variant + 1701);
        return Render(rate, 0.9, variant, (t, noise) =>
        {
            var n = noise();
            var shutter = t < 0.5 ? Pulse(t, 0.045, 0.006) * Attack(t, 0.05) : 0;
            return hiss(n) * Decay(t, 0.08) * 0.8 + rattle(n * shutter) * 4 + clang(t, n);
        });
    }

    /// <summary>Сеть перегружена: свет мигает — гул проседает и рвётся.</summary>
    public static float[] Brownout(double rate, int variant)
    {
        var phase = 0.0;
        return Render(rate, 1.2, variant, (t, _) =>
        {
            phase += PI2 * (100 - 40 * Min(1, t / 0.8)) * (1 + variant * 0.015) / rate;
            var flicker = t < 0.6 ? 0.4 + 0.6 * (Sin(PI2 * 9 * t) > -0.2 ? 1 : 0) : 1;
            return (Sin(phase) + Signum(Sin(phase)) * 0.25 + Sin(phase * 3) * 0.2) * flicker * Decay(t, 0.45) * Attack(t, 0.01);
        });
    }

    /// <summary>Тон-колокольчик для уведомлений: ноты по очереди через gap, каждая звенит ring секунд.</summary>
    public static float[] Chime(double rate, int variant, double[] notes, double gap, double ring) =>
        Render(rate, gap * (notes.Length - 1) + ring * 3, variant, (t, _) =>
        {
            double output = 0;
            for (var i = 0; i < notes.Length; i++)
            {
                var localT = t - i * gap;
                if (localT >= 0)
                {
                    var frequency = notes[i] * (1 + variant * 0.003);
                    output += (Sin(PI2 * frequency * localT) + Sin(PI2 * frequency * 2.76 * localT) * 0.2 * Decay(localT, ring / 3)) * Attack(localT, 0.004) * Decay(localT, ring);
                }
            }
            return output;
        });

    /// <summary>Отсчёт перед ядерным ударом: короткий высокий писк.</summary>
    public static float[] Beep(double rate, int variant)
    {
        var phase = 0.0;
        return Render(rate, 0.12, variant, (t, _) =>
        {
            phase += PI2 * 1760 * (1 + variant * 0.005) / rate;
            return (Sin(phase) * 0.8 + Signum(Sin(phase)) * 0.2) * Attack(t, 0.003) * Min(1, (0.12 - t) / 0.02);
        });
    }
}
