using MemoryPack;

namespace Base.Shared.Orders;

// Приказы стройки (Construction). Тайлы протяжки — x и y подряд, по порядку.

/// <summary>build: заложить здание левым верхним углом в тайл (x, y) и послать своих строителей.</summary>
[MemoryPackable]
public sealed partial class BuildOrder
{
    public const string Name = "build";
    public string Building { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public uint[] Builders { get; set; } = [];
    public bool Queue { get; set; }
}

/// <summary>assist: своих строителей — на свою стройку. Только изнутри (цель по номеру).</summary>
[MemoryPackable]
public sealed partial class AssistOrder
{
    public const string Name = "assist";
    public uint[] Units { get; set; } = [];
    public uint Site { get; set; }
    public bool Queue { get; set; }
}

/// <summary>cancelBuild: отменить свою стройку с возвратом кредитов; у здания под разбор — отменить разбор.</summary>
[MemoryPackable]
public sealed partial class CancelBuildOrder
{
    public const string Name = "cancelBuild";
    public uint Site { get; set; }
}

/// <summary>demolish: назначить своё здание под разбор и послать строителей.</summary>
[MemoryPackable]
public sealed partial class DemolishOrder
{
    public const string Name = "demolish";
    public uint Building { get; set; }
    public uint[] Builders { get; set; } = [];
}

/// <summary>pave: заложить покрытие (road или foundation) в тайлы; дорога по болоту — мост.</summary>
[MemoryPackable]
public sealed partial class PaveOrder
{
    public const string Name = "pave";
    public string Kind { get; set; } = "road";
    public int[] Tiles { get; set; } = [];
    public uint[] Builders { get; set; } = [];
    public bool Queue { get; set; }
}

/// <summary>unpave: снять своё покрытие с тайлов: недостроенное — сразу с возвратом, готовое разберут.</summary>
[MemoryPackable]
public sealed partial class UnpaveOrder
{
    public const string Name = "unpave";
    public int[] Tiles { get; set; } = [];
    public uint[] Builders { get; set; } = [];
    public bool Queue { get; set; }
}

/// <summary>walls: стена протяжкой по тайлам; Gate — ворота.</summary>
[MemoryPackable]
public sealed partial class WallsOrder
{
    public const string Name = "walls";
    public int[] Tiles { get; set; } = [];
    public uint[] Builders { get; set; } = [];
    public bool Gate { get; set; }
    public bool Queue { get; set; }
}

/// <summary>dig: срыть горы (peak) или сгладить обрывы (cliff) в рамке тайлов.</summary>
[MemoryPackable]
public sealed partial class DigOrder
{
    public const string Name = "dig";
    public string Kind { get; set; } = "peak";
    public int[] Tiles { get; set; } = [];
    public uint[] Builders { get; set; } = [];
    public bool Queue { get; set; }
}
