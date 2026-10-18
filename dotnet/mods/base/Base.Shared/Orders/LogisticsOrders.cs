using MemoryPack;

namespace Base.Shared.Orders;

// Приказы логистики (Logistics). Грузы — имена записей resources, products, ores.

/// <summary>haul: привязать свои грузовики к своей шахте. Только изнутри.</summary>
[MemoryPackable]
public sealed partial class HaulOrder
{
    public const string Name = "haul";
    public uint[] Units { get; set; } = [];
    public uint Mine { get; set; }
    public bool Queue { get; set; }
}

/// <summary>pickup: грузовики вывозят дроп, пока он не опустеет. Только изнутри.</summary>
[MemoryPackable]
public sealed partial class PickupOrder
{
    public const string Name = "pickup";
    public uint[] Units { get; set; } = [];
    public uint Drop { get; set; }
    public bool Queue { get; set; }
}

/// <summary>supply: грузовики обеспечивают грузом своё здание или стройку. Только изнутри.</summary>
[MemoryPackable]
public sealed partial class SupplyOrder
{
    public const string Name = "supply";
    public uint[] Units { get; set; } = [];
    public uint Target { get; set; }
    public bool Queue { get; set; }
}

/// <summary>route: маршрут грузовиков — свои здания-остановки по кругу; меньше двух — снять.</summary>
[MemoryPackable]
public sealed partial class RouteOrder
{
    public const string Name = "route";
    public uint[] Units { get; set; } = [];
    public uint[] Stops { get; set; } = [];
}

/// <summary>serve: грузовики возят только по заявкам этих своих зданий; пусто — снять.</summary>
[MemoryPackable]
public sealed partial class ServeOrder
{
    public const string Name = "serve";
    public uint[] Units { get; set; } = [];
    public uint[] Buildings { get; set; } = [];
}

/// <summary>filter: какие грузы возить; пусто — любые.</summary>
[MemoryPackable]
public sealed partial class FilterOrder
{
    public const string Name = "filter";
    public uint[] Units { get; set; } = [];
    public string[] Goods { get; set; } = [];
}

/// <summary>carry: перевезти amount груза из from в to; from = 0 — то, что уже в кузове.</summary>
[MemoryPackable]
public sealed partial class CarryOrder
{
    public const string Name = "carry";
    public uint[] Units { get; set; } = [];
    public uint From { get; set; }
    public uint To { get; set; }
    public string Resource { get; set; } = "";
    public double Amount { get; set; }
}

/// <summary>reserve: резерв своих хранилищ — столько они не выдают никому, кроме продажи; ноль — снять.</summary>
[MemoryPackable]
public sealed partial class ReserveOrder
{
    public const string Name = "reserve";
    public uint[] Buildings { get; set; } = [];
    public double Amount { get; set; }
}

/// <summary>want: заказ станций запроса — сколько груза держать; ноль — не заказывать.</summary>
[MemoryPackable]
public sealed partial class WantOrder
{
    public const string Name = "want";
    public uint[] Buildings { get; set; } = [];
    public string Resource { get; set; } = "";
    public double Amount { get; set; }
}
