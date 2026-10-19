using static Base.Client.Sounds.Dsp;
using static System.Math;

namespace Base.Client.Sounds;

/// <summary>
/// Остальные разовые звуки мира — перенос worldSynth.ts: огонь и взрывы, щиты, ворота, корабли космопорта, деньги,
/// поражение, рубильник и груз.
/// </summary>
internal static partial class WorldSounds
{
    /// <summary>Огнемёт: хлопок поджига и рёв струи — шум, который вспыхивает и гаснет, со светлым шипением сверху.</summary>
    public static float[] Flame(double rate, int variant)
    {
        var t = 0.0;
        var roar = Lowpass(rate, () => 700 + 1500 * Decay(t, 0.12));
        var hiss = Highpass(rate, 3000);
        var flutter = Random(variant + 601);
        var gust = 1.0;
        return Render(rate, 0.55, variant, (time, noise) =>
        {
            t = time;
            if (Floor(time * 60) != Floor((time - 1 / rate) * 60))
                gust = 0.7 + flutter() * 0.3;
            var n = noise();
            return (roar(n) * 2.4 * gust + hiss(n) * 0.25) * Attack(time, 0.02) * Decay(time, 0.18);
        });
    }

    /// <summary>Зенитка: сухой хлопок выстрела и тугой «тумп» снаряда, как удар по барабану.</summary>
    public static float[] Flak(double rate, int variant)
    {
        var crack = Highpass(rate, 2200);
        var body = Lowpass(rate, 500.0);
        var phase = 0.0;
        return Render(rate, 0.35, variant, (t, noise) =>
        {
            var n = noise();
            phase += PI2 * (90 + 140 * Decay(t, 0.02)) / rate;
            return crack(n) * Decay(t, 0.006) * 0.8 + body(n) * Decay(t, 0.05) * 1.6 + Sin(phase) * Decay(t, 0.07) * 0.8;
        });
    }

    /// <summary>Гаубица: удар ниже пушечного, раскат катится дольше и уходит эхом.</summary>
    public static float[] Artillery(double rate, int variant)
    {
        var crack = Highpass(rate, 1200);
        var rumble = Lowpass(rate, 220.0);
        var echo = Lowpass(rate, 400.0);
        var phase = 0.0;
        return Render(rate, 1.9, variant, (t, noise) =>
        {
            var n = noise();
            phase += PI2 * (30 + 50 * Decay(t, 0.15)) / rate;
            var tail = t > 0.35 ? echo(n) * Decay(t - 0.35, 0.45) * 0.9 : echo(0);
            return crack(n) * Decay(t, 0.015) * 0.9 + Sin(phase) * Decay(t, 0.4) * 1 + rumble(n) * Decay(t, 0.5) * 2.6 + tail;
        });
    }

    /// <summary>Бомба: свист падения — тон сползает вниз и крепнет.</summary>
    public static float[] BombFall(double rate, int variant)
    {
        var phase = variant * 0.3;
        var air = Bandpass(rate, 1400, 3);
        return Render(rate, 0.9, variant, (t, noise) =>
        {
            phase += PI2 * (1500 - 700 * (t / 0.9)) / rate;
            var swell = Attack(t, 0.5) * Min(1, (0.9 - t) / 0.05);
            return (Sin(phase) * 0.5 + air(noise()) * 0.8) * swell;
        });
    }

    /// <summary>Пуля попала: короткий щелчок о броню, иногда со звоном рикошета.</summary>
    public static float[] Ricochet(double rate, int variant)
    {
        var next = Random(variant + 701);
        var whine = next() < 0.6;
        var start = 2800 + next() * 1600;
        var tick = Highpass(rate, 2500);
        var phase = 0.0;
        return Render(rate, whine ? 0.3 : 0.06, variant, (t, noise) =>
        {
            phase += PI2 * start * (1 - 0.35 * Min(1, t / 0.3)) / rate;
            return tick(noise()) * Decay(t, 0.004) * 1.2 + (whine ? Sin(phase) * Attack(t, 0.01) * Decay(t, 0.08) * 0.35 : 0);
        });
    }

