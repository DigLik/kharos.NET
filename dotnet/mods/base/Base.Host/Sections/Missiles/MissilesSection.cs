using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Missiles;

/// <summary>
/// Раздел Missiles (С7). Ракеты: шахты, пуски, перехватчики, радары.
/// Объявит: scan, launchMissiles; приказы warhead, cancelWarhead, launch, abortLaunch. Системы — через <see cref="Order.System"/> (место в тике — из <see cref="Order.Tick"/>).
/// </summary>
internal static class MissilesSection
{
    public static void Setup(ModBuilder mod)
    {
        // Ждёт С7.
        _ = mod;
    }
}
