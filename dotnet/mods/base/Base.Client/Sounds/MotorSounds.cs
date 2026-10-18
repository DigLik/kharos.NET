using Kharos.Mod.Sdk.Client;
using static Base.Client.Sounds.Dsp;
using static System.Math;

namespace Base.Client.Sounds;

/// <summary>Устройство мотора: чем он звучит.</summary>
internal enum MotorKind { Combustion, Rotor, Props, Jet }

/// <summary>Труба двигателя: частота резонанса, добротность (в долях), вес.</summary>
internal readonly record struct Pipe(double Frequency, double Q, double Weight);

/// <summary>
/// Мотор (MOTORS пилота): устройство, во сколько раз обороты на пределе выше холостых, чем звучит ход, стартер,
/// пневмотормоз. Поля устройства — те, что нужны его виду.
/// </summary>
internal sealed class Motor
{
    public MotorKind Kind { get; init; }
    public double Revs { get; init; }
    /// <summary>"roll" — шорох шин, "clatter" — лязг гусениц; null — не ездит по земле.</summary>
    public string? Running { get; init; }
    /// <summary>"ignition" или "ignitionDiesel"; null — стартера нет (летающие).</summary>
    public string? Start { get; init; }
    public bool Brake { get; init; }
    // Двигатель внутреннего сгорания.
    public double Firing { get; init; }
    public int Cylinders { get; init; }
    public Pipe[] Pipes { get; init; } = [];
    public double Body { get; init; }
    public double Knock { get; init; }
    public double[] Fade { get; init; } = [];
    public double[] Roar { get; init; } = [];
    // Вертолёт и турбина: лопасти, свист; дрон: ноты винтов.
    public int Blades { get; init; }
    public double Whine { get; init; }
    public double[] Notes { get; init; } = [];
}

/// <summary>Ручки голоса мотора (sound у update пилота).</summary>
internal struct MotorKnobs
{
    public double Rate, Load, Level, Running, RunningRate, Squeal, Clarity;
}

/// <summary>
/// Моторы техники — перенос mods/base/sounds/motors.luau (motorSynth.ts и motorVoice.ts). У каждого мотора две петли
/// с одинаковым тактом — холостой ход и под нагрузкой: голос плавно переходит от одной к другой, пока водитель жмёт на
/// газ, и крутит их быстрее вслед за оборотами. Под мотором — ход: шорох шин или лязг гусениц, и скрип разворота.
/// </summary>
internal static class MotorSounds
{
    /// <summary>Сколько секунд длится петля мотора: короче — слышно, что такт повторяется.</summary>
    public const double LoopSeconds = 2;

    private const double PI2 = 2 * PI;

    /// <summary>Моторы по именам (data.motor видов).</summary>
    public static readonly Dictionary<string, Motor> Motors = new(StringComparer.Ordinal)
    {
        // Бензиновая четвёрка лёгких машин: бодрая, высоко раскручивается.
        ["petrol"] = new Motor
        {
            Kind = MotorKind.Combustion, Firing = 28, Cylinders = 4, Pipes = [new(95, 3, 1), new(260, 5, 0.55), new(820, 4, 0.22), new(1900, 3, 0.08)],
            Body = 0.7, Knock = 0, Fade = [0.004, 0.0025], Roar = [0.25, 0.7], Revs = 3.4, Running = "roll", Start = "ignition", Brake = false,
        },
        // Дизельная шестёрка грузовиков: ниже, стучит, раскручивается недалеко.
        ["diesel"] = new Motor
        {
            Kind = MotorKind.Combustion, Firing = 20, Cylinders = 6, Pipes = [new(70, 3, 1), new(190, 4, 0.6), new(560, 4, 0.28), new(1300, 3, 0.1)],
            Body = 0.9, Knock = 0.45, Fade = [0.005, 0.003], Roar = [0.3, 0.75], Revs = 2.4, Running = "roll", Start = "ignitionDiesel", Brake = true,
        },
        // Гусеничные: большой V8 — неровный рокот, самый низкий.
        ["heavy"] = new Motor
        {
            Kind = MotorKind.Combustion, Firing = 16, Cylinders = 8, Pipes = [new(52, 2.5, 1), new(140, 4, 0.75), new(400, 4, 0.35), new(950, 3, 0.12)],
            Body = 1.1, Knock = 0.25, Fade = [0.007, 0.004], Roar = [0.35, 0.8], Revs = 2.3, Running = "clatter", Start = "ignitionDiesel", Brake = true,
        },
        // Вертолёт: транспортник и штурмовик.
        ["rotor"] = new Motor { Kind = MotorKind.Rotor, Blades = 13, Whine = 1650, Revs = 1.25 },
        // Дрон: четыре винта.
        ["props"] = new Motor { Kind = MotorKind.Props, Notes = [190, 197.5, 205, 184.5], Revs = 1.6 },
        // Бомбардировщик: турбины.
        ["jet"] = new Motor { Kind = MotorKind.Jet, Whine = 2400, Revs = 1.3 },
    };