    /// <summary>Подбитая машина: скрежет и хруст смятого металла после взрыва, долгий звон обшивки.</summary>
    public static float[] Wreck(double rate, int variant)
    {
        var next = Random(variant + 801);
        var crunch = Bandpass(rate, 900 + next() * 400, 2);
        var ring = Metal(180 + next() * 120, 0.6, variant + 811);
        var rattle = Highpass(rate, 1500);
        return Render(rate, 1.4, variant, (t, noise) =>
        {
            var n = noise();
            var wobble = Sin(PI2 * 23 * t);
            var grit = t < 0.5 ? crunch(n) * (0.5 + 0.5 * (wobble * wobble)) * Decay(t, 0.15) * 2 : crunch(0);
            return grit + ring(t) * 0.5 + rattle(n) * (next() < 0.003 ? 1 : 0) * Decay(t, 0.5) * 3;
        });
    }

    /// <summary>Рушится здание: глухой обвал — рокот, удары падающих плит и сыплющийся щебень.</summary>
    public static float[] Collapse(double rate, int variant)
    {
        var next = Random(variant + 901);
        var rumble = Lowpass(rate, 140.0);
        var debris = Bandpass(rate, 700, 1.5);
        var grit = Highpass(rate, 2500);
        var fallAt = new double[7];
        var fallLevel = new double[7];
        for (var i = 0; i < 7; i++)
        {
            fallAt[i] = 0.15 + next() * 1.6;
            fallLevel[i] = 0.5 + next() * 0.5;
        }
        var thuds = Lowpass(rate, 300.0);
        return Render(rate, 2.6, variant, (t, noise) =>
        {
            var n = noise();
            double hits = 0;
            for (var i = 0; i < 7; i++)
                if (t >= fallAt[i])
                    hits += Decay(t - fallAt[i], 0.05) * fallLevel[i];
            var sprinkle = next() < 0.02 * Decay(t, 0.9) ? 1 : 0;
            return rumble(n) * Attack(t, 0.05) * Decay(t, 0.8) * 3 + thuds(n * hits) * 4 + debris(n) * Decay(t, 0.6) * 0.6 + grit(sprinkle) * 0.8;
        });
    }

    /// <summary>Ядерный взрыв: оглушительный удар, огромный бас, раскаты и через полсекунды ударная волна.</summary>
    public static float[] NukeBlast(double rate, int variant)
    {
        var t = 0.0;
        var body = Lowpass(rate, () => 90 + 1600 * Decay(t, 0.3));
        var wind = Bandpass(rate, 450, 0.8);
        var rolls = Lowpass(rate, 160.0);
        var next = Random(variant + 1001);
        var at = new double[5];
        var level = new double[5];
        for (var i = 0; i < 5; i++)
        {
            at[i] = 1.2 + i * 0.7 + next() * 0.6;
            level[i] = 0.8 / (1 + i * 0.4);
        }
        var phase = 0.0;
        return Render(rate, 6, variant, (time, noise) =>
        {
            t = time;
            var n = noise();
            phase += PI2 * (24 + 30 * Decay(time, 0.4)) / rate;
            var blast = body(n) * Attack(time, 0.003) * Decay(time, 1.1) * 3 + Sin(phase) * Decay(time, 1.4) * 1.3;
            var wave = time > 0.55
                ? (Sin(PI2 * 40 * (time - 0.55)) * Decay(time - 0.55, 0.15) * 0.4 + wind(n) * Decay(time - 0.55, 1.2) * 2.5) * Attack(time - 0.55, 0.01)
                : wind(0);
            double rolling = 0;
            for (var i = 0; i < 5; i++)
                if (time >= at[i])
                    rolling += level[i] * Attack(time - at[i], 0.2) * Decay(time - at[i], 0.9);
            return blast + wave + rolls(n) * rolling * 3;
        });
    }

    /// <summary>Створки шахты встали: тяжёлый замок — глухой удар и звон металла.</summary>
    public static float[] DoorsLocked(double rate, int variant)
    {
        var clang = ClangAt(rate, 0, 95, 1, variant + 1101);
        var second = ClangAt(rate, 0.12, 140, 0.6, variant + 1111);
        return Render(rate, 1.2, variant, (t, noise) =>
        {
            var n = noise();
            return clang(t, n) + second(t, n);
        });
    }

