using static Base.Client.Sounds.Dsp;
using static System.Math;

namespace Base.Client.Sounds;

/// <summary>
/// Звуки боя, интерфейса, фоновые петли и звуки дали — перенос mods/base/sounds/synth.luau (synth.ts). Каждый —
/// функция (rate, variant) → сэмплы; variant меняет зерно шума: варианты одного звука не звучат одинаково.
/// </summary>
internal static class SynthSounds
{
    private const double PI2 = 2 * PI;

    /// <summary>Винтовка: сухой щелчок и короткий хлопок.</summary>
    public static float[] Rifle(double rate, int variant)
    {
        var crack = Highpass(rate, 2500);
        var body = Lowpass(rate, 900.0);
        return Render(rate, 0.2, variant, (t, noise) =>
        {
            var n = noise();
            return crack(n) * Decay(t, 0.01) * 0.9 + body(n) * Decay(t, 0.05) * 1.6 + Sin(PI2 * 140 * t) * Decay(t, 0.025) * 0.35;
        });
    }

    /// <summary>Пулемёт: щелчок короче и легче винтовочного, чтобы очередь не сливалась в гул.</summary>
    public static float[] Machinegun(double rate, int variant)
    {
        var crack = Highpass(rate, 3000);
        var body = Lowpass(rate, 1400.0);
        return Render(rate, 0.12, variant, (t, noise) =>
        {
            var n = noise();
            return crack(n) * Decay(t, 0.007) * 0.7 + body(n) * Decay(t, 0.03) * 1.2 + Sin(PI2 * 180 * t) * Decay(t, 0.015) * 0.25;
        });
    }

    /// <summary>Пушка: удар, глухой бас, уходящий вниз, и раскат.</summary>
    public static float[] Cannon(double rate, int variant)
    {
        var crack = Highpass(rate, 1500);
        var rumble = Lowpass(rate, 320.0);
        var phase = 0.0;
        return Render(rate, 1.3, variant, (t, noise) =>
        {
            var n = noise();
            phase += PI2 * (40 + 60 * Decay(t, 0.12)) / rate;
            return crack(n) * Decay(t, 0.012) * 0.8 + Sin(phase) * Decay(t, 0.3) * 0.8 + rumble(n) * Decay(t, 0.4) * 2.4;
        });
    }

    /// <summary>Ракета: хлопок пуска и шипение уходящего двигателя.</summary>
    public static float[] Launcher(double rate, int variant)
    {
        var hiss = Highpass(rate, 500);
        var t = 0.0;
        // Шипение темнеет: ракета уходит.
        var band = Lowpass(rate, () => 1200 + 3000 * Decay(t, 0.15));
        var pop = Lowpass(rate, 600.0);
        return Render(rate, 0.7, variant, (time, noise) =>
        {
            t = time;
            var n = noise();
            var whoosh = band(hiss(n)) * Attack(t, 0.03) * Decay(t, 0.25) * 1.4;
            return whoosh + pop(n) * Decay(t, 0.03) * 1.5;
        });
    }

    /// <summary>Лазер: тон, падающий сверху вниз, с лёгким дребезгом.</summary>
    public static float[] Laser(double rate, int variant)
    {
        var phase = variant * 0.7;
        return Render(rate, 0.35, variant, (t, _) =>
        {
            var frequency = 500 + 1800 * Decay(t, 0.06) + Sin(PI2 * 30 * t) * 25;
            phase += PI2 * frequency / rate;
            var tone = Sin(phase) * 0.7 + Signum(Sin(phase)) * 0.15;
            return tone * Attack(t, 0.004) * Decay(t, 0.1) * 0.6;
        });
    }

    /// <summary>Разряд: треск — шум, который рвётся на вспышки, поверх гудения.</summary>
    public static float[] Arc(double rate, int variant)
    {
        var next = Random(variant + 101);
        var crackle = Highpass(rate, 1200);
        double gate = 0, untilAt = 0;
        return Render(rate, 0.5, variant, (t, noise) =>
        {
            if (t >= untilAt)
            {
                gate = next() < 0.55 ? 0.4 + next() * 0.6 : 0;
                untilAt = t + 0.003 + next() * 0.012;
            }
            var buzz = t * 110 % 1 * 2 - 1;
            return crackle(noise()) * gate * Decay(t, 0.18) * 1.1 + buzz * Decay(t, 0.12) * 0.18;
        });
    }

