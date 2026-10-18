using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Mining;

/// <summary>
/// Раздел Mining (С2а). Добыча: шахты, переработка руды, ящики с грузом, обновление месторождений.
/// Объявит: refine, renewDeposits. Системы — через <see cref="Order.System"/> (место в тике — из <see cref="Order.Tick"/>).
/// </summary>
internal static class MiningSection
{
    public static void Setup(ModBuilder mod)
    {
        // Ждёт С2а.
        _ = mod;
    }
}
