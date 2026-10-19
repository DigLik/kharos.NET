using Base.Host.Kernel;
using Base.Shared.Data;

namespace Base.Host.Sections.Combat;

/// <summary>Оружие (WeaponSpec в weapons.ts) с тем, что бою нужно каждый выстрел, разобранным заранее.</summary>
internal sealed class Weapon
{
    public required string Name { get; init; }
    public required WeaponSpec Spec { get; init; }

    /// <summary>Номер вида «weapons/имя» — Proto выстрела.</summary>
    public int Proto { get; init; }

    public Shots Shot { get; init; }
    public double Range => Spec.Range;
    public double Damage => Spec.Damage;
    public double Reload => Spec.Reload;
    public double Speed { get; init; }
    public double Splash { get; init; }
    public bool HasSplash { get; init; }
    public int Chain { get; init; }
    public double MinRange { get; init; }
    public bool Air => Spec.Air;
    public bool OnlyAir => Spec.OnlyAir;
    public bool Lob => Spec.Lob;
    public double Ammo { get; init; }

    /// <summary>Множитель урона по броне (<see cref="Arms.Armor"/>).</summary>
    public required double[] Vs { get; init; }

    /// <summary>Останавливает ли выстрел стена (STOPS_AT_WALL).</summary>
    public bool StopsAtWall => Shot is Shots.Bullet or Shots.Shell or Shots.Laser or Shots.Flame;
}

/// <summary>Чем стреляет оружие (ShotKind).</summary>
internal enum Shots
{
    Bullet,
    Shell,
    Rocket,
    Laser,
    Arc,
    Flame,
}

/// <summary>Вид юнита глазами боя.</summary>
internal sealed class UnitArms
{
    public required string Type { get; init; }
    public required UnitSpec Spec { get; init; }
    public int Armor { get; init; }
    public bool Air { get; init; }
    public bool Foot { get; init; }
    public Weapon? Weapon { get; init; }
    public double Sight { get; init; }
    public double MaxHarm { get; init; }

    /// <summary>Урон местности в секунду по ходовой (moves.json harm): ключ — toxic, brine…</summary>
    public required Dictionary<string, double> Harm { get; init; }
}

/// <summary>Вид турели глазами боя.</summary>
internal sealed class TurretArms
{
    public required string Type { get; init; }
    public double Turn { get; init; }
    public double Radius { get; init; }
    public Weapon? Weapon { get; init; }
}

/// <summary>Вид здания глазами боя.</summary>
internal sealed class BuildingArms
{
    public required string Type { get; init; }
    public required BuildingSpec Spec { get; init; }
    public double Hp { get; init; }
    public bool Wall { get; init; }
    public bool Gate { get; init; }

    /// <summary>Потребитель энергии: стреляет на долю энергии зоны.</summary>
    public bool Consumer { get; init; }
}

/// <summary>
/// Виды глазами боя по номерам Proto (weapons.ts, turrets.ts, units.ts): разбираются раз на вид и живут, пока жива схема
/// (<see cref="Refs.Load"/> их сбрасывает). Производное от данных, не состояние партии.
/// </summary>
internal static class Arms
{
    /// <summary>Сколько единиц прочности у здания на кредит его цены (BUILDING_HP).</summary>
    public const double BuildingHp = 2;

    /// <summary>Брони по порядку: класс юнита или здание.</summary>
    public const int Infantry = 0, Vehicle = 1, Heavy = 2, AirArmor = 3, BuildingArmor = 4;

    private static readonly string[] ArmorNames = ["infantry", "vehicle", "heavy", "air", "building"];

    private static readonly Dictionary<int, UnitArms?> Units = [];
    private static readonly Dictionary<int, TurretArms?> Turrets = [];
    private static readonly Dictionary<int, BuildingArms?> Buildings = [];
    private static readonly Dictionary<string, Weapon?> Weapons = new(StringComparer.Ordinal);

    public static void Reset()
    {
        Units.Clear();
        Turrets.Clear();
        Buildings.Clear();
        Weapons.Clear();
    }

    /// <summary>Номер брони класса юнита.</summary>
    public static int Armor(string kind) => Math.Max(0, Array.IndexOf(ArmorNames, kind));

