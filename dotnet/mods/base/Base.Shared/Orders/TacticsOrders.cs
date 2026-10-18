using MemoryPack;

namespace Base.Shared.Orders;

// Приказы тактики (Tactics).

/// <summary>stance: стойка своих вооружённых юнитов — aggressive, defensive, hold или passive.</summary>
[MemoryPackable]
public sealed partial class StanceOrder
{
    public const string Name = "stance";
    public uint[] Units { get; set; } = [];
    public string Stance { get; set; } = "";
}

/// <summary>patrol: патруль по кругу от места через точки (x и y подряд); Append — дописать к нынешнему.</summary>
[MemoryPackable]
public sealed partial class PatrolOrder
{
    public const string Name = "patrol";
    public uint[] Units { get; set; } = [];
    public double[] Points { get; set; } = [];
    public bool Append { get; set; }
}
