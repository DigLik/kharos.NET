using MemoryPack;

namespace Base.Shared.Orders;

// Приказы труб (Pipes).

/// <summary>pipes: наземная труба по тайлам (x и y подряд, по порядку).</summary>
[MemoryPackable]
public sealed partial class PipesOrder
{
    public const string Name = "pipes";
    public int[] Tiles { get; set; } = [];
    public uint[] Builders { get; set; } = [];
    public bool Queue { get; set; }
}

/// <summary>wells: колодец (x, y) или пара — концы подземной трубы.</summary>
[MemoryPackable]
public sealed partial class WellsOrder
{
    public const string Name = "wells";
    public int[] Tiles { get; set; } = [];
    public uint[] Builders { get; set; } = [];
    public bool Queue { get; set; }
}
