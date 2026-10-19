using MemoryPack;

namespace Base.Shared.Queries;

// Запросы логистики (Host.Query): только чтение мира — для тестов, администратора и подсказок интерфейса.

/// <summary>Аргументы «склад Entity и груз Good» (имя груза из data).</summary>
[MemoryPackable]
public sealed partial class GoodArgs
{
    public uint Entity { get; set; }
    public string Good { get; set; } = "";
}

/// <summary>Аргументы «грузовик Truck, склад To и груз Good».</summary>
[MemoryPackable]
public sealed partial class TruckArgs
{
    public uint Truck { get; set; }
    public uint To { get; set; }
    public string Good { get; set; } = "";
}

/// <summary>Аргументы «игрок Player и его здание или стройка Target».</summary>
[MemoryPackable]
public sealed partial class TargetArgs
{
    public uint Player { get; set; }
    public uint Target { get; set; }
}

/// <summary>Заявка зоны: куда, что, сколько, важность.</summary>
[MemoryPackable]
public sealed partial class RequestInfo
{
    public uint To { get; set; }
    public string Good { get; set; } = "";
    public double Amount { get; set; }
    public int Priority { get; set; }
}

/// <summary>Список заявок.</summary>
[MemoryPackable]
public sealed partial class RequestList
{
    public RequestInfo[] Items { get; set; } = [];
}

/// <summary>Имена запросов логистики.</summary>
public static class LogisticsQueries
{
    /// <summary>Сколько груза уже едет на склад (incomingTo): GoodArgs → double.</summary>
    public const string Incoming = "logistics.incoming";

    /// <summary>Отмечено ли здание или дроп «не подъехать» (unreachable): EntityArgs → bool.</summary>
    public const string Unreachable = "logistics.unreachable";

    /// <summary>Сколько груза поместится на склад для этого грузовика (spaceFor): TruckArgs → double.</summary>
    public const string SpaceFor = "logistics.spaceFor";

    /// <summary>Ближайшая переработка руды для грузовика (refineryFor): TruckArgs (To не нужен) → uint; нет — 0.</summary>
    public const string RefineryFor = "logistics.refineryFor";

    /// <summary>Можно ли послать грузовики обеспечить здание или стройку (canSupply): TargetArgs → bool.</summary>
    public const string CanSupply = "logistics.canSupply";

    /// <summary>Заявки зон игрока (requestsOf): PlayerArgs → RequestList.</summary>
    public const string Requests = "logistics.requests";
}
