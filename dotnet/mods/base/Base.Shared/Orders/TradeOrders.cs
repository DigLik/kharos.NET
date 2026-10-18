using MemoryPack;

namespace Base.Shared.Orders;

// Приказы торговли (Trade): космопорт, подарки союзникам.

/// <summary>sell: заявка на продажу до amount единиц ресурса.</summary>
[MemoryPackable]
public sealed partial class SellOrder
{
    public const string Name = "sell";
    public uint Port { get; set; }
    public string Resource { get; set; } = "";
    public double Amount { get; set; }
}

/// <summary>buy: закупить amount единиц ресурса с орбиты.</summary>
[MemoryPackable]
public sealed partial class BuyOrder
{
    public const string Name = "buy";
    public uint Port { get; set; }
    public string Resource { get; set; } = "";
    public double Amount { get; set; }
}

/// <summary>closeSale: закрыть заявку раньше срока.</summary>
[MemoryPackable]
public sealed partial class CloseSaleOrder
{
    public const string Name = "closeSale";
    public uint Port { get; set; }
}

/// <summary>ferry: подарок союзнику to через свой космопорт — кредиты и груз из хранилищ.</summary>
[MemoryPackable]
public sealed partial class FerryOrder
{
    public const string Name = "ferry";
    public uint Port { get; set; }
    public uint To { get; set; }
    public double Credits { get; set; }
    public Dictionary<string, double> Goods { get; set; } = [];
}

/// <summary>gift: отдать своих юнитов союзнику to.</summary>
[MemoryPackable]
public sealed partial class GiftOrder
{
    public const string Name = "gift";
    public uint[] Units { get; set; } = [];
    public uint To { get; set; }
}