    /// <summary>Старт ракеты: зажигание, выход двигателя на тягу с рёвом, который рвётся и трещит, и уход вверх.</summary>
    public static float[] Liftoff(double rate, int variant)
    {
        var t = 0.0;
        var roar = Lowpass(rate, () => 300 + 2200 * Min(1, t / 0.6) * Decay(Max(0, t - 1.5), 1.5));
        var crackle = Highpass(rate, 2000);
        var deep = Lowpass(rate, 80.0);
        var next = Random(variant + 1201);
        var gate = 1.0;
        var until = 0.0;
        return Render(rate, 5, variant, (time, noise) =>
        {
            t = time;
            var n = noise();
            if (time >= until)
            {
                gate = 0.3 + next() * 0.7;
                until = time + 0.004 + next() * 0.02;
            }
            var swell = Attack(time, 0.4) * Decay(Max(0, time - 1.2), 1.4);
            return (roar(n) * 2.6 + crackle(n) * gate * 0.35 + deep(n) * 3) * swell + crackle(n) * Decay(time, 0.03) * 1.5;
        });
    }

    /// <summary>Щит принял удар: всплеск — шум с резким скольжением вниз и дрожащий звон поля.</summary>
    public static float[] ShieldHit(double rate, int variant)
    {
        var t = 0.0;
        var sweep = Lowpass(rate, () => 600 + 5000 * Decay(t, 0.05));
        var phase = variant * 0.2;
        return Render(rate, 0.45, variant, (time, noise) =>
        {
            t = time;
            phase += PI2 * (380 + 220 * Decay(time, 0.06)) * (1 + 0.02 * Sin(PI2 * 35 * time)) / rate;
            return sweep(noise()) * Decay(time, 0.05) * 1.3 + Sin(phase) * Attack(time, 0.004) * Decay(time, 0.15) * 0.6;
        });
    }

    /// <summary>Щит пробит: поле гаснет — гул падает вниз, рвётся треском и глохнет.</summary>
    public static float[] ShieldDown(double rate, int variant)
    {
        var crackle = Highpass(rate, 1500);
        var next = Random(variant + 1301);
        var phase = 0.0;
        var gate = 0.0;
        var until = 0.0;
        return Render(rate, 1.4, variant, (t, noise) =>
        {
            if (t >= until)
            {
                gate = next() < 0.5 ? next() : 0;
                until = t + 0.005 + next() * 0.02;
            }
            phase += PI2 * (320 * Decay(t, 0.5) + 30) / rate;
            var hum = (Sin(phase) + Sin(phase * 2) * 0.4) * Decay(t, 0.45);
            return hum * 0.8 + crackle(noise()) * gate * Decay(t, 0.35) * 0.9;
        });
    }

    /// <summary>Щит поднялся: гул нарастает снизу вверх и встаёт на ровную ноту.</summary>
    public static float[] ShieldUp(double rate, int variant)
    {
        var phase = 0.0;
        return Render(rate, 1, variant, (t, _) =>
        {
            phase += PI2 * (120 + 260 * Min(1, t / 0.6)) * (1 + variant * 0.01) / rate;
            return (Sin(phase) + Sin(phase * 2.01) * 0.35) * Attack(t, 0.5) * Min(1, (1 - t) / 0.3);
        });
    }

    /// <summary>Ворота: шипит гидравлика, створка едет и встаёт с лязгом.</summary>
    public static float[] Gate(double rate, int variant)
    {
        var hiss = Bandpass(rate, 2500, 1);
        var whine = Lowpass(rate, 400.0);
        var clang = ClangAt(rate, 0.55, 120, 0.7, variant + 1401);
        return Render(rate, 1, variant, (t, noise) =>
        {
            var n = noise();
            var moving = t < 0.55 ? Attack(t, 0.05) : 0;
            var motor = Sin(PI2 * 110 * t) * 0.25 + whine(n) * 0.6;
            return hiss(n) * moving * 0.6 + motor * moving + clang(t, n);
        });
    }

    /// <summary>Груз упал на землю: глухой удар ящиков и стук.</summary>
    public static float[] Crates(double rate, int variant)
    {
        var thud = Lowpass(rate, 260.0);
        var wood = Bandpass(rate, 600, 4);
        return Render(rate, 0.5, variant, (t, noise) =>
        {
            var n = noise();
            var hits = Decay(t, 0.03) + (t > 0.09 ? Decay(t - 0.09, 0.02) * 0.6 : 0);
            return thud(n * hits) * 4 + wood(n * hits) * 2;
        });
    }

    /// <summary>Покрытие легло: плотный шлепок бетона и скрежет мастерка.</summary>
    public static float[] Paved(double rate, int variant)
    {
        var slap = Lowpass(rate, 400.0);
        var scrape = Bandpass(rate, 2500, 2);
        return Render(rate, 0.45, variant, (t, noise) =>
        {
            var n = noise();
            return slap(n) * Decay(t, 0.04) * 3 + scrape(n) * (t > 0.08 ? Attack(t - 0.08, 0.05) * Decay(t - 0.08, 0.1) : 0) * 0.8;
        });
    }

