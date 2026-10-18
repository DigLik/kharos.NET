namespace Base.Shared.Data;

/// <summary>Турель на носителе: вид из turrets.json, место вперёд и вправо от центра (тайлы).</summary>
public sealed class MountSpec
{
    public string Turret { get; set; } = "";
    public double Along { get; set; }
    public double Across { get; set; }
}

/// <summary>Луч: радиус, груза за секунду; give, take — отдаёт ли и берёт ли.</summary>
public sealed class BeamSpec
{
    public double Radius { get; set; }
    public double Rate { get; set; }
    public bool Give { get; set; } = true;
    public bool Take { get; set; } = true;
}

/// <summary>Снабжение боеприпасами вокруг себя.</summary>
public sealed class SuppliesSpec
{
    public double Radius { get; set; }
    public double Rate { get; set; }
}

/// <summary>Облик пилота для подачи base (подпись, мотор, тон, выезд из ворот, цвет): игре он не нужен.</summary>
public sealed class LookSpec
{
    public string? Title { get; set; }
    public string? Motor { get; set; }
    public double? Pitch { get; set; }
    public double[]? Exit { get; set; }
    public double? Color { get; set; }
}

/// <summary>
/// Вид юнита (units.json, UnitSpec в units.ts). <see cref="Kind"/> — броня: infantry, vehicle, heavy, air;
/// <see cref="Chassis"/> — ходовая, запись moves.json.
/// </summary>
public sealed class UnitSpec
{
    public string? About { get; set; }
    public double Speed { get; set; }
    public double Turn { get; set; }
    public double Radius { get; set; }
    public double Cost { get; set; }
    public double BuildTime { get; set; }
    public string Kind { get; set; } = "";
    public double Hp { get; set; }
    public double Supply { get; set; }
    public string Chassis { get; set; } = "";
    public string? Weapon { get; set; }
    public double? Repair { get; set; }
    public MountSpec[] Mounts { get; set; } = [];
    public Dictionary<string, double> Materials { get; set; } = [];
    public string[] Requires { get; set; } = [];
    public string? Research { get; set; }
    public double? Inventory { get; set; }
    public string[] Accepts { get; set; } = [];
    public bool Hauls { get; set; }
    public BeamSpec? Beam { get; set; }
    public double? Sight { get; set; }
    public double? Ammo { get; set; }
    public SuppliesSpec? Supplies { get; set; }
    public LookSpec? Look { get; set; }
}

/// <summary>Турель (turrets.json, TurretSpec в turrets.ts).</summary>
public sealed class TurretSpec
{
    public string? About { get; set; }
    public double Turn { get; set; }
    public double Radius { get; set; }
    public string? Weapon { get; set; }
    public double? Repair { get; set; }
    public LookSpec? Look { get; set; }
}

/// <summary>Оружие (weapons.json, WeaponSpec в weapons.ts). <see cref="Vs"/> — множитель урона по броне.</summary>
public sealed class WeaponSpec
{
    public string? About { get; set; }
    public string Shot { get; set; } = "";
    public double Range { get; set; }
    public double Damage { get; set; }
    public double Reload { get; set; }
    public bool Air { get; set; }
    public double? Speed { get; set; }
    public double? Splash { get; set; }
    public double? Chain { get; set; }
    public double? MinRange { get; set; }
    public bool Lob { get; set; }
    public bool OnlyAir { get; set; }
    public Dictionary<string, double> Vs { get; set; } = [];
    public double? Ammo { get; set; }
}
