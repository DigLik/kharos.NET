using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Energy;

/// <summary>
/// Раздел Energy (С3). Энергия: хозяйства зон, топливо реакторов, накопители, доход.
/// Объявит: burnFuel, markBusy, storeEnergy, openBooks, earn, closeBooks; приказ work. Системы — через <see cref="Order.System"/> (место в тике — из <see cref="Order.Tick"/>).
/// </summary>
internal static class EnergySection
{
    public static void Setup(ModBuilder mod)
    {
        // Ждёт С3.
        _ = mod;
    }
}