    /// <summary>Радар: мягкий звон импульса, уходящий эхом.</summary>
    public static float[] Radar(double rate, int variant)
    {
        var phase = 0.0;
        return Render(rate, 1.6, variant, (t, _) =>
        {
            phase += PI2 * 1320 * (1 - 0.03 * t) * (1 + variant * 0.008) / rate;
            var echo = t > 0.4 ? Decay(t - 0.4, 0.3) * 0.3 : 0;
            return Sin(phase) * (Attack(t, 0.003) * Decay(t, 0.15) + echo);
        });
    }

    /// <summary>Лазер попал: шипение прожжённого металла — шум, который тает, с тонким звоном.</summary>
    public static float[] Sizzle(double rate, int variant)
    {
        var hiss = Highpass(rate, 3500);
        double phase = variant;
        return Render(rate, 0.35, variant, (t, noise) =>
        {
            phase += PI2 * 2600 / rate;
            return hiss(noise()) * Attack(t, 0.005) * Decay(t, 0.1) * 1.2 + Sin(phase) * Decay(t, 0.05) * 0.2;
        });
    }

    /// <summary>Огонь лизнул цель: потрескивание горящего — щелчки поверх тихого гула пламени.</summary>
    public static float[] Burn(double rate, int variant)
    {
        var next = Random(variant + 3101);
        var roar = Lowpass(rate, 500.0);
        var pop = Bandpass(rate, 1800, 2);
        return Render(rate, 0.5, variant, (t, noise) =>
        {
            var n = noise();
            var crack = next() < 0.004 ? 1 : 0;
            return roar(n) * Decay(t, 0.2) * 1.4 + pop(crack) * 4 * Decay(t, 0.3);
        });
    }

    /// <summary>Разряд ударил в цель: сухой щелчок и треск.</summary>
    public static float[] Zap(double rate, int variant)
    {
        var snap = Highpass(rate, 2000);
        var body = Bandpass(rate, 700, 2);
        return Render(rate, 0.18, variant, (t, noise) =>
        {
            var n = noise();
            return snap(n) * Decay(t, 0.008) * 1.5 + body(n) * Decay(t, 0.03) * (0.6 + 0.4 * Signum(Sin(PI2 * 90 * t)));
        });
    }

    /// <summary>Корабль космопорта взлетает: прогрев, рёв двигателей нарастает и уходит вверх, светлея и стихая.</summary>
    public static float[] ShipLaunch(double rate, int variant)
    {
        var t = 0.0;
        var roar = Lowpass(rate, () => 300 + 2500 * Min(1, t / 1.2));
        var deep = Lowpass(rate, 90.0);
        var phase = 0.0;
        return Render(rate, 3.5, variant, (time, noise) =>
        {
            t = time;
            var n = noise();
            phase += PI2 * (300 + 900 * Min(1, time / 1.5)) / rate;
            var swell = Attack(time, 1) * Decay(Max(0, time - 1.3), 0.9);
            return (roar(n) * 2 + deep(n) * 2.5 + Sin(phase) * 0.12) * swell;
        });
    }

    /// <summary>Корабль садится: рёв спускается сверху и тяжелеет, касание — глухой удар, шипит сброс давления.</summary>
    public static float[] ShipLand(double rate, int variant)
    {
        var t = 0.0;
        var roar = Lowpass(rate, () => 2500 - 2100 * Min(1, t / 2));
        var deep = Lowpass(rate, 90.0);
        var hiss = Highpass(rate, 2500);
        var touch = Lowpass(rate, 150.0);
        return Render(rate, 3.5, variant, (time, noise) =>
        {
            t = time;
            var n = noise();
            var engine = (roar(n) * 2 + deep(n) * 2) * Attack(time, 0.8) * (time < 2.2 ? 1 : Decay(time - 2.2, 0.15));
            var thump = time > 2.2 ? touch(n) * Decay(time - 2.2, 0.08) * 6 : touch(0);
            var vent = time > 2.4 ? hiss(n) * Attack(time - 2.4, 0.03) * Decay(time - 2.4, 0.35) * 0.8 : 0;
            return engine + thump + vent;
        });
    }

