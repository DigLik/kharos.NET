using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Construction;

/// <summary>
/// Раздел Construction (С1). Стройка: стройки и строители, ремонт, покрытие, землеройные работы, стены и ворота.
/// Объявит: gates, construct; приказы build, assist, cancelBuild, demolish, pave, unpave, walls, dig. Системы — через <see cref="Order.System"/> (место в тике — из <see cref="Order.Tick"/>).
/// </summary>
internal static class ConstructionSection
{
    public static void Setup(ModBuilder mod)
    {
        // Ждёт С1.
        _ = mod;
    }
}
