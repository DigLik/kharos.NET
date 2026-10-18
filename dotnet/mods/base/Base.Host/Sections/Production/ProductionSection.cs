using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Production;

/// <summary>
/// Раздел Production (С4). Производство: цеха собирают изделия из сырья.
/// Объявит: assemble. Системы — через <see cref="Order.System"/> (место в тике — из <see cref="Order.Tick"/>).
/// </summary>
internal static class ProductionSection
{
    public static void Setup(ModBuilder mod)
    {
        // Ждёт С4.
        _ = mod;
    }
}
