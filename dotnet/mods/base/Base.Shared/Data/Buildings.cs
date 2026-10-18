namespace Base.Shared.Data;

/// <summary>Щит: радиус, ёмкость, восстановление в секунду.</summary>
public sealed class ShieldSpec
{
    public double Radius { get; set; }
    public double Capacity { get; set; }
    public double Regen { get; set; }
}

/// <summary>Накопитель: ёмкость и отдача в секунду.</summary>
public sealed class BatterySpec
{
    public double Capacity { get; set; }
    public double Rate { get; set; }
}

/// <summary>Топливо реактора: изделие и сколько секунд горит единица.</summary>
public sealed class BurnsSpec
{
    public string Good { get; set; } = "";
    public double Seconds { get; set; }
}

/// <summary>Авария при разрушении: радиус и урон.</summary>
public sealed class MeltdownSpec
{
    public double Radius { get; set; }
    public double Damage { get; set; }
}

/// <summary>Взрыв боеголовки: радиус, ядро полного урона, урон.</summary>
public sealed class BlastSpec
{
    public double Radius { get; set; }
    public double Core { get; set; }
    public double Damage { get; set; }
}

/// <summary>Боеголовка ракетной шахты.</summary>
public sealed class WarheadSpec
{
    public double Cost { get; set; }
    public Dictionary<string, double> Materials { get; set; } = [];
    public double Seconds { get; set; }
    public double Launch { get; set; }
    public double Speed { get; set; }
    public BlastSpec Blast { get; set; } = new();
}

/// <summary>Перехватчик: радиус, цена и материалы ракеты, сборка, скорость, запас.</summary>
public sealed class InterceptSpec
{
    public double Radius { get; set; }
    public double Cost { get; set; }
    public Dictionary<string, double> Materials { get; set; } = [];
    public double Seconds { get; set; }
    public double Speed { get; set; }
    public double Capacity { get; set; }
}

/// <summary>Вид здания (buildings.json, BuildingSpec в buildings.ts). Размер — в тайлах, левый верхний угол — место.</summary>
public sealed class BuildingSpec
{
    public string? About { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Cost { get; set; }
    public double? Zone { get; set; }
    public double? Expand { get; set; }
    public bool Defense { get; set; }
    public double? Hp { get; set; }
    public MountSpec[] Mounts { get; set; } = [];
    public double? Sight { get; set; }
    public bool Gate { get; set; }
    public double? Scan { get; set; }
    public double? Power { get; set; }
    public bool Extract { get; set; }
    public Dictionary<string, double> Materials { get; set; } = [];
    public double? Inventory { get; set; }
    public string[] Accepts { get; set; } = [];
    public Dictionary<string, double> Limits { get; set; } = [];
    public bool Stores { get; set; }
    public string? Refines { get; set; }
    public string? Assembles { get; set; }
    public bool Ammo { get; set; }
    public BeamSpec? Beam { get; set; }
    public bool Depot { get; set; }
    public bool Rearms { get; set; }
    public bool Requester { get; set; }
    public bool Trades { get; set; }
    public string[] Produces { get; set; } = [];
    public double? Income { get; set; }
    public bool Crowding { get; set; }
    public double? Repair { get; set; }
    public string? Pipe { get; set; }
    public bool Isolated { get; set; }
    public bool Researches { get; set; }
    public ShieldSpec? Shield { get; set; }
    public BatterySpec? Battery { get; set; }
    public BurnsSpec? Burns { get; set; }
    public MeltdownSpec? Meltdown { get; set; }
    public WarheadSpec? Warhead { get; set; }
    public InterceptSpec? Intercept { get; set; }
    public string[] Requires { get; set; } = [];
    public string? Research { get; set; }
    public double? Menu { get; set; }
    public LookSpec? Look { get; set; }
}