    /// <summary>Прочность здания вида (buildingHp).</summary>
    public static double HpOf(BuildingSpec spec) => spec.Hp ?? spec.Cost * BuildingHp;

    /// <summary>Оружие по имени (weapons.json); нет — null.</summary>
    public static Weapon? WeaponOf(string? name)
    {
        if (name is null)
            return null;
        if (Weapons.TryGetValue(name, out var found))
            return found;
        Weapon? weapon = null;
        if (Core.Tables.Weapons.TryGetValue(name, out var spec))
        {
            var vs = new double[ArmorNames.Length];
            for (var i = 0; i < vs.Length; i++)
                vs[i] = spec.Vs.TryGetValue(ArmorNames[i], out var factor) ? factor : 1;
            weapon = new Weapon
            {
                Name = name,
                Spec = spec,
                Proto = Core.Proto("weapons", name),
                Shot = spec.Shot switch
                {
                    "shell" => Shots.Shell,
                    "rocket" => Shots.Rocket,
                    "laser" => Shots.Laser,
                    "arc" => Shots.Arc,
                    "flame" => Shots.Flame,
                    _ => Shots.Bullet,
                },
                Speed = spec.Speed ?? 0,
                Splash = spec.Splash ?? 0,
                HasSplash = (spec.Splash ?? 0) != 0,
                Chain = (int)(spec.Chain ?? 0),
                MinRange = spec.MinRange ?? 0,
                Ammo = spec.Ammo ?? 0,
                Vs = vs,
            };
        }
        Weapons[name] = weapon;
        return weapon;
    }

    /// <summary>Оружие выстрела по его Proto; нет — null.</summary>
    public static Weapon? WeaponByProto(int proto) => Core.ProtoName(proto, "weapons") is { } name ? WeaponOf(name) : null;

    /// <summary>Вид юнита по Proto; не юнит — null.</summary>
    public static UnitArms? Unit(int proto)
    {
        if (Units.TryGetValue(proto, out var found))
            return found;
        UnitArms? arms = null;
        if (Core.ProtoName(proto, "units") is { } type && Factory.Unit(type) is { } spec)
        {
            var harm = Core.Tables.Moves.TryGetValue(spec.Chassis, out var chassis) ? chassis.Harm : [];
            arms = new UnitArms
            {
                Type = type,
                Spec = spec,
                Armor = Armor(spec.Kind),
                Air = spec.Kind == "air",
                Foot = spec.Kind == "infantry",
                Weapon = WeaponOf(spec.Weapon),
                Sight = Factory.UnitSight(type),
                Harm = harm,
                MaxHarm = harm.Count > 0 ? harm.Values.Max() : 0,
            };
        }
        Units[proto] = arms;
        return arms;
    }

    /// <summary>Вид турели по Proto; не турель — null.</summary>
    public static TurretArms? Turret(int proto)
    {
        if (Turrets.TryGetValue(proto, out var found))
            return found;
        TurretArms? arms = null;
        if (Core.ProtoName(proto, "turrets") is { } type && Core.Tables.Turrets.TryGetValue(type, out var spec))
            arms = new TurretArms { Type = type, Turn = spec.Turn, Radius = spec.Radius, Weapon = WeaponOf(spec.Weapon) };
        Turrets[proto] = arms;
        return arms;
    }

    /// <summary>Вид здания по Proto; не здание — null.</summary>
    public static BuildingArms? Building(int proto)
    {
        if (Buildings.TryGetValue(proto, out var found))
            return found;
        BuildingArms? arms = null;
        if (Core.ProtoName(proto, "buildings") is { } type && Factory.Building(type) is { } spec)
            arms = new BuildingArms
            {
                Type = type,
                Spec = spec,
                Hp = HpOf(spec),
                Wall = type == "wall",
                Gate = type == "gate",
                Consumer = (spec.Power ?? 0) < 0,
            };
        Buildings[proto] = arms;
        return arms;
    }

    /// <summary>Оружие юнита или турели по Proto (weaponOf); безоружен — null.</summary>
    public static Weapon? Gun(int proto) => Unit(proto)?.Weapon ?? Turret(proto)?.Weapon;
}
