using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Combat;

/// <summary>
/// Раздел Combat (С5а). Бой: цели, выстрелы, урон, гибель, взрывы и следы боя.
/// Объявит: fight, recover; приказ attack. Системы — через <see cref="Order.System"/> (место в тике — из <see cref="Order.Tick"/>).
/// </summary>
internal static class CombatSection
{
    public static void Setup(ModBuilder mod)
    {
        // Ждёт С5а.
        _ = mod;
    }
}
