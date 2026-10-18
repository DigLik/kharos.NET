using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Research;

/// <summary>
/// Раздел Research (С6). Исследования и уровни зданий.
/// Объявит: research, triggers; приказы research, dequeue, cancelResearch, upgrade, cancelUpgrade. Системы — через <see cref="Order.System"/> (место в тике — из <see cref="Order.Tick"/>).
/// </summary>
internal static class ResearchSection
{
    public static void Setup(ModBuilder mod)
    {
        // Ждёт С6.
        _ = mod;
    }
}