    /// <summary>Взрыв: удар и раскатистый шум, который глохнет, темнея. big — громадный: дольше и ниже.</summary>
    public static float[] Explosion(double rate, int variant, bool big)
    {
        var length = big ? 1.9 : 0.9;
        var fade = big ? 0.5 : 0.22;
        var t = 0.0;
        var body = Lowpass(rate, () => 160 + 2200 * Decay(t, big ? 0.18 : 0.1));
        var phase = 0.0;
        return Render(rate, length, variant, (time, noise) =>
        {
            t = time;
            phase += PI2 * (big ? 38 : 60) * (1 + Decay(time, 0.08)) / rate;
            var thump = Sin(phase) * Decay(time, big ? 0.25 : 0.12) * (big ? 0.9 : 0.6);
            return body(noise()) * Attack(time, 0.004) * Decay(time, fade) * 2.6 + thump;
        });
    }

    /// <summary>Сигнал: тоны по очереди, каждый — tone секунд с паузой gap; квадратная примесь делает его слышным в бою.</summary>
    public static float[] Signal(double rate, int variant, double[] tones, double tone, double gap)
    {
        var phase = 0.0;
        return Render(rate, tones.Length * (tone + gap), variant, (t, _) =>
        {
            var index = (int)Floor(t / (tone + gap));
            var localT = t - index * (tone + gap);
            if (index >= tones.Length || localT > tone)
                return 0;
            // Варианты чуть разнятся высотой: один и тот же сигнал подряд не звучит как запись.
            phase += PI2 * tones[index] * (1 + variant * 0.012) / rate;
            var wave = Sin(phase) * 0.8 + Signum(Sin(phase)) * 0.2;
            return wave * Attack(localT, 0.005) * Min(1, (tone - localT) / 0.02);
        });
    }

    /// <summary>Короткий тон интерфейса: частота скользит от from к to за length секунд, click — щелчок в начале.</summary>
    public static float[] Blip(double rate, int variant, double from, double to, double length, double click)
    {
        var phase = 0.0;
        var tick = Highpass(rate, 3000);
        return Render(rate, length, variant, (t, noise) =>
        {
            var frequency = from + (to - from) * Min(1, t / length);
            phase += PI2 * frequency * (1 + variant * 0.01) / rate;
            var tone = (Sin(phase) * 0.85 + Signum(Sin(phase)) * 0.15) * Attack(t, 0.003) * Decay(t, length / 3);
            return tone + tick(noise()) * Decay(t, 0.004) * click;
        });
    }

    /// <summary>Гул электростанции: сеть и её гармоники, чуть дышащие.</summary>
    public static float[] Hum(double rate)
    {
        var air = Lowpass(rate, 400.0);
        return Loop(rate, 4, (t, noise) =>
        {
            var breath = 1 + Sin(PI2 * 0.5 * t) * 0.15;
            return (Sin(PI2 * 50 * t) * 0.6 + Sin(PI2 * 100 * t) * 0.35 + Sin(PI2 * 150 * t) * 0.12) * breath + air(noise()) * 0.25;
        });
    }

    /// <summary>Цех и переработка: мерный стук пресса и звон металла поверх шипения.</summary>
    public static float[] Machinery(double rate)
    {
        var thud = Lowpass(rate, 300.0);
        var hiss = Highpass(rate, 2500);
        return Loop(rate, 2, (t, noise) =>
        {
            var n = noise();
            var press = thud(n) * Pulse(t, 0.5, 0.06) * 2.2;
            var ring = Sin(PI2 * 760 * t) * Pulse(t, 1, 0.12, 0.25) * 0.35;
            return press + ring + hiss(n) * 0.08 + Sin(PI2 * 60 * t) * 0.15;
        });
    }

    /// <summary>Шахта: бур — жужжание, которое дрожит.</summary>
    public static float[] Drill(double rate)
    {
        var grit = Lowpass(rate, 900.0);
        return Loop(rate, 2, (t, noise) =>
        {
            var wobble = 1 + Sin(PI2 * 7 * t) * 0.3;
            var buzz = (t * 95 % 1 * 2 - 1) * 0.4;
            return (buzz + grit(noise()) * 1.4) * wobble;
        });
    }

    /// <summary>Пехота: шаги вразнобой — глухие шорохи.</summary>
    public static float[] Steps(double rate)
    {
        var scuff = Lowpass(rate, 1200.0);
        return Loop(rate, 2, (t, noise) =>
        {
            var n = noise();
            var beat = Pulse(t, 0.5, 0.03) + Pulse(t, 0.5, 0.03, 0.23) * 0.8 + Pulse(t, 0.66, 0.025, 0.1) * 0.6;
            return scuff(n) * beat * 2;
        });
    }

