using MemoryPack;

namespace Base.Shared.Queries;

// Запросы обороны (Host.Query: тесты, администратор, редактор) — только чтение мира; урон куполам запрос считает в памяти.

/// <summary>Аргументы «юнит вида Unit стреляет оружием Weapon».</summary>
[MemoryPackable]
public sealed partial class ShotCostArgs
{
    public string Unit { get; set; } = "";
    public string Weapon { get; set; } = "";
}

/// <summary>Купол, который держится сейчас (Dome в shields.ts): генератор, хозяин, центр, радиус и запас.</summary>
[MemoryPackable]
public sealed partial class DomeInfo
{
    public uint Entity { get; set; }
    public uint Player { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Radius { get; set; }
    public double Charge { get; set; }
}

/// <summary>Купола тика по порядку генераторов.</summary>
[MemoryPackable]
public sealed partial class DomeList
{
    public DomeInfo[] Items { get; set; } = [];
}

/// <summary>Попадание по цели игрока Player в (X, Y), пришедшее из (FromX, FromY); FromAir — стрелял летающий.</summary>
[MemoryPackable]
public sealed partial class AbsorbHit
{
    public uint Player { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Damage { get; set; }
    public double FromX { get; set; }
    public double FromY { get; set; }
    public bool FromAir { get; set; }
}

/// <summary>Попадания подряд (absorb): каждое видит запас, оставшийся после прежних.</summary>
[MemoryPackable]
public sealed partial class AbsorbArgs
{
    public AbsorbHit[] Hits { get; set; } = [];
}

/// <summary>Запас купола после попаданий: тик последнего погашенного попадания (−1 — не было) и тик, до которого купол погашен.</summary>
[MemoryPackable]
public sealed partial class DomeState
{
    public uint Entity { get; set; }
    public double Charge { get; set; }
    public double Hit { get; set; }
    public double Down { get; set; }
}

/// <summary>Сколько урона прошло сквозь щит при каждом попадании и в каком состоянии остались задетые купола (мир запрос не меняет).</summary>
[MemoryPackable]
public sealed partial class AbsorbResult
{
    public double[] Passed { get; set; } = [];
    public DomeState[] Domes { get; set; } = [];
}

public static class DefenseQueries
{
    /// <summary>Магазин юнита вида (magazineOf): UnitArgs (Unit) → double; ноль — стреляет бесплатно.</summary>
    public const string Magazine = "defense.magazine";

    /// <summary>Сколько боеприпасов тратит выстрел (shotCost): ShotCostArgs → double.</summary>
    public const string ShotCost = "defense.shotCost";

    /// <summary>Пуст ли магазин юнита (isEmpty): EntityArgs → bool.</summary>
    public const string Empty = "defense.empty";

    /// <summary>Купола, которые держатся сейчас (domes): без аргументов → DomeList.</summary>
    public const string Domes = "defense.domes";

    /// <summary>Доля прочности здания вида на месте (durabilityOf, Construction): BuildArgs (Type, X, Y) → double. Для тестов обороны на песке.</summary>
    public const string Durability = "defense.durability";

    /// <summary>Гасит урон куполами (absorb): AbsorbArgs → AbsorbResult. Мир не меняется: состояние куполов — в ответе.</summary>
    public const string Absorb = "defense.absorb";
}
