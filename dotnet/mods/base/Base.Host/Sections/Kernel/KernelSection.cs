using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Kernel;

/// <summary>
/// Раздел Kernel (Б1). Ядро: приказы (accept, resolveOrder, очередь Shift), превращения MCV, производство юнитов.
/// Объявит: deployOrders, convert, produceUnits; приказы order, move, stop, rally, deploy, cancelDeploy, pack, produce, cancelProduction. Системы — через <see cref="Order.System"/> (место в тике — из <see cref="Order.Tick"/>).
/// </summary>
internal static class KernelSection
{
    public static void Setup(ModBuilder mod)
    {
        // Ждёт Б1.
        _ = mod;
    }
}