    /// <summary>Скрип ржавого металла: срывы трения, то чаще, то реже, раскачивают звонкие резонансы.</summary>
    public static float[] Creak(double rate, int variant)
    {
        var next = Random(variant + 301);
        var length = 1.6 + next() * 1.2;
        var basis = 34 + next() * 26;
        var sway = next() * PI * 2;
        var bodies = new[] { Bandpass(rate, 480 + next() * 200, 14), Bandpass(rate, 1100 + next() * 300, 18), Bandpass(rate, 2200 + next() * 500, 24) };
        double[] weights = [1, 0.7, 0.35];
        var phase = 0.0;
        return Render(rate, length, variant, (t, noise) =>
        {
            // Металл тянет: срывы то учащаются, то редеют.
            var frequency = basis * (1 + 0.4 * Sin(PI2 * 0.5 * t + sway) + 0.15 * Sin(PI2 * 1.7 * t + sway * 2));
            phase += frequency / rate;
            double slip = 0;
            if (phase >= 1)
            {
                phase -= 1;
                slip = 0.6 + 0.4 * Abs(noise());
            }
            var swell = Pow(Sin(PI * Min(1, t / length)), 0.7);
            var input = (slip + noise() * 0.02) * swell;
            double output = 0;
            for (var i = 0; i < bodies.Length; i++)
                output += bodies[i](input) * weights[i];
            return output;
        });
    }

    /// <summary>Лязг: где-то упал или стукнул на ветру лист железа. Неровные обертоны с биением, долго гаснут.</summary>
    public static float[] Clank(double rate, int variant)
    {
        var next = Random(variant + 401);
        var basis = 140 + next() * 160;
        // Отношение к основному тону, за сколько секунд гаснет и насколько громок.
        double[][] partials = [[1, 1.8, 1], [2.32, 1.1, 0.6], [3.89, 0.7, 0.45], [5.61, 0.45, 0.3], [7.13, 0.3, 0.2]];
        // Две близкие частоты на обертон — металл дрожит. Синус — точка, что вращается по кругу.
        var tones = new List<Ring>();
        foreach (var partial in partials)
            foreach (var detune in (ReadOnlySpan<double>)[1, 1.004])
            {
                var step = PI2 * basis * partial[0] * detune / rate;
                tones.Add(new Ring { Cos = Cos(step), Sin = Sin(step), X = 1, Y = 0, Level = partial[2], Fade = Exp(-1 / (partial[1] * rate)) });
            }
        var click = Lowpass(rate, 2500.0);
        return Render(rate, 3, variant, (t, noise) =>
        {
            double output = 0;
            foreach (var tone in tones)
            {
                var x = tone.X * tone.Cos - tone.Y * tone.Sin;
                tone.Y = tone.X * tone.Sin + tone.Y * tone.Cos;
                tone.X = x;
                output += tone.Y * tone.Level;
                tone.Level *= tone.Fade;
            }
            return output * Attack(t, 0.002) + click(noise()) * Decay(t, 0.008) * 2;
        });
    }

    private sealed class Ring
    {
        public double Cos, Sin, X, Y, Level, Fade;
    }

    /// <summary>Далёкий гром: раскаты низкого шума, один за другим, всё тише.</summary>
    public static float[] Thunder(double rate, int variant)
    {
        var next = Random(variant + 501);
        var rumble = Lowpass(rate, 150.0);
        var deeper = Lowpass(rate, 150.0);
        var rolls = new List<Roll>();
        for (var i = 0; i <= 3; i++)
        {
            // Порядок вызовов next — как полей в конструкторе таблицы пилота.
            var at = i == 0 ? 0 : i * 0.4 + next() * 1.8;
            var level = (0.6 + next() * 0.4) / (1 + i * 0.6);
            var fade = Exp(-1 / ((0.6 + next() * 0.8) * rate));
            rolls.Add(new Roll { At = at, Level = level, Fade = fade });
        }
        return Render(rate, 5.5, variant, (t, noise) =>
        {
            double level = 0;
            foreach (var roll in rolls)
                if (t >= roll.At)
                {
                    level += roll.Level * Attack(t - roll.At, 0.15);
                    roll.Level *= roll.Fade;
                }
            return deeper(rumble(noise())) * level;
        });
    }

    private sealed class Roll
    {
        public double At, Level, Fade;
    }
}