    /// <summary>Сколько секунд крутит стартер, прежде чем мотор схватит.</summary>
    public static double StartCrank(string start) => start == "ignitionDiesel" ? 0.4 : 0.28;

    /// <summary>
    /// Двигатель внутреннего сгорания: вспышки ровно по такту, но цилиндры неодинаковы, и каждая вспышка чуть сдвинута
    /// и чуть сильнее или слабее соседней — мотор звучит живым. Такт петли кратен числу цилиндров: она без шва.
    /// </summary>
    public static float[] Combustion(double rate, Motor spec, bool load)
    {
        var period = 1 / spec.Firing;
        var count = Max(spec.Cylinders, RoundHalf(LoopSeconds * spec.Firing / spec.Cylinders) * spec.Cylinders);
        // Расписание вспышек одно на обе петли: холостая и нагруженная звучат в такт.
        var next = Random(spec.Firing * 31 + spec.Cylinders);
        var cylinders = new double[spec.Cylinders];
        for (var i = 0; i < cylinders.Length; i++)
            cylinders[i] = 0.7 + next() * 0.3;
        var shifts = new double[count];
        var strengths = new double[count];
        for (var i = 0; i < count; i++)
        {
            shifts[i] = next() * 0.05 * period;
            strengths[i] = cylinders[i % spec.Cylinders] * (0.9 + next() * 0.2);
        }
        var fade = spec.Fade[load ? 1 : 0];
        var roar = spec.Roar[load ? 1 : 0];
        var pipes = spec.Pipes.Select(pipe => (Filter: Bandpass(rate, pipe.Frequency, pipe.Q * 2), pipe.Weight)).ToArray();
        var thump = Lowpass(rate, load ? 260.0 : 180.0);
        var flow = Lowpass(rate, load ? 900.0 : 500.0);
        var knock = Bandpass(rate, 2700, 3);
        var rasp = Bandpass(rate, 3400, 2);
        var dc = Highpass(rate, 20);
        double index = -1, at = 0, strength = 0;
        return Loop(rate, count * period, (t, noise) =>
        {
            var slot = Floor(t / period);
            var firing = (int)(slot % count);
            if (slot != index && t >= slot * period + shifts[firing])
            {
                index = slot;
                at = t;
                strength = strengths[firing];
            }
            var since = t - at;
            var n = noise();
            var kick = strength * (Decay(since, fade) - Decay(since, 0.0004));
            var burst = strength * n * Decay(since, fade * 2.5) * roar;
            // Толчок давления: волна выхлопа длиной в половину такта — из них рокот на частоте вспышек.
            var surge = since < period * 0.45 ? strength * Sin(PI * since / (period * 0.45)) : 0;
            double pipe = 0;
            foreach (var p in pipes)
                pipe += p.Filter(kick + burst) * p.Weight;
            var output = pipe * 2.2 + thump(surge) * spec.Body * 1.4;
            // Поток газа в трубе не стихает между вспышками.
            output += flow(n) * (0.25 + surge * 0.5) * (load ? 0.8 : 0.5);
            output += knock(n) * strength * Decay(since, 0.0015) * spec.Knock * 3;
            if (load)
                output += rasp(n) * strength * Decay(since, fade * 3) * 0.5;
            return dc(output);
        });
    }

    /// <summary>Вертолёт: каждая лопасть — хлопок воздуха с низким ударом; под нагрузкой хлопки резче, турбина громче.</summary>
    public static float[] Rotor(double rate, Motor spec, bool load)
    {
        var period = 1.0 / spec.Blades;
        var wash = Lowpass(rate, load ? 1500.0 : 650.0);
        var air = Lowpass(rate, 400.0);
        var next = Random(spec.Blades);
        var strengths = new double[RoundHalf(LoopSeconds * spec.Blades)];
        for (var i = 0; i < strengths.Length; i++)
            strengths[i] = 0.85 + next() * 0.15;
        return Loop(rate, LoopSeconds, (t, noise) =>
        {
            var slot = Floor(t / period);
            var since = t - slot * period;
            var strength = strengths[(int)(slot % strengths.Length)];
            var n = noise();
            var slap = wash(n) * Decay(since, load ? 0.018 : 0.03) * strength * (load ? 2.6 : 1.8);
            var thump = Sin(PI2 * 48 * since) * Decay(since, 0.03) * strength * 0.6;
            var whine = (Sin(PI2 * spec.Whine * t) + Sin(PI2 * spec.Whine * 2 * t) * 0.4) * (load ? 0.09 : 0.05);
            return slap + thump + whine + air(n) * 0.35;
        });
    }

