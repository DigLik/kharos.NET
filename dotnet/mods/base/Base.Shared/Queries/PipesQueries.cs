using MemoryPack;

namespace Base.Shared.Queries;

// Запросы труб (Host.Query): только чтение мира — для тестов, администратора и подсказок протяжки.

/// <summary>Аргументы протяжки: игрок Player, тайлы Tiles (x, y подряд), вид Kind (pipe или well).</summary>
[MemoryPackable]
public sealed partial class PipeStrokeArgs
{
    public uint Player { get; set; }
    public int[] Tiles { get; set; } = [];
    public string Kind { get; set; } = "pipe";
}

/// <summary>Аргументы «тайл (X, Y) игрока Player под трубу вида Kind».</summary>
[MemoryPackable]
public sealed partial class PipeTileArgs
{
    public uint Player { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public string Kind { get; set; } = "pipe";
}

/// <summary>Колодцы, с которыми свяжется новый (wellPartners): тайлы и готов ли каждый.</summary>
[MemoryPackable]
public sealed partial class WellList
{
    public int[] X { get; set; } = [];
    public int[] Y { get; set; } = [];
    public bool[] Ready { get; set; } = [];
}

/// <summary>Сеть труб: здания и трубы (главное — первым), подключённые стройки, рёбра графа a, b, длина подряд.</summary>
[MemoryPackable]
public sealed partial class NetworkInfo
{
    public uint[] Buildings { get; set; } = [];
    public uint[] Sites { get; set; } = [];
    public double[] Edges { get; set; } = [];
}

/// <summary>Имена запросов труб.</summary>
public static class PipesQueries
{
    /// <summary>Сеть здания, трубы или подключённой стройки (networkOf): EntityArgs → NetworkInfo (не в сети — пусто).</summary>
    public const string Network = "pipes.network";

    /// <summary>Какие тайлы протяжки можно заложить (pipeStroke): PipeStrokeArgs → FlagList.</summary>
    public const string Stroke = "pipes.stroke";

    /// <summary>Своя труба другого вида в тайле, вместо которой ляжет новая (pipeSwap): PipeTileArgs → uint (0 — нет).</summary>
    public const string Swap = "pipes.swap";

    /// <summary>С какими колодцами свяжется колодец в тайле (wellPartners): PipeTileArgs → WellList.</summary>
    public const string Partners = "pipes.partners";
}
