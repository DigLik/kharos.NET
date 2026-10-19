using Kharos.Audio.Synthesis;

namespace Kharos.Audio;

/// <summary>
/// Звуки оболочки (меню, настройки, чат): щелчок, наведение, отказ, вход в раздел и тик ползунка. Синтезируются здесь,
/// в движке, — оболочке не нужен мод — по тем же формулам, что blip в packages/client/src/audio/synth.ts. Лежат в банке
/// движка под именами «shell:…» на шине interface, по <see cref="Variants"/> вариантов. Громкости — как у меню в TS
/// (audio/music.ts): щелчок 0,35, наведение 0,12.
/// </summary>
public static class ShellSounds
{
    public const string Click = "shell:click";
    public const string Hover = "shell:hover";
    public const string Open = "shell:open";
    public const string Deny = "shell:deny";

    /// <summary>Сколько вариантов у звука (SOUND_VARIANTS в TS).</summary>
    public const int Variants = 3;

    /// <summary>Громкость щелчка относительно шины.</summary>
    public const float ClickLevel = 0.35f;

    /// <summary>Громкость наведения.</summary>
    public const float HoverLevel = 0.12f;

    /// <summary>Тик ползунка не чаще, секунды.</summary>
    public const double SlideGap = 0.06;

    /// <summary>Высота тика у левого и правого края ползунка.</summary>
    public const float SlideLow = 0.7f, SlideHigh = 1.5f;

    public static readonly string[] Names = [Click, Hover, Open, Deny];

    /// <summary>Короткий тон: частота скользит from → to за length секунд, click — щелчок в начале (blip в synth.ts).</summary>
    public static float[] Blip(double rate, int variant, double from, double to, double length, double click = 0)
    {
        var phase = 0.0;
        var tick = Synth.Highpass(rate, 3000);
        return Synth.Render(rate, length, variant, (t, noise) =>
        {
            var frequency = from + (to - from) * Math.Min(1, t / length);
            phase += 2 * Math.PI * frequency * (1 + variant * 0.01) / rate;
            var tone = (Math.Sin(phase) * 0.85 + Math.Sign(Math.Sin(phase)) * 0.15) * Synth.Attack(t, 0.003) * Synth.Decay(t, length / 3);
            return tone + tick(noise()) * Synth.Decay(t, 0.004) * click;
        });
    }

    /// <summary>Вариант variant звука name; null — нет такого.</summary>
    public static float[]? Make(string name, int variant, double rate = AudioFormat.SampleRate) => name switch
    {
        Click => Blip(rate, variant, 1400, 1100, 0.05, 0.6),
        Hover => Blip(rate, variant, 2200, 2200, 0.025, 0.3),
        Open => Blip(rate, variant, 700, 1300, 0.09, 0.3),
        Deny => Blip(rate, variant, 220, 160, 0.16, 0.2),
        _ => null,
    };

    /// <summary>Завести звуки в банк движка (повторно — не пересчитывает).</summary>
    public static void Install(AudioEngine engine)
    {
        foreach (var name in Names)
        {
            if (engine.Bank.Contains(name))
                continue;
            var variants = new SampleBuffer[Variants];
            for (var v = 0; v < Variants; v++)
                variants[v] = new SampleBuffer(Make(name, v)!);
            engine.Bank.Add(name, Bus.Interface, variants);
        }
    }

    /// <summary>Нажатие кнопки меню: щелчок, у выключенной — отказ.</summary>
    public static void PlayClick(AudioEngine engine, bool disabled = false)
    {
        Install(engine);
        engine.Play(disabled ? Deny : Click, ClickLevel);
    }

    /// <summary>Курсор встал на кнопку.</summary>
    public static void PlayHover(AudioEngine engine)
    {
        Install(engine);
        engine.Play(Hover, HoverLevel);
    }

    /// <summary>Вход в раздел.</summary>
    public static void PlayOpen(AudioEngine engine)
    {
        Install(engine);
        engine.Play(Open, ClickLevel);
    }

    /// <summary>Тик ползунка: share — положение от 0 (слева, ниже) до 1 (справа, выше).</summary>
    public static void PlaySlide(AudioEngine engine, float share)
    {
        Install(engine);
        engine.Play(Hover, ClickLevel, 0f, SlideLow + (SlideHigh - SlideLow) * Math.Clamp(share, 0f, 1f));
    }
}