    /// <summary>Дрон: винты звенят пилой каждый на своей ноте, биения между ними — жужжание роя.</summary>
    public static float[] Props(double rate, Motor spec, bool load)
    {
        var tone = Lowpass(rate, load ? 3200.0 : 1800.0);
        var air = Lowpass(rate, 900.0);
        return Loop(rate, LoopSeconds, (t, noise) =>
        {
            double saw = 0;
            foreach (var note in spec.Notes)
            {
                var phase = t * note % 1;
                saw += phase * 2 - 1 + ((phase * 2 % 1) * 2 - 1) * (load ? 0.6 : 0.35);
            }
            return tone(saw) * 0.5 + air(noise()) * (load ? 0.5 : 0.3);
        });
    }

    /// <summary>Турбина: рёв струи — шум с низами, над ним свист компрессора, который чуть плавает.</summary>
    public static float[] Jet(double rate, Motor spec, bool load)
    {
        var roar = Lowpass(rate, load ? 1400.0 : 800.0);
        var deep = Lowpass(rate, 120.0);
        return Loop(rate, LoopSeconds, (t, noise) =>
        {
            var n = noise();
            var wobble = 1 + Sin(PI2 * 0.5 * t) * 0.004;
            var whine = Sin(PI2 * spec.Whine * wobble * t) * 0.12 + Sin(PI2 * spec.Whine * 1.5 * wobble * t) * 0.05;
            return roar(n) * (load ? 1.6 : 1.1) + deep(n) * 2 + whine * (load ? 1.4 : 1);
        });
    }

    /// <summary>Петля мотора в одном из двух режимов.</summary>
    public static float[] Design(double rate, Motor motor, bool load) => motor.Kind switch
    {
        MotorKind.Combustion => Combustion(rate, motor, load),
        MotorKind.Rotor => Rotor(rate, motor, load),
        MotorKind.Props => Props(rate, motor, load),
        _ => Jet(rate, motor, load),
    };

    /// <summary>Лязг гусениц: звенья ложатся на катки по очереди — звонкий щелчок металла и глухой удар; снизу рокочет земля.</summary>
    public static float[] Clatter(double rate)
    {
        const int links = 12;
        const double period = 1.0 / links;
        var next = Random(1201);
        var strengths = new double[(int)LoopSeconds * links];
        var shifts = new double[strengths.Length];
        for (var i = 0; i < strengths.Length; i++)
            strengths[i] = 0.5 + next() * 0.5;
        for (var i = 0; i < shifts.Length; i++)
            shifts[i] = next() * 0.15 * period;
        var plates = new[] { Bandpass(rate, 1300, 12), Bandpass(rate, 2900, 16), Bandpass(rate, 650, 6) };
        double[] weights = [1, 0.55, 0.7];
        var thud = Lowpass(rate, 200.0);
        var ground = Lowpass(rate, 110.0);
        return Loop(rate, LoopSeconds, (t, noise) =>
        {
            var slot = Floor(t / period);
            var at = (int)(slot % shifts.Length);
            var since = t - slot * period - shifts[at];
            var strength = since >= 0 ? strengths[(int)(slot % strengths.Length)] : 0;
            var n = noise();
            var hit = since >= 0 ? n * Decay(since, 0.0012) * strength : 0;
            double ring = 0;
            for (var i = 0; i < plates.Length; i++)
                ring += plates[i](hit) * weights[i];
            return ring * 6 + thud(hit) * 3 + ground(n) * 1.2;
        });
    }

    /// <summary>Шорох шин: шум, глухой снизу, и щелчки гравия из-под колёс.</summary>
    public static float[] Roll(double rate)
    {
        var next = Random(1301);
        var hum = Lowpass(rate, 600.0);
        var deep = Lowpass(rate, 90.0);
        var grit = Bandpass(rate, 3200, 2);
        return Loop(rate, LoopSeconds, (_, noise) =>
        {
            var n = noise();
            var stone = next() < 0.0012 ? 0.6 + next() * 0.4 : 0;
            return hum(n) * 0.9 + deep(n) * 2 + grit(stone) * 2.5;
        });
    }

