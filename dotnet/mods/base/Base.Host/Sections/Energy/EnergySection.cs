using System.Diagnostics.CodeAnalysis;
using Base.Host.Kernel;
using Base.Host.Sections.Mining;
using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;
using ConstructionHooks = Base.Host.Sections.Construction.Hooks;
using KernelProduction = Base.Host.Sections.Kernel.Production;

namespace Base.Host.Sections.Energy;

/// <summary>
/// Раздел Energy (С3). Энергия: хозяйства энергосетей и книги тика (<see cref="Books"/>), реакторы (<see cref="Reactors"/>),
/// занятые потребители (<see cref="Busy"/>), накопители (<see cref="Batteries"/>), доход и износ станций
/// (<see cref="Income"/>), выключатель (<see cref="Switches"/>, приказ work). Системы burnFuel, markBusy, storeEnergy,
/// openBooks, earn, closeBooks; запросы energy.*. Кто в сети (Player.online, события player_online и player_left), ведёт
/// раздел игроков (Players/Presence).
/// <para>
/// Другим разделам: <see cref="Books.ShareOf"/> и <see cref="Books.Supply"/> (powerSupply — доля энергии потребителя),
/// <see cref="Books.SwitchedOff"/>, <see cref="Books.States"/>, <see cref="Reactors.Meltdown"/> (зовёт тот, кто разрушает
/// здание). Ставит крючки: скорость производства ядра, переработка, ремонтные станции и перегруженные станции стройки.
/// </para>
/// </summary>
internal static class EnergySection
{
    public static void Setup(ModBuilder mod)
    {
        Order.System(mod, "burnFuel", new Reactors())
            .Query(Names.Reactor.Name, Names.Building.Name, Names.Stock.Name).Without(Names.Off.Name).Write(Names.Reactor.Name);
        Order.System(mod, "markBusy", new Busy()).Query(Names.Building.Name, Names.Proto.Name).Every(PowerRules.BusyTicks);
        Order.System(mod, "storeEnergy", new Batteries()).Query(Names.Accumulator.Name).Write(Names.Accumulator.Name);
        Order.System(mod, "openBooks", new Income.OpenBooks());
        Order.System(mod, "earn", new Income.Earn()).Query(Names.Player.Name);
        Order.System(mod, "closeBooks", new Income.CloseBooks());
        Commands.Handle<WorkOrder>(WorkOrder.Name, (call, player, order) => Switches.SetWorking(call, player, order.Building, order.On));

        // Потребителю энергии при её нехватке — доля, вне сети и выключенному — ноль; остальным — полная скорость.
        KernelProduction.Speed = (call, entity) => (Books.Kind(call, entity)?.Spec.Power ?? 0) < 0 ? Books.ShareOf(call, entity) : 1;
        Refining.Supply = Books.ShareOf;
        ConstructionHooks.PowerShare = Books.ShareOf;
        ConstructionHooks.Overbuilt = call => Books.Overbuilt(call);
        Queries(mod);
    }

    private static void Queries(ModBuilder mod)
    {
        mod.Query(EnergyQueries.Economy, ctx => ctx.Reply(Books.Total(Call.Of(ctx), Args<PlayerArgs>(ctx).Player).Info()));
        mod.Query(EnergyQueries.Zones, ctx =>
            ctx.Reply(new EconomyList { Items = [.. Books.OfPlayer(Call.Of(ctx), Args<PlayerArgs>(ctx).Player).Select(economy => economy.Info())] }));
        mod.Query(EnergyQueries.Supply, ctx =>
        {
            var supply = Books.Supply(Call.Of(ctx));
            ctx.Reply(new SupplyList { Entities = [.. supply.Keys], Shares = [.. supply.Values] });
        });
        mod.Query(EnergyQueries.States, ctx =>
        {
            var states = Books.States(Call.Of(ctx));
            ctx.Reply(new PowerStateList { Entities = [.. states.Keys], States = [.. states.Values] });
        });
        mod.Query(EnergyQueries.Loaded, ctx => ctx.Reply(new EntityList { Items = [.. Books.Loaded(Call.Of(ctx))] }));
        mod.Query(EnergyQueries.Overbuilt, ctx => ctx.Reply(new EntityList { Items = [.. Books.Overbuilt(Call.Of(ctx)).Order()] }));
        mod.Query(EnergyQueries.Coreless, ctx => ctx.Reply(new EntityList { Items = [.. Books.Coreless(Call.Of(ctx))] }));
        mod.Query(EnergyQueries.Burning, ctx => ctx.Reply(Books.Burning(Call.Of(ctx), Args<EntityArgs>(ctx).Entity)));
    }

    private static T Args<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(QueryContext ctx) where T : class, new() => ctx.Read<T>() ?? new T();
}
