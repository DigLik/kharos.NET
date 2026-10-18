using System.Diagnostics.CodeAnalysis;
using Base.Host.Kernel;
using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Mining;

/// <summary>
/// Раздел Mining (С2а). Добыча: месторождения (слой deposits: генератор при начале партии, смена выработанных), шахты
/// (<see cref="Mines"/>), переработка руды (<see cref="Refining"/>), склады (<see cref="Stocks"/>) и ящики с грузом на
/// земле (<see cref="Drops"/>). Системы: refine и renewDeposits — места в тике по <see cref="Order.Tick"/>; extract
/// (в TS — начало haul) и sweepDrops (его конец) — свои, сразу после refine, до движения.
/// <para>
/// Общее для других разделов: <see cref="Stocks"/> (put, take, roomFor), <see cref="Drops"/> (dropItems, dropCargo,
/// clearDrops), <see cref="Deposits"/> (depositAt, reserveLeft, takeReserve, hasMine), <see cref="Refining.Supply"/> (энергия
/// заводов, ставит раздел Energy). Транспортный луч (inventory.ts: beamFor, transfer, approach) здесь не нужен —
/// он у Logistics.
/// </para>
/// </summary>
internal static class MiningSection
{
    public static void Setup(ModBuilder mod)
    {
        // Генератор месторождений — при начале партии; в сохранённой партии слой уже в сохранении.
        mod.On("start", Generator.OnStart);
        Order.System(mod, "refine", new Refining.Refine()).Query(Names.Building.Name, Names.Proto.Name).Without(Names.Site.Name);
        Order.System(mod, "renewDeposits", new Renewal.RenewDeposits()).Query(Names.Deposit.Name, Names.Position.Name).Write(Names.Deposit.Name);
        mod.System("mining/extract", new Mines.Extract())
            .Query(Names.Building.Name, Names.Proto.Name, Names.Position.Name).Without(Names.Site.Name).After("refine").Before("engine.paths");
        mod.System("mining/sweepDrops", new Drops.Sweep()).Query(Names.Drop.Name, Names.Stock.Name).After("refine").Before("engine.paths");
        mod.Order(EditDropOrder.Name, EditDrop);
        Queries(mod);
    }

    /// <summary>Таблицы и схема, нужные разделу (после Core.Load).</summary>
    public static void Load(LoadContext ctx)
    {
        Goods.Load();
        Deposits.Load();
        Generator.Load(ctx);
    }

    /// <summary>edit.drop: бросить груз на землю (право редактора).</summary>
    private static void EditDrop(OrderContext ctx)
    {
        var call = Call.Of(ctx);
        if ((ctx.Rights & (OrderRights.Editor | OrderRights.Admin)) == 0)
            call.Refuse(ctx.Player, ctx.Name, "rights");
        else if (ctx.Read<EditDropOrder>() is not { } order || !double.IsFinite(order.X) || !double.IsFinite(order.Y))
            call.Refuse(ctx.Player, ctx.Name, "payload");
        else if (order.Entity != 0)
            Drops.DropCargo(call, order.Entity);
        else
            Drops.Items(call, order.X, order.Y, order.Items);
    }

    private static void Queries(ModBuilder mod)
    {
        mod.Query(MiningQueries.Reserve, ctx =>
        {
            var args = Args<DepositArgs>(ctx);
            ctx.Reply(Deposits.ReserveLeft(Call.Of(ctx), args.X, args.Y));
        });
        mod.Query(MiningQueries.Kind, ctx =>
        {
            var args = Args<DepositArgs>(ctx);
            ctx.Reply(Deposits.At(args.X, args.Y)?.Name ?? "");
        });
        mod.Query(MiningQueries.Ore, ctx => ctx.Reply(Deposits.MineOre(Call.Of(ctx), Args<EntityArgs>(ctx).Entity) ?? ""));
        mod.Query(MiningQueries.Renewal, ctx =>
        {
            var state = RenewalState.Of(Call.Of(ctx));
            ctx.Reply(new RenewalInfo { Due = state.Due, Spawned = state.Spawned });
        });
    }

    private static T Args<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(QueryContext ctx) where T : class, new() => ctx.Read<T>() ?? new T();
}
