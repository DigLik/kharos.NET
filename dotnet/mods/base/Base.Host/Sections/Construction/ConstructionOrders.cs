using Base.Host.Kernel;
using Base.Shared.Orders;
using Base.Shared.Rules;

namespace Base.Host.Sections.Construction;

/// <summary>
/// Приказы стройки (run и lay в commands.ts): build, assist, cancelBuild, demolish, pave, unpave, walls, dig; правило
/// приказа точкой — строители по своей стройке строят, по повреждённому чинят, по улучшаемому улучшают.
/// </summary>
internal static class ConstructionOrders
{
    /// <summary>Приоритет правила приказа точкой (стройка — после атаки, шахты, дропа и снабжения).</summary>
    public const int ResolvePriority = 50;

    public static void Setup()
    {
        Commands.Handle<BuildOrder>(BuildOrder.Name, Build);
        Commands.Handle<AssistOrder>(AssistOrder.Name, (call, player, order) => Sites.AssignBuilders(call, player, order.Site, order.Units));
        Commands.Handle<CancelBuildOrder>(CancelBuildOrder.Name, (call, player, order) => Sites.CancelBuild(call, player, order.Site));
        Commands.Handle<DemolishOrder>(DemolishOrder.Name, (call, player, order) => Sites.Demolish(call, player, order.Building, order.Builders));
        Commands.Handle<PaveOrder>(PaveOrder.Name, (call, player, order) =>
            Lay(call, player, order.Builders, order.Queue, order.Tiles, (_, free) => Paving.Order(call, player, order.Kind, order.Tiles, free) > 0));
        Commands.Handle<UnpaveOrder>(UnpaveOrder.Name, (call, player, order) =>
            Lay(call, player, order.Builders, order.Queue, order.Tiles, (builders, free) => Paving.Remove(call, player, order.Tiles, builders, free)));
        Commands.Handle<WallsOrder>(WallsOrder.Name, (call, player, order) =>
            Lay(call, player, order.Builders, order.Queue, order.Tiles, (_, free) => Sites.OrderWalls(call, player, order.Tiles, free, order.Gate ? "gate" : "wall") > 0));
        Commands.Handle<DigOrder>(DigOrder.Name, (call, player, order) =>
            Lay(call, player, order.Builders, order.Queue, order.Tiles, (_, free) => Dig(call, player, order.Kind, order.Tiles, free)));
        // Стройка с Shift закладывается сразу (место и кредиты заняты), а строителям в очередь идёт помощь на ней.
        Commands.QueueAs(BuildOrder.Name, (call, player, order, units) =>
        {
            var build = (BuildOrder)order;
            var site = Factory.Building(build.Building) is null ? 0 : Sites.OrderBuild(call, player, build.Building, build.X, build.Y, []);
            return site == 0 ? null : new AssistOrder { Units = units, Site = site };
        });
        // Приказ идти или стоять снимает строителя со стройки.
        Commands.OnRelease((call, unit) => call.Remove(unit, S.Builds));
        Commands.Resolve(ResolvePriority, Resolve);
    }

    /// <summary>
    /// Стройка из меню (build): не отнимает строителя у работы, которую дал ему игрок, — занятым она встаёт в очередь
    /// помощью на ней.
    /// </summary>
    private static bool Build(Call call, uint player, BuildOrder order)
    {
        if (Factory.Building(order.Building) is null)
            return false;
        var busy = order.Builders.Distinct().Where(unit => Sites.IsBusyBuilder(call, unit)).ToArray();
        var site = Sites.OrderBuild(call, player, order.Building, order.X, order.Y, [.. order.Builders.Where(unit => !busy.Contains(unit))]);
        if (site == 0)
            return false;
        if (busy.Length > 0)
            Commands.Accept(call, player, AssistOrder.Name, new AssistOrder { Units = busy, Site = site, Queue = true });
        return true;
    }

    /// <summary>
    /// Укладка протяжкой (lay): не отнимает строителя у работы игрока и не стирает его очередь — свободные едут сразу,
    /// занятым работа встаёт в конец очереди; с Shift — в очередь всем. Закладывается и оплачивается всё сразу.
    /// </summary>
    private static bool Lay(Call call, uint player, uint[] units, bool queue, int[] tiles, Func<List<uint>, List<uint>, bool> order)
    {
        var builders = Commands.OwnUnits(call, player, units);
        var busy = queue ? builders : builders.Where(unit => Sites.IsBusyBuilder(call, unit)).ToList();
        var free = builders.Where(unit => !busy.Contains(unit)).ToList();
        if (!order(builders, free))
            return false;
        if (busy.Count > 0 && Sites.WorkIn(call, player, tiles) is var work and not 0)
            Commands.Accept(call, player, AssistOrder.Name, new AssistOrder { Units = [.. busy], Site = work, Queue = true });
        return true;
    }

    /// <summary>Заказывает землеройные работы и посылает к первой свободных строителей.</summary>
    private static bool Dig(Call call, uint player, string kind, int[] tiles, List<uint> builders)
    {
        var first = Earthworks.Order(call, player, kind, tiles);
        if (first == 0)
            return false;
        Sites.AssignBuilders(call, player, first, builders);
        return true;
    }

    /// <summary>
    /// Правило приказа точкой (resolveOrder): строители по своей стройке — строят, по своему повреждённому зданию или
    /// юниту — чинят, по улучшаемому — улучшают; остальные выбранные при этом стоят.
    /// </summary>
    private static IReadOnlyList<object>? Resolve(Call call, uint player, uint[] units, in Commands.Point point)
    {
        if (!point.Seen)
            return null;
        var broken = point.Own != 0 ? point.Own : point.Occupant;
        var site = point.Site != 0 ? point.Site
            : broken != 0 && (Sites.CanRepair(call, player, broken) || call.Has(broken, Refs.R.Upgrade)) ? broken : 0;
        if (site == 0 || !units.Any(unit => call.Has(unit, S.Repair)) || !call.IsOwn(player, site))
            return null;
        return [new AssistOrder { Units = units, Site = site }];
    }
}
