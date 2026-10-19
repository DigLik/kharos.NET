using Base.Host.Kernel;
using Base.Shared.Orders;

namespace Base.Host.Sections.Logistics;

/// <summary>
/// Приказы логистики (run в commands.ts): haul, pickup, supply, route, serve, filter, carry, reserve, want; правила
/// приказа точкой (target.ts) — грузовики по своей шахте возят её руду (20), по дропу вывозят его (30), по своему
/// зданию или стройке, которым нужен груз, обеспечивают их (40); приказ идти или стоять снимает с грузовика работу, а
/// со строителя — поручение.
/// </summary>
internal static class LogisticsOrders
{
    /// <summary>Приоритеты правил приказа точкой: шахта, дроп, снабжение (после атаки, до стройки).</summary>
    public const int HaulPriority = 20, PickupPriority = 30, SupplyPriority = 40;

    public static void Setup()
    {
        Commands.Handle<HaulOrder>(HaulOrder.Name, (call, player, order) => Hauling.AssignHaulers(Pass.Of(call), player, order.Mine, order.Units));
        Commands.Handle<PickupOrder>(PickupOrder.Name, (call, player, order) => Hauling.AssignPickup(Pass.Of(call), player, order.Drop, order.Units));
        Commands.Handle<SupplyOrder>(SupplyOrder.Name, (call, player, order) => Hauling.AssignSupply(Pass.Of(call), player, order.Target, order.Units));
        Commands.Handle<RouteOrder>(RouteOrder.Name, (call, player, order) => Routes.SetRoute(Pass.Of(call), player, order.Units, order.Stops));
        Commands.Handle<ServeOrder>(ServeOrder.Name, (call, player, order) => Routes.SetServe(Pass.Of(call), player, order.Units, order.Buildings));
        Commands.Handle<FilterOrder>(FilterOrder.Name, (call, player, order) => Routes.SetFilter(Pass.Of(call), player, order.Units, order.Goods));
        Commands.Handle<CarryOrder>(CarryOrder.Name, (call, player, order) =>
            Carrying.SetCarry(Pass.Of(call), player, order.Units, order.From, order.To, order.Resource, order.Amount));
        Commands.Handle<ReserveOrder>(ReserveOrder.Name, (call, player, order) => SetReserve(Pass.Of(call), player, order.Buildings, order.Amount));
        Commands.Handle<WantOrder>(WantOrder.Name, (call, player, order) => SetWanted(Pass.Of(call), player, order.Buildings, order.Resource, order.Amount));
        // Приказ идти или стоять снимает строителя с поручения, грузовик — с работы, шахты, маршрута и приказа.
        Commands.OnRelease(Release);
        Commands.Resolve(HaulPriority, ResolveHaul);
        Commands.Resolve(PickupPriority, ResolvePickup);
        Commands.Resolve(SupplyPriority, ResolveSupply);
    }

    private static void Release(Call call, uint unit)
    {
        call.Remove(unit, S.Fetch);
        if (call.Has(unit, S.Hauler) && Pass.Of(call).TruckOf(unit) is { } truck)
        {
            truck.Release();
            truck.Save(call);
        }
    }

    /// <summary>
    /// Резерв своих готовых хранилищ (setReserve): сколько груза не выдавать; не больше, чем вмещает; ноль — снять.
    /// </summary>
    private static bool SetReserve(Pass pass, uint player, uint[] buildings, double amount)
    {
        if (!double.IsFinite(amount))
            return false;
        var view = pass.V;
        var count = 0;
        foreach (var building in buildings.Distinct())
        {
            if (!view.HasStock(building) || !view.IsStore(building) || view.IsSite(building) || !view.IsOwn(player, building))
                continue;
            var reserve = Math.Max(0, Math.Min(view.Capacity(building), Math.Floor(amount + 0.5)));
            pass.Call.Set(building, S.Stock).Number(L.StockReserve, reserve);
            count++;
        }
        return count > 0;
    }

    /// <summary>
    /// Заказ своих готовых станций запроса (setWanted): сколько груза держать; ноль — не заказывать. Заказ всех грузов
    /// вместе не больше склада станции. Руду не заказывают.
    /// </summary>
    private static bool SetWanted(Pass pass, uint player, uint[] buildings, string resource, double amount)
    {
        var good = Cargo.Index(resource);
        if (!double.IsFinite(amount) || good < 0 || Cargo.IsOre(good) || L.Wanted[good] is not { } field)
            return false;
        var view = pass.V;
        var count = 0;
        foreach (var building in buildings.Distinct())
        {
            if (!view.HasWanted(building) || !view.HasStock(building) || !view.IsReady(player, building))
                continue;
            var others = 0.0;
            for (var other = 0; other < Cargo.Count; other++)
                if (other != good)
                    others += view.Wanted(building, other);
            var next = Math.Max(0, Math.Min(view.Capacity(building) - others, Math.Floor(amount + 0.5)));
            pass.Call.Set(building, S.Wanted).Number(field, next);
            count++;
        }
        return count > 0;
    }

    // ===== Приказ точкой (target.ts) =====

    private static bool Trucks(Call call, uint[] units) => units.Any(unit => call.Has(unit, S.Hauler));

    /// <summary>Грузовики по своей шахте — привязываются к ней и возят добытое.</summary>
    private static IReadOnlyList<object>? ResolveHaul(Call call, uint player, uint[] units, in Commands.Point point)
    {
        if (!point.Seen || point.Occupant == 0 || !Trucks(call, units) || !Hauling.CanHaul(new View(call), player, point.Occupant))
            return null;
        return [new HaulOrder { Units = units, Mine = point.Occupant }];
    }

    /// <summary>Грузовики по дропу — вывозят его.</summary>
    private static IReadOnlyList<object>? ResolvePickup(Call call, uint player, uint[] units, in Commands.Point point)
    {
        if (!point.Seen || !Trucks(call, units))
            return null;
        var drop = Mining.Drops.At(call, point.TileX, point.TileY);
        return drop == 0 ? null : [new PickupOrder { Units = units, Drop = drop }];
    }

    /// <summary>Грузовики по своему зданию или стройке, которым нужен груз, — обеспечивают их; строители при этом строят.</summary>
    private static IReadOnlyList<object>? ResolveSupply(Call call, uint player, uint[] units, in Commands.Point point)
    {
        var needy = point.Site != 0 ? point.Site : point.Occupant;
        if (!point.Seen || needy == 0 || !Trucks(call, units) || !Hauling.CanSupply(Pass.Of(call), player, needy))
            return null;
        var haulers = units.Where(unit => call.Has(unit, S.Hauler)).ToArray();
        var commands = new List<object> { new SupplyOrder { Units = haulers, Target = needy } };
        var rest = units.Where(unit => !haulers.Contains(unit) && call.Has(unit, S.Repair)).ToArray();
        if (rest.Length > 0 && point.Site == needy)
            commands.Add(new AssistOrder { Units = rest, Site = needy });
        return commands;
    }
}
