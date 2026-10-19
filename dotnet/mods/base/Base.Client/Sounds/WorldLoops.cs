using static Base.Client.Sounds.Dsp;
using static System.Math;

namespace Base.Client.Sounds;

/// <summary>Петли мира — перенос WORLD_LOOPS из worldSynth.ts: громкость каждой задаёт тот, кто её играет.</summary>
internal static class WorldLoops
{
    private const double PI2 = 2 * PI;

    /// <summary>Перегрузка электростанции: треск дуг поверх надсадного гудения сети с перегрузом.</summary>
    public static float[] Overload(double rate)
    {
        var next = Random(2001);
        var crackle = Highpass(rate, 1800);
        var snap = Bandpass(rate, 900, 2);
        var gate = 0.0;
        var until = 0.0;
        return Loop(rate, 3, (t, noise) =>
        {
            if (t >= until)
            {
                // Разряды идут пачками: то густо, то пусто.
                var burst = Sin(PI2 * 0.7 * t) > -0.3;
                gate = burst && next() < 0.45 ? 0.3 + next() * 0.7 : 0;
                until = t + 0.002 + next() * 0.01;
            }
            var n = noise();
            var strain = Signum(Sin(PI2 * 100 * t)) * 0.12 + Sin(PI2 * 150 * t) * 0.1;
            return crackle(n) * gate * 1.3 + snap(n) * gate * 0.8 + strain * (0.8 + 0.2 * Sin(PI2 * 3 * t));
        });
    }

    /// <summary>Стройка: удары молота, трещотка гайковёрта и шипение сварки — вразнобой.</summary>
    public static float[] Construct(double rate)
    {
        var hammer = WorldSounds.ClangAt(rate, 0, 380, 0.5, 2101);
        var ratchet = Bandpass(rate, 3200, 5);
        var weld = Highpass(rate, 3000);
        return Loop(rate, 4, (t, noise) =>
        {
            var n = noise();
            var knock = hammer((t % 0.7 + 0.7) % 0.7, n) * (t % 2.8 < 2.1 ? 1 : 0);
            var drive = t > 2.2 && t < 3.2 ? ratchet(n * Pulse(t, 0.03, 0.002)) * 2 : ratchet(0);
            var spark = weld(n) * (0.2 + 0.2 * Sin(PI2 * 0.6 * t)) * 0.4;
            return knock + drive + spark;
        });
    }

    /// <summary>Сварка ремонта: треск дуги — рваное шипение — и гул аппарата.</summary>
    public static float[] Weld(double rate)
    {
        var next = Random(2201);
        var hiss = Highpass(rate, 2500);
        var level = 0.0;
        var until = 0.0;
        return Loop(rate, 2, (t, noise) =>
        {
            if (t >= until)
            {
                level = 0.4 + next() * 0.6;
                until = t + 0.01 + next() * 0.05;
            }
            return hiss(noise()) * level + Signum(Sin(PI2 * 100 * t)) * 0.08;
        });
    }

    /// <summary>Луч грузовика: переливчатый гул, который дышит, — груз течёт по лучу.</summary>
    public static float[] Tractor(double rate) =>
        Loop(rate, 2, (t, _) =>
        {
            var wobble = Sin(PI2 * 3 * t);
            return (Sin(PI2 * 220 * t + wobble * 2) * 0.6 + Sin(PI2 * 331 * t + wobble) * 0.3 + Sin(PI2 * 660 * t) * 0.1) * (0.7 + 0.3 * Sin(PI2 * 1.5 * t));
        });

    /// <summary>Привод башни: тонкое жужжание мотора с редуктором.</summary>
    public static float[] Servo(double rate)
    {
        var gear = Bandpass(rate, 1800, 6);
        return Loop(rate, 2, (t, noise) =>
        {
            var phase = t * 240 % 1;
            return (phase * 2 - 1) * 0.4 + gear(noise()) * 0.6 + Sin(PI2 * 480 * t) * 0.15;
        });
    }

    /// <summary>Реактор: глубокий мерный пульс насосов, гул и шипение пара.</summary>
    public static float[] Reactor(double rate)
    {
        var steam = Highpass(rate, 1500);
        var flow = Lowpass(rate, 200.0);
        return Loop(rate, 4, (t, noise) =>
        {
            var n = noise();
            var pump = Pulse(t, 1, 0.25) * Sin(PI2 * 45 * t);
            return pump * 0.8 + Sin(PI2 * 60 * t) * 0.25 + flow(n) * 0.8 + steam(n) * 0.08 * (1 + Sin(PI2 * 0.25 * t));
        });
    }

    /// <summary>Купол щита: ровный высокий гул поля с медленными биениями.</summary>
    public static float[] Dome(double rate) =>
        Loop(rate, 4, (t, _) => (Sin(PI2 * 330 * t) + Sin(PI2 * 330.5 * t) + Sin(PI2 * 660.25 * t) * 0.3) * 0.5);

    /// <summary>Гидравлика: створки шахты расходятся — ровный вой насоса и шипение.</summary>
    public static float[] Hydraulic(double rate)
    {
        var hiss = Bandpass(rate, 2200, 1.5);
        return Loop(rate, 2, (t, noise) =>
        {
            var phase = t * 140 % 1;
            return (phase * 2 - 1) * 0.35 + Sin(PI2 * 280 * t) * 0.2 + hiss(noise()) * 0.5;
        });
    }

    /// <summary>Пар из шахты: широкое шипение, пульсирующее клубами.</summary>
    public static float[] Steam(double rate)
    {
        var hiss = Highpass(rate, 800);
        var body = Lowpass(rate, 3000.0);
        return Loop(rate, 3, (t, noise) =>
        {
            var puff = Sin(PI2 * (2.0 / 3) * t);
            return body(hiss(noise())) * (0.6 + 0.4 * (puff * puff));
        });
    }

    /// <summary>Ракетный двигатель: рёв струи с треском — для разгона в шахте и для ракеты в полёте.</summary>
    public static float[] Rocket(double rate)
    {
        var next = Random(2301);
        var roar = Lowpass(rate, 1400.0);
        var deep = Lowpass(rate, 90.0);
        var crackle = Highpass(rate, 2500);
        var gate = 0.0;
        var until = 0.0;
        return Loop(rate, 3, (t, noise) =>
        {
            if (t >= until)
            {
                gate = next() < 0.5 ? 0.3 + next() * 0.7 : 0;
                until = t + 0.003 + next() * 0.015;
            }
            var n = noise();
            return roar(n) * 2 + deep(n) * 3 + crackle(n) * gate * 0.4;
        });
    }

    /// <summary>Накопитель заряжается или отдаёт: тонкий высокий свист преобразователя.</summary>
    public static float[] Charge(double rate) =>
        Loop(rate, 2, (t, _) => Sin(PI2 * 3200 * t) * 0.3 + Sin(PI2 * 1600 * t) * 0.3 * (0.6 + 0.4 * Sin(PI2 * 2 * t)));

    /// <summary>Трубы: груз течёт — глухое журчание и шорох в металле.</summary>
    public static float[] Flow(double rate)
    {
        var gurgle = Bandpass(rate, 350, 4);
        var rush = Lowpass(rate, 700.0);
        var swirl = Random(2401);
        var level = 0.5;
        var until = 0.0;
        return Loop(rate, 3, (t, noise) =>
        {
            if (t >= until)
            {
                level = 0.3 + swirl() * 0.7;
                until = t + 0.03 + swirl() * 0.08;
            }
            var n = noise();
            return gurgle(n) * level * 2 + rush(n) * 0.8;
        });
    }
}