    /// <summary>Скрип разворота на месте: траки трут грунт — шершавый скрежет с металлическим призвуком.</summary>
    public static float[] Squeal(double rate)
    {
        var bodies = new[] { Bandpass(rate, 900, 10), Bandpass(rate, 1850, 14), Bandpass(rate, 2750, 18) };
        double[] weights = [1, 0.6, 0.3];
        var grind = Wanderer(1401);
        return Loop(rate, LoopSeconds, (t, noise) =>
        {
            var scrape = noise() * (0.55 + 0.45 * grind(t * 9));
            double output = 0;
            for (var i = 0; i < bodies.Length; i++)
                output += bodies[i](scrape) * weights[i];
            return output;
        });
    }

    /// <summary>Визг шин в крутом повороте: тон резины, дрожащий от проскальзывания, и шорох.</summary>
    public static float[] Screech(double rate)
    {
        var tire = Bandpass(rate, 1150, 12);
        var second = Bandpass(rate, 2300, 16);
        var slip = Wanderer(1501);
        var phase = 0.0;
        return Loop(rate, LoopSeconds, (t, noise) =>
        {
            var n = noise();
            var wobble = 1 + 0.03 * Sin(PI2 * 7 * t) + 0.02 * (slip(t * 15) - 0.5);
            phase += PI2 * 1150 * wobble / rate;
            return Sin(phase) * (0.3 + 0.2 * slip(t * 6)) + tire(n) * 1.5 + second(n) * 0.8;
        });
    }

    /// <summary>
    /// Стартер: электромотор крутит коленвал, каждый цилиндр на сжатии тормозит его: тон проседает рывками. Потом мотор
    /// схватывает: два-три хлопка выхлопа. heavy — дизель: ниже, медленнее, хлопки тяжелее, с клубом выхлопа.
    /// </summary>
    public static float[] Ignition(double rate, int variant, bool heavy)
    {
        var crank = StartCrank(heavy ? "ignitionDiesel" : "ignition");
        var humTone = heavy ? 95 : 140;
        var pulls = heavy ? 7 : 10;
        var length = crank + 0.28;
        var gears = Bandpass(rate, heavy ? 900 : 1300, 4);
        var body = Lowpass(rate, 600.0);
        var pipe = Bandpass(rate, heavy ? 75 : 105, 2.5);
        var knock = Bandpass(rate, 2400, 3);
        var puff = Lowpass(rate, 400.0);
        var phase = 0.0;
        double[] moments = [0, 0.07, 0.12];
        return Render(rate, length, variant, (t, noise) =>
        {
            var n = noise();
            double output = 0;
            if (t < crank + 0.03)
            {
                var cycle = t * pulls % 1;
                var loadNow = Exp(-cycle * 6);
                phase += humTone * (1 - 0.25 * loadNow) / rate;
                var tone = Sin(PI2 * phase) + 0.35 * Sin(2 * PI2 * phase) + 0.15 * Sin(3 * PI2 * phase);
                var level = (0.45 + 0.55 * (1 - loadNow)) * Attack(t, 0.015) * Min(1, (crank + 0.03 - t) / 0.03);
                output += (tone * 0.4 + body(n) * 0.5 + gears(n) * 0.35) * level;
            }
            if (t >= crank)
            {
                var localT = t - crank;
                for (var k = 0; k < moments.Length; k++)
                {
                    var since = localT - moments[k];
                    if (since >= 0)
                    {
                        var kick = (1.2 - k * 0.2) * (Decay(since, heavy ? 0.012 : 0.008) - Decay(since, 0.0006));
                        output += pipe(kick + n * kick * 0.5) * 4 + kick * 0.5;
                        if (heavy)
                            output += knock(n) * Decay(since, 0.002) * (1 - k * 0.25);
                    }
                }
                if (heavy)
                    output += puff(n) * Decay(localT, 0.1) * 0.8;
                output *= Min(1, (length - t) / 0.08);
            }
            return output;
        });
    }

    /// <summary>Пневмотормоз: щелчок клапана и шипение сброшенного воздуха, которое темнеет и стихает.</summary>
    public static float[] AirBrake(double rate, int variant)
    {
        var t = 0.0;
        var hiss = Highpass(rate, 1800);
        var tone = Lowpass(rate, () => 3000 + 5000 * Decay(t, 0.15));
        var click = Bandpass(rate, 1400, 4);
        return Render(rate, 0.8, variant, (time, noise) =>
        {
            t = time;
            var n = noise();
            return tone(hiss(n)) * Attack(t, 0.006) * Decay(t, 0.22) * 1.6 + click(n) * Decay(t, 0.004) * 3;
        });
    }

