using MemoryPack;

namespace Base.Shared.Orders;

// Приказы ракетных шахт (Missiles).

/// <summary>warhead: начать сборку боеголовки в своей пустой шахте; цена списывается сразу.</summary>
[MemoryPackable]
public sealed partial class WarheadOrder
{
    public const string Name = "warhead";
    public uint Silo { get; set; }
}

/// <summary>cancelWarhead: бросить сборку с возвратом цены.</summary>
[MemoryPackable]
public sealed partial class CancelWarheadOrder
{
    public const string Name = "cancelWarhead";
    public uint Silo { get; set; }
}

/// <summary>launch: пустить готовую боеголовку в тайл (x, y).</summary>
[MemoryPackable]
public sealed partial class LaunchOrder
{
    public const string Name = "launch";
    public uint Silo { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>abortLaunch: отменить пуск, пока ракета не стартовала.</summary>
[MemoryPackable]
public sealed partial class AbortLaunchOrder
{
    public const string Name = "abortLaunch";
    public uint Silo { get; set; }
}
