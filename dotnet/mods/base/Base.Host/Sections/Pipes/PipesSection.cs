using System.Diagnostics.CodeAnalysis;
using Base.Host.Kernel;
using Base.Host.Sections.Construction;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Kharos.Mod.Sdk;
using LogisticsHooks = Base.Host.Sections.Logistics.Hooks;

namespace Base.Host.Sections.Pipes;

/// <summary>
/// Раздел Pipes (С2c). Трубы: пачки груза по сетям труб и колодцев (<see cref="Flow"/>, система pipeFlow; граф сети —
/// <see cref="Graph"/> поверх кэша зон), протяжка труб и колодцев (<see cref="Piping"/>, приказы pipes и wells),
/// трассы без стройки (правки edit.connect и edit.connectAll), запросы pipes.*.
/// <para>
/// Логистике: пачки в пути — её потоки (в системах — таблицей кадра, в приказах и запросах — крючком
/// <see cref="LogisticsHooks.Batches"/> из памяти мода).
/// </para>
/// </summary>
internal static class PipesSection
{
    public static void Setup(ModBuilder mod)
    {
        Flow.Declare(mod);
        Commands.Handle<PipesOrder>(PipesOrder.Name, (call, player, order) =>
            Lay(call, player, order.Builders, order.Queue, order.Tiles, free => Piping.OrderPipes(call, player, order.Tiles, free) > 0));
        Commands.Handle<WellsOrder>(WellsOrder.Name, (call, player, order) =>
            Lay(call, player, order.Builders, order.Queue, order.Tiles, free => Piping.OrderWells(call, player, order.Tiles, free)));
        mod.Order(EditConnectOrder.Name, ctx =>
        {
            if (Edit<EditConnectOrder>(ctx, out var call) is { } order)
                Piping.ConnectBuilding(call, order.Building);
        });
        mod.Order(EditConnectAllOrder.Name, ctx =>
        {
            if (Edit<EditConnectAllOrder>(ctx, out var call) is { } order)
                Piping.ConnectAll(call, order.Player);
        });
        LogisticsHooks.Batches = Ledger.InFlight;
        Queries(mod);
    }

    /// <summary>
    /// Укладка протяжкой (lay в commands.ts): не отнимает строителя у работы игрока и не стирает его очередь — свободные
    /// едут сразу, занятым работа встаёт в конец очереди; с Shift — в очередь всем. Закладывается всё сразу.
    /// </summary>
    private static bool Lay(Call call, uint player, uint[] units, bool queue, int[] tiles, Func<List<uint>, bool> order)
    {
        var builders = Commands.OwnUnits(call, player, units);
        var busy = queue ? builders : builders.Where(unit => Sites.IsBusyBuilder(call, unit)).ToList();
        var free = builders.Where(unit => !busy.Contains(unit)).ToList();
        if (!order(free))
            return false;
        if (busy.Count > 0 && Sites.WorkIn(call, player, tiles) is var work and not 0)
            Commands.Accept(call, player, AssistOrder.Name, new AssistOrder { Units = [.. busy], Site = work, Queue = true });
        return true;
    }

    /// <summary>Правка редактора: payload и вызов; без права или с негодным payload — отказ и null.</summary>
    private static T? Edit<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(OrderContext ctx, out Call call) where T : class
    {
        call = Call.Of(ctx);
        if ((ctx.Rights & (OrderRights.Editor | OrderRights.Admin)) == 0)
        {
            call.Refuse(ctx.Player, ctx.Name, "rights");
            return null;
        }
        if (ctx.Read<T>() is not { } order)
        {
            call.Refuse(ctx.Player, ctx.Name, "payload");
            return null;
        }
        return order;
    }

    private static void Queries(ModBuilder mod)
    {
        mod.Query(PipesQueries.Network, ctx =>
        {
            var zone = Zones.NetworkOf(Args<EntityArgs>(ctx).Entity);
            ctx.Reply(zone is null ? new NetworkInfo() : new NetworkInfo { Buildings = [.. zone.Buildings], Sites = [.. zone.Sites], Edges = [.. zone.Edges] });
        });
        mod.Query(PipesQueries.Stroke, ctx =>
        {
            var args = Args<PipeStrokeArgs>(ctx);
            ctx.Reply(new FlagList { Items = Piping.Stroke(Call.Of(ctx), args.Player, args.Tiles, args.Kind) });
        });
        mod.Query(PipesQueries.Swap, ctx =>
        {
            var args = Args<PipeTileArgs>(ctx);
            ctx.Reply(Piping.Swap(Call.Of(ctx), args.Player, args.Kind, args.X, args.Y));
        });
        mod.Query(PipesQueries.Partners, ctx =>
        {
            var args = Args<PipeTileArgs>(ctx);
            var found = Piping.WellPartners(Call.Of(ctx), args.Player, args.X, args.Y);
            ctx.Reply(new WellList { X = [.. found.Select(item => item.X)], Y = [.. found.Select(item => item.Y)], Ready = [.. found.Select(item => item.Ready)] });
        });
    }

    private static T Args<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(QueryContext ctx) where T : class, new() => ctx.Read<T>() ?? new T();
}