    // ------------------------------------------------------------------ голос машины

    /// <summary>Узлы голоса мотора: куда писать ручки.</summary>
    internal readonly record struct Nodes(int Tone, int Engine, int Idle, int Loaded, int IdleGain, int LoadGain, int Ground, int GroundGain, int Scrape, int ScrapeGain);

    private const float Glide = 0.06f;
    private const float RunningLevel = 0.55f;
    private const float SquealLevel = 0.35f;

    /// <summary>
    /// Голос одной машины (createMotorVoice): две петли мотора крутятся с одной скоростью и с одного места — такты
    /// совпадают, голос перетекает от холостой к нагруженной. Под ними ход и скрип разворота. Всё через срез верхов: на
    /// газу и вблизи звук ярче. running и squeal — имена петель хода и скрипа (или null); offset — одно на обе петли.
    /// </summary>
    public static (VoiceGraph Graph, Nodes Nodes) Voice(string motor, string? running, string? squeal, float offset)
    {
        var s = new VoiceGraphBuilder();
        var tone = s.Filter(FilterType.Lowpass, 500, 0.7f);
        var engine = s.Gain(0);
        tone.To(engine).To(s.Out);
        // Одна случайная точка начала на обе петли мотора: такты холостой и нагруженной совпадают.
        var idle = s.Sample("motor:" + motor + ":idle", loop: true, offset: offset);
        var loaded = s.Sample("motor:" + motor + ":load", loop: true, offset: offset);
        var idleGain = s.Gain(0);
        var loadGain = s.Gain(0);
        idle.To(idleGain).To(tone);
        loaded.To(loadGain).To(tone);
        int ground = -1, groundGain = -1, scrape = -1, scrapeGain = -1;
        if (running is not null)
        {
            var source = s.Sample("running:" + running, loop: true);
            var gain = s.Gain(0);
            source.To(gain).To(s.Out);
            (ground, groundGain) = (source.Index, gain.Index);
        }
        if (squeal is not null)
        {
            var source = s.Sample("running:" + squeal, loop: true);
            var gain = s.Gain(0);
            source.To(gain).To(s.Out);
            (scrape, scrapeGain) = (source.Index, gain.Index);
        }
        return (s.Build(), new Nodes(tone.Index, engine.Index, idle.Index, loaded.Index, idleGain.Index, loadGain.Index, ground, groundGain, scrape, scrapeGain));
    }

    /// <summary>Ручки голоса: rate (скорость петли), load (0..1), level, running, runningRate, squeal, clarity.</summary>
    public static void Update(Sound voice, in Nodes n, in MotorKnobs knobs)
    {
        var rate = (float)knobs.Rate;
        voice.Glide(n.Idle, ParamId.Rate, MathF.Max(0.1f, rate), Glide);
        voice.Glide(n.Loaded, ParamId.Rate, MathF.Max(0.1f, rate), Glide);
        // Переход холостая — нагрузка по четверти круга: суммарная громкость не проседает посередине.
        var angle = Clamp(knobs.Load, 0, 1) * PI / 2;
        voice.Glide(n.IdleGain, ParamId.Gain, (float)Cos(angle), Glide);
        voice.Glide(n.LoadGain, ParamId.Gain, (float)(Sin(angle) * 1.2), Glide);
        voice.Glide(n.Engine, ParamId.Gain, (float)knobs.Level, Glide * 2);
        // На газу и на высоких оборотах звук светлее; издалека верхи тонут.
        var bright = (500 + 3500 * knobs.Load) * (0.7 + 0.3 * knobs.Rate);
        voice.Glide(n.Tone, ParamId.Frequency, (float)Min(16000, bright * (0.35 + 0.65 * knobs.Clarity)), Glide);
        if (n.Ground >= 0)
        {
            voice.Glide(n.Ground, ParamId.Rate, (float)Max(0.05, knobs.RunningRate), Glide);
            voice.Glide(n.GroundGain, ParamId.Gain, (float)knobs.Running * RunningLevel, Glide * 2);
        }
        if (n.Scrape >= 0)
            voice.Glide(n.ScrapeGain, ParamId.Gain, (float)knobs.Squeal * SquealLevel, Glide * 2);
    }
}
