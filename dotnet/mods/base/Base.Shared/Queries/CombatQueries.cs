using MemoryPack;

namespace Base.Shared.Queries;

// Запросы боя (Host.Query: тесты, администратор, редактор) — только чтение мира.

/// <summary>Приказ точкой глазами симуляции: кто, куда, с туманом ли (fog false — редактор: видно всё).</summary>
[MemoryPackable]
public sealed partial class ResolveArgs
{
    public uint Player { get; set; }
    public uint[] Units { get; set; } = [];
    public double X { get; set; }
    public double Y { get; set; }
    public bool Fog { get; set; } = true;
}

/// <summary>Приказ с целью из разбора точки (resolveOrder): имя, юниты, цель (сущность) или тайл.</summary>
[MemoryPackable]
public sealed partial class ResolvedOrder
{
    public string Name { get; set; } = "";
    public uint[] Units { get; set; } = [];
    public uint Target { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>Приказы разбора точки по порядку.</summary>
[MemoryPackable]
public sealed partial class ResolvedList
{
    public ResolvedOrder[] Items { get; set; } = [];
}

public static class CombatQueries
{
    /// <summary>Может ли игрок приказать атаковать цель (canAttack): TargetArgs → bool.</summary>
    public const string CanAttack = "combat.canAttack";

    /// <summary>Может ли сущность воевать — сама или турелями (canFight): EntityArgs → bool.</summary>
    public const string CanFight = "combat.canFight";

    /// <summary>Что юнитам делать с точкой (resolveOrder): ResolveArgs → ResolvedList.</summary>
    public const string Resolve = "combat.resolve";
}
