using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Logistics;

/// <summary>
/// Раздел Logistics (С2b). Логистика: грузовики, заявки зон, маршруты, ручной подвоз, подвоз строителями.
/// Объявит: haul, fetchMaterials; приказы haul, pickup, supply, route, serve, filter, carry, reserve, want. Системы — через <see cref="Order.System"/> (место в тике — из <see cref="Order.Tick"/>).
/// </summary>
internal static class LogisticsSection
{
    public static void Setup(ModBuilder mod)
    {
        // Ждёт С2b.
        _ = mod;
    }
}
