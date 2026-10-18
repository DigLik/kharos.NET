using Base.Host.Kernel;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Kernel;

/// <summary>
/// Раздел Kernel (Б1). Ядро: приказы (диспетчер Kernel/Commands — accept, resolveOrder, очередь Shift), превращения MCV,
/// производство юнитов, вход игроков и стартовый набор, правки edit.*, запросы kernel.*. Общий API для разделов —
/// Base.Host/Kernel (Core, S, Call, Accounts, Factory, Placement, Moves, Commands, Memory). Системы — через
/// <see cref="Order.System"/> (место в тике — из <see cref="Order.Tick"/>).
/// </summary>
internal static class KernelSection
{
    public static void Setup(ModBuilder mod)
    {
        // Все приказы base — через диспетчер; обработчики ставят разделы.
        Commands.Install(mod);
        KernelOrders.Setup();
        Edits.Setup();
        KernelQueriesSetup.Setup(mod);
        mod.On("player_joined", Spawning.OnJoined);
        Order.System(mod, "deployOrders", new Conversion.DeployOrders()).Query(Names.Deploys.Name).Without(Names.Converting.Name, Names.Path.Name);
        Order.System(mod, "convert", new Conversion.Convert()).Query(Names.Converting.Name).Write(Names.Converting.Name);
        Order.System(mod, "produceUnits", new Production.ProduceUnits()).Query(Names.Producer.Name).Write(Names.Producer.Name);
    }
}