    /// <summary>Деньги пришли: звон монет — несколько ярких металлических ударов вразнобой.</summary>
    public static float[] Coins(double rate, int variant)
    {
        var next = Random(variant + 3201);
        var at = new double[5];
        var ring = new Func<double, double>[5];
        for (var i = 0; i < 5; i++)
        {
            at[i] = i * 0.05 + next() * 0.03;
            ring[i] = Metal(2400 + next() * 900, 0.08, variant * 10 + i);
        }
        return Render(rate, 0.6, variant, (t, _) =>
        {
            double output = 0;
            for (var i = 0; i < 5; i++)
                if (t >= at[i])
                    output += ring[i](t - at[i]);
            return output;
        });
    }

    /// <summary>Подарок: звонкий восходящий перебор.</summary>
    public static float[] Gift(double rate, int variant) => Chime(rate, variant, [523, 659, 784, 1047, 1319], 0.06, 0.3);

    /// <summary>Поражение: тяжёлый низкий аккорд, который сползает вниз и гаснет.</summary>
    public static float[] Defeat(double rate, int variant)
    {
        double[] notes = [110, 130.8, 164.8];
        return Render(rate, 4, variant, (t, _) =>
        {
            var slide = (1 - 0.06 * Min(1, t / 3)) * (1 + variant * 0.006);
            double output = 0;
            foreach (var note in notes)
            {
                var f = note * slide;
                output += (Sin(PI2 * f * t) + Sin(PI2 * f * 2 * t) * 0.3 + Sin(PI2 * f * 3 * t) * 0.12) / notes.Length;
            }
            return output * Attack(t, 0.15) * Decay(t, 1.4);
        });
    }

    /// <summary>Капитуляция: долгий низкий гудок сирены — база гибнет волной.</summary>
    public static float[] Surrender(double rate, int variant)
    {
        var phase = 0.0;
        return Render(rate, 3, variant, (t, _) =>
        {
            phase += PI2 * (150 + 40 * Sin(PI * Min(1, t / 3))) * (1 + variant * 0.01) / rate;
            return (Sin(phase) + Signum(Sin(phase)) * 0.2 + Sin(phase * 1.5) * 0.3) * Attack(t, 0.3) * Min(1, (3 - t) / 0.6);
        });
    }

    /// <summary>Месторождение выработано: бур сбегает с оборотов и глохнет, два низких тона.</summary>
    public static float[] Depleted(double rate, int variant)
    {
        var grit = Lowpass(rate, 900.0);
        var phase = 0.0;
        return Render(rate, 1.4, variant, (t, noise) =>
        {
            phase += PI2 * 95 * Decay(t, 0.5) / rate;
            var drill = ((phase / PI2 % 1 * 2 - 1) * 0.4 + grit(noise())) * Decay(t, 0.35);
            var tones = (t > 0.5 ? Sin(PI2 * 330 * t) * Decay(t - 0.5, 0.15) : 0) + (t > 0.75 ? Sin(PI2 * 247 * t) * Decay(t - 0.75, 0.25) : 0);
            return drill + tones * 0.5;
        });
    }

    /// <summary>Рубильник: щелчок и гул, который уходит вниз (выключили) или поднимается (включили).</summary>
    public static float[] Power(double rate, int variant, bool on)
    {
        var click = Bandpass(rate, 1500, 3);
        var phase = 0.0;
        return Render(rate, 0.8, variant, (t, noise) =>
        {
            var sweep = on ? Min(1, t / 0.5) : 1 - Min(1, t / 0.6);
            phase += PI2 * (40 + 80 * sweep) / rate;
            var hum = (Sin(phase) + Sin(phase * 2) * 0.4) * (on ? Attack(t, 0.1) * Min(1, (0.8 - t) / 0.3) : Decay(t, 0.3));
            return click(noise()) * Decay(t, 0.006) * 4 + hum * 0.6;
        });
    }

    /// <summary>Кузов полон или пуст: глухой удар груза о металл.</summary>
    public static float[] Cargo(double rate, int variant)
    {
        var thud = Lowpass(rate, 220.0);
        var ring = Metal(260 + variant * 20, 0.12, variant + 3301);
        return Render(rate, 0.4, variant, (t, noise) => thud(noise()) * Decay(t, 0.03) * 3 + ring(t) * 0.35);
    }

    /// <summary>Звонок чата: мягкий короткий тон; системное сообщение — ниже.</summary>
    public static float[] ChatBell(double rate, int variant, double note) => Chime(rate, variant, [note], 0.05, 0.12);
}
