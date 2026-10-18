using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Trade;

/// <summary>
/// Раздел Trade (С2d). Торговля: космопорт, корабли, подарки союзникам.
/// Объявит: trade, ferry; приказы sell, buy, closeSale, ferry, gift. Системы — через <see cref="Order.System"/> (место в тике — из <see cref="Order.Tick"/>).
/// </summary>
internal static class TradeSection
{
    public static void Setup(ModBuilder mod)
    {
        // Ждёт С2d.
        _ = mod;
    }
}
