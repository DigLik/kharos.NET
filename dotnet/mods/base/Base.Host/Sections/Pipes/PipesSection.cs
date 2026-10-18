using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Pipes;

/// <summary>
/// Раздел Pipes (С2c). Трубы: сети труб и колодцев, партии груза по ним.
/// Объявит: pipeFlow; приказы pipes, wells. Системы — через <see cref="Order.System"/> (место в тике — из <see cref="Order.Tick"/>).
/// </summary>
internal static class PipesSection
{
    public static void Setup(ModBuilder mod)
    {
        // Ждёт С2c.
        _ = mod;
    }
}
