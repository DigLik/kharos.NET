using Kharos.Mod.Sdk.Client;

namespace Base.Client.Sounds;

/// <summary>
/// Банк звуков base — записи sounds/synth, sounds/motors и sounds/world пилота под теми же именами. Клиент считает их
/// при загрузке (ClientBuilder.Sample), на каждый вариант.
/// </summary>
internal static class SoundBank
{
    /// <summary>Вариантов у каждого разового звука.</summary>
    public const int Variants = 3;

    /// <summary>Запись банка: имя, шина, число вариантов.</summary>
    internal readonly record struct Entry(string Name, Bus Bus, int Variants, BankRender Render);

    /// <summary>Вся запись банка по порядку — её читают регистрация и тесты.</summary>
    public static IReadOnlyList<Entry> All { get; } = Build();

    public static void Register(ClientBuilder client)
    {
        foreach (var entry in All)
            client.Sample(entry.Name, entry.Bus, entry.Variants, entry.Render);
    }

    private static List<Entry> Build()
    {
        var list = new List<Entry>();

        void Add(string name, Bus bus, BankRender render, int variants = Variants) => list.Add(new Entry(name, bus, variants, render));
        void Mono(string name, Bus bus, Func<double, int, float[]> make, int variants = Variants) => Add(name, bus, (rate, variant) => [make(rate, variant)], variants);
        void Looped(string name, Func<double, float[]> make) => Add(name, Bus.World, (rate, _) => [make(rate)], 1);
        void Signal(string name, double[] tones, double tone, double gap) => Mono(name, Bus.Interface, (rate, variant) => SynthSounds.Signal(rate, variant, tones, tone, gap));
        void Blip(string name, double from, double to, double length, double click) => Mono(name, Bus.Interface, (rate, variant) => SynthSounds.Blip(rate, variant, from, to, length, click));
        void Chime(string name, double[] notes, double gap, double ring) => Mono(name, Bus.Interface, (rate, variant) => WorldSounds.Chime(rate, variant, notes, gap, ring));
        // Петли шума — в стерео: у каждого уха своя, и ветер обступает со всех сторон (NOISE_SECONDS в bank.ts).
        void Noise(string name, NoiseColor color, double seconds) =>
            Add(name, Bus.World, (rate, _) => [Dsp.NoiseLoop(rate, seconds, 1, color), Dsp.NoiseLoop(rate, seconds, 2, color)], 1);

        // sounds/synth: бой.
        Mono("rifle", Bus.Battle, SynthSounds.Rifle);
        Mono("machinegun", Bus.Battle, SynthSounds.Machinegun);
        Mono("cannon", Bus.Battle, SynthSounds.Cannon);
        Mono("launcher", Bus.Battle, SynthSounds.Launcher);
        Mono("laser", Bus.Battle, SynthSounds.Laser);
        Mono("arc", Bus.Battle, SynthSounds.Arc);
        Mono("blast", Bus.Battle, (rate, variant) => SynthSounds.Explosion(rate, variant, false));
        Mono("bigBlast", Bus.Battle, (rate, variant) => SynthSounds.Explosion(rate, variant, true));
        // Тревога: нас атакуют — два спуска вниз.
        Signal("attacked", [880, 660, 880, 660], 0.11, 0.03);
        // Интерфейс: щелчок кнопки, касание ячейки при наведении, вход в раздел, отказ.
        Blip("click", 1400, 1100, 0.05, 0.6);
        Blip("hover", 2200, 2200, 0.025, 0.3);
        Blip("open", 700, 1300, 0.09, 0.3);
        Blip("deny", 220, 160, 0.16, 0.2);
        // Выделение: юниты — бодрый двойной щелчок, здание — один пониже.
        Signal("select", [1300, 1700], 0.03, 0.015);
        Blip("selectBuilding", 600, 520, 0.08, 0.3);
        // Приказы: закладка здания, движение, атака.
        Signal("place", [520, 780], 0.05, 0.02);
        Blip("order", 900, 1250, 0.07, 0.4);
        Signal("attackOrder", [1050, 700], 0.045, 0.015);
        Signal("spotted", [620, 930], 0.09, 0.05);
        Signal("found", [520, 660, 780], 0.08, 0.03);
        Signal("nuke", [440, 880, 440, 880, 440], 0.2, 0.01);
        Signal("nukeClose", [660, 1320, 660, 1320, 660, 1320, 660, 1320], 0.12, 0.01);
        Signal("ping", [990, 1320, 990], 0.07, 0.02);
        Signal("intercepted", [660, 880, 1320], 0.1, 0.03);
        // Фоновые петли: работа зданий и шаги пехоты.
        Looped("hum", SynthSounds.Hum);
        Looped("machinery", SynthSounds.Machinery);
        Looped("drill", SynthSounds.Drill);
        Looped("steps", SynthSounds.Steps);
        // Звуки дали: играет их фон, редко и сквозь эхо.
        Mono("creak", Bus.World, SynthSounds.Creak);
        Mono("clank", Bus.World, SynthSounds.Clank);
        Mono("thunder", Bus.World, SynthSounds.Thunder);
        // Петли шума для ветра и отклик эха.
        Noise("noise:white", NoiseColor.White, 3);
        Noise("noise:pink", NoiseColor.Pink, 8);
        Noise("noise:brown", NoiseColor.Brown, 8);
        Add("reverb", Bus.World, (rate, _) => Dsp.ReverbImpulse(rate, 4, 1), 1);

        // sounds/motors: петли моторов (холостой ход и под нагрузкой), петли хода, стартеры и тормоз.
        foreach (var (name, motor) in MotorSounds.Motors)
        {
            Looped($"motor:{name}:idle", rate => MotorSounds.Design(rate, motor, false));
            Looped($"motor:{name}:load", rate => MotorSounds.Design(rate, motor, true));
        }
        Looped("running:clatter", MotorSounds.Clatter);
        Looped("running:roll", MotorSounds.Roll);
        Looped("running:squeal", MotorSounds.Squeal);
        Looped("running:screech", MotorSounds.Screech);
        Mono("ignition", Bus.World, (rate, variant) => MotorSounds.Ignition(rate, variant, false));
        Mono("ignitionDiesel", Bus.World, (rate, variant) => MotorSounds.Ignition(rate, variant, true));
        Mono("airBrake", Bus.World, MotorSounds.AirBrake);

        // sounds/world: здания и мир.
        Mono("built", Bus.World, WorldSounds.Built);
        Mono("deploy", Bus.World, WorldSounds.Deploy);
        Mono("rollout", Bus.World, WorldSounds.Rollout);
        Mono("brownout", Bus.Interface, WorldSounds.Brownout);
        Mono("countdown", Bus.Interface, WorldSounds.Beep);
        // Уведомления: машина готова, изучено, улучшено, боеголовка готова, награда, доставлено, защита кончилась.
        Chime("unitReady", [660, 880], 0.08, 0.2);
        Chime("researched", [523, 659, 784, 1047], 0.09, 0.35);
        Chime("upgraded", [587, 880], 0.1, 0.3);
        Chime("warheadReady", [440, 415, 440], 0.16, 0.4);
        Chime("reward", [784, 988, 1175], 0.07, 0.3);
        Chime("delivered", [659, 784, 659], 0.08, 0.25);
        Chime("protectionOver", [880, 659, 523], 0.14, 0.3);

        // sounds/world, остальное: бой (шина боя), щиты, ворота, корабли, деньги и мелочи мира.
        Mono("flame", Bus.Battle, WorldSounds.Flame);
        Mono("flak", Bus.Battle, WorldSounds.Flak);
        Mono("artillery", Bus.Battle, WorldSounds.Artillery);
        Mono("bombFall", Bus.Battle, WorldSounds.BombFall);
        Mono("ricochet", Bus.Battle, WorldSounds.Ricochet);
        Mono("wreck", Bus.Battle, WorldSounds.Wreck);
        Mono("collapse", Bus.Battle, WorldSounds.Collapse);
        Mono("nukeBlast", Bus.Battle, WorldSounds.NukeBlast);
        Mono("shieldHit", Bus.Battle, WorldSounds.ShieldHit);
        Mono("sizzle", Bus.Battle, WorldSounds.Sizzle);
        Mono("burn", Bus.Battle, WorldSounds.Burn);
        Mono("zap", Bus.Battle, WorldSounds.Zap);
        Mono("doorsLocked", Bus.World, WorldSounds.DoorsLocked);
        Mono("liftoff", Bus.World, WorldSounds.Liftoff);
        Mono("shieldDown", Bus.World, WorldSounds.ShieldDown);
        Mono("shieldUp", Bus.World, WorldSounds.ShieldUp);
        Mono("gate", Bus.World, WorldSounds.Gate);
        Mono("crates", Bus.World, WorldSounds.Crates);
        Mono("paved", Bus.World, WorldSounds.Paved);
        Mono("radar", Bus.World, WorldSounds.Radar);
        Mono("shipLaunch", Bus.World, WorldSounds.ShipLaunch);
        Mono("shipLand", Bus.World, WorldSounds.ShipLand);
        Mono("depleted", Bus.World, WorldSounds.Depleted);
        Mono("powerOn", Bus.World, (rate, variant) => WorldSounds.Power(rate, variant, true));
        Mono("powerOff", Bus.World, (rate, variant) => WorldSounds.Power(rate, variant, false));
        Mono("cargo", Bus.World, WorldSounds.Cargo);
        Mono("coins", Bus.Interface, WorldSounds.Coins);
        Mono("gift", Bus.Interface, WorldSounds.Gift);
        Mono("defeat", Bus.Interface, WorldSounds.Defeat);
        Mono("surrender", Bus.Interface, WorldSounds.Surrender);
        Mono("chat", Bus.Interface, (rate, variant) => WorldSounds.ChatBell(rate, variant, 1175));
        Mono("chatSystem", Bus.Interface, (rate, variant) => WorldSounds.ChatBell(rate, variant, 784));
        // Петли мира (WORLD_LOOPS): громкость каждой задаёт тот, кто её играет.
        Looped("overload", WorldLoops.Overload);
        Looped("construct", WorldLoops.Construct);
        Looped("weld", WorldLoops.Weld);
        Looped("tractor", WorldLoops.Tractor);
        Looped("servo", WorldLoops.Servo);
        Looped("reactor", WorldLoops.Reactor);
        Looped("dome", WorldLoops.Dome);
        Looped("hydraulic", WorldLoops.Hydraulic);
        Looped("steam", WorldLoops.Steam);
        Looped("rocket", WorldLoops.Rocket);
        Looped("charge", WorldLoops.Charge);
        Looped("flow", WorldLoops.Flow);
        return list;
    }
}
