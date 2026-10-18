using MemoryPack;

namespace Base.Shared.Orders;

// Приказы ядра (Kernel/Commands): те же имена и поля, что у Command в packages/sim-ts/src/commands.ts. Сущности —
// номерами, виды — именами записей data/*.json, тайлы — целыми. Queue — Shift: встать в конец очереди юнитов.

/// <summary>order: приказ точкой (x, y), можно дробной; что делать — атаковать, копать, возить, строить, чинить или идти, — решает мод.</summary>
[MemoryPackable]
public sealed partial class PointOrder
{
    public const string Name = "order";
    public uint[] Units { get; set; } = [];
    public double X { get; set; }
    public double Y { get; set; }
    public bool Queue { get; set; }
}

/// <summary>move: свои юниты идут к тайлу (x, y).</summary>
[MemoryPackable]
public sealed partial class MoveOrder
{
    public const string Name = "move";
    public uint[] Units { get; set; } = [];
    public int X { get; set; }
    public int Y { get; set; }
    public bool Queue { get; set; }
}

/// <summary>stop: свои юниты бросают дела и очередь и встают, где стоят.</summary>
[MemoryPackable]
public sealed partial class StopOrder
{
    public const string Name = "stop";
    public uint[] Units { get; set; } = [];
}

/// <summary>rally: точка сбора своего производящего здания.</summary>
[MemoryPackable]
public sealed partial class RallyOrder
{
    public const string Name = "rally";
    public uint Building { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>deploy: развернуть свой MCV — на месте или (At) доехать до места с левым верхним тайлом (x, y).</summary>
[MemoryPackable]
public sealed partial class DeployOrder
{
    public const string Name = "deploy";
    public uint Unit { get; set; }
    public bool At { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>cancelDeploy: отменить поездку или разворачивание MCV.</summary>
[MemoryPackable]
public sealed partial class CancelDeployOrder
{
    public const string Name = "cancelDeploy";
    public uint Unit { get; set; }
}

/// <summary>pack: свернуть своё главное здание обратно в MCV.</summary>
[MemoryPackable]
public sealed partial class PackOrder
{
    public const string Name = "pack";
    public uint Building { get; set; }
}

/// <summary>produce: заказать юнит (запись units.json) у своего производителя.</summary>
[MemoryPackable]
public sealed partial class ProduceOrder
{
    public const string Name = "produce";
    public uint Producer { get; set; }
    public string Unit { get; set; } = "";
}

/// <summary>cancelProduction: отменить последний заказ в очереди производителя.</summary>
[MemoryPackable]
public sealed partial class CancelProductionOrder
{
    public const string Name = "cancelProduction";
    public uint Producer { get; set; }
}
