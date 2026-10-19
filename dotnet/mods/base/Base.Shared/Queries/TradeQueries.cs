using MemoryPack;

namespace Base.Shared.Queries;

// Запросы торговли (Host.Query: тесты, администратор, редактор) — только чтение мира.

/// <summary>Аргументы «игрок и космопорт».</summary>
[MemoryPackable]
public sealed partial class PortArgs
{
    public uint Player { get; set; }
    public uint Port { get; set; }
    public string Good { get; set; } = "";
}

/// <summary>Аргументы «юниты игрока Player, которых он хочет отдать».</summary>
[MemoryPackable]
public sealed partial class GiftArgs
{
    public uint Player { get; set; }
    public uint[] Units { get; set; } = [];
}

/// <summary>Корабль с подарком (Transfer и его груз): нет компонента — Present ложь.</summary>
[MemoryPackable]
public sealed partial class TransferInfo
{
    public bool Present { get; set; }
    public uint To { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Credits { get; set; }
    public double Left { get; set; }
    public double Total { get; set; }
    public bool Done { get; set; }
    public Dictionary<string, double> Goods { get; set; } = [];
}

/// <summary>Запас игрока в хранилищах его зон (stockOf): груз → сколько, и сколько помещается всего.</summary>
[MemoryPackable]
public sealed partial class StockInfo
{
    public string[] Goods { get; set; } = [];
    public double[] Amounts { get; set; } = [];
    public double Capacity { get; set; }
}

/// <summary>Имена запросов торговли.</summary>
public static class TradeQueries
{
    /// <summary>Может ли игрок продавать и покупать через здание (canSell): PortArgs → bool.</summary>
    public const string CanSell = "trade.canSell";

    /// <summary>Свободен ли космопорт для подарка (canFerry): PortArgs → bool.</summary>
    public const string CanFerry = "trade.canFerry";

    /// <summary>Корабль космопорта с подарком: EntityArgs → TransferInfo.</summary>
    public const string Transfer = "trade.transfer";

    /// <summary>Сколько товара заявки уже привезено в космопорт (deliveredTo): EntityArgs → double.</summary>
    public const string Delivered = "trade.delivered";

    /// <summary>Сколько товара заявки ещё не хватает и никто не везёт (neededBy): EntityArgs → double.</summary>
    public const string Needed = "trade.needed";

    /// <summary>Сколько тиков летит корабль космопорта за seconds (flightTicks): FlightArgs → int.</summary>
    public const string Flight = "trade.flight";

    /// <summary>Какую тему надо изучить, чтобы купить ресурс (buyResearch): PortArgs (Player, Good) → string; ничего — "".</summary>
    public const string BuyResearch = "trade.buyResearch";

    /// <summary>Запас игрока в хранилищах (stockOf): PlayerArgs → StockInfo.</summary>
    public const string Stock = "trade.stock";

    /// <summary>Кого из юнитов можно отдать (giftable): GiftArgs → EntityList.</summary>
    public const string Giftable = "trade.giftable";
}

/// <summary>Аргументы «сколько тиков летит корабль космопорта Port за Seconds секунд».</summary>
[MemoryPackable]
public sealed partial class FlightArgs
{
    public uint Port { get; set; }
    public double Seconds { get; set; }
}
