using System.Diagnostics.CodeAnalysis;
using Base.Host.Kernel;
using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Tactics;

/// <summary>
/// Раздел Tactics (С8). Тактика (tactics.ts, orders.ts): стойки и патруль (<see cref="Stances"/>), системы patrol
/// (<see cref="Patrol"/>) и followOrders (<see cref="Follow"/>) — места в тике по <see cref="Order.Tick"/>; приказы stance и
/// patrol; поход агрессивных после приказа идти (<see cref="Commands.OnMoved"/>); запросы tactics.*. Очередь Shift
/// наполняет диспетчер приказов (<see cref="Commands"/>), followOrders её разбирает.
/// <para>
/// Бою (<see cref="Combat.Hooks"/>) раздел отдаёт запись тактики: уход из дома в погоню и снятие патруля и похода. Снятие
/// приказом идти или стоять бой зовёт сам из своего Release, так что своего Release у тактики нет.
/// </para>
/// </summary>
internal static class TacticsSection
{
    public static void Setup(ModBuilder mod)
    {
        Patrol.Declare(Order.System(mod, "patrol", new Patrol()));
        Follow.Declare(Order.System(mod, "followOrders", new Follow()));
        Commands.Handle<StanceOrder>(StanceOrder.Name, (call, player, order) => Stances.SetStance(call, player, order.Units ?? [], order.Stance ?? ""));
        Commands.Handle<PatrolOrder>(PatrolOrder.Name, (call, player, order) => Stances.OrderPatrol(call, player, order.Units ?? [], order.Points ?? [], order.Append));
        // Агрессивные идут походом: по дороге бьют увиденных врагов и продолжают путь.
        Commands.OnMoved((call, _, units) => Stances.StartMarch(call, units));
        Combat.Hooks.LeaveHome = Stances.LeaveHome;
        Combat.Hooks.ClearTactics = Stances.Clear;
        Queries(mod);
    }

    private static void Queries(ModBuilder mod)
    {
        mod.Query(TacticsQueries.Stance, ctx =>
        {
            var call = Call.Of(ctx);
            var entity = Args<EntityArgs>(ctx).Entity;
            var has = call.Has(entity, S.Tactics);
            ctx.Reply(new TacticsInfo
            {
                Has = has,
                Stance = Stances.StanceOf(call, entity),
                Leg = (int)call.Num(entity, S.Tactics, T.Leg),
                Away = call.Flag(entity, S.Tactics, T.Away),
                HomeX = call.Num(entity, S.Tactics, T.HomeX),
                HomeY = call.Num(entity, S.Tactics, T.HomeY),
                Resupply = call.Flag(entity, S.Tactics, T.Resupply),
                Patrol = Stances.PatrolOf(call, entity),
            });
        });
        mod.Query(TacticsQueries.Queue, ctx =>
        {
            var call = Call.Of(ctx);
            var queue = Commands.QueueOf(call, Args<EntityArgs>(ctx).Entity);
            ctx.Reply(new QueueInfo { Names = [.. queue.Select(item => item.Name)], Groups = [.. queue.Select(item => item.Group)], Payloads = [.. queue.Select(item => item.Payload)] });
        });
    }

    private static TArgs Args<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TArgs>(QueryContext ctx) where TArgs : class, new() => ctx.Read<TArgs>() ?? new TArgs();
}
