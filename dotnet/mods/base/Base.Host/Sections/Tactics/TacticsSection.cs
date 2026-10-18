using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Tactics;

/// <summary>
/// Раздел Tactics (С8). Тактика: стойки, патруль, очереди приказов.
/// Объявит: patrol, followOrders; приказы stance, patrol. Системы — через <see cref="Order.System"/> (место в тике — из <see cref="Order.Tick"/>).
/// </summary>
internal static class TacticsSection
{
    public static void Setup(ModBuilder mod)
    {
        // Ждёт С8.
        _ = mod;
    }
}
