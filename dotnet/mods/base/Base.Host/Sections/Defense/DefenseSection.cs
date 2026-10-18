using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Defense;

/// <summary>
/// Раздел Defense (С5b). Оборона: щиты, боеприпасы и снабжение, турели в покое.
/// Объявит: supply, restTurrets, chargeShields. Системы — через <see cref="Order.System"/> (место в тике — из <see cref="Order.Tick"/>).
/// </summary>
internal static class DefenseSection
{
    public static void Setup(ModBuilder mod)
    {
        // Ждёт С5b.
        _ = mod;
    }
}
