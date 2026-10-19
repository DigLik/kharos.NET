using Base.Host.Kernel;

namespace Base.Host.Sections.Logistics;

/// <summary>
/// Маршруты (routes.ts): грузовик объезжает свои здания-остановки по кругу — сгружает, что остановка принимает, и
/// берёт, что примет следующая. Назначение (serve) — грузовик возит только по заявкам этих зданий. Фильтр — какие
/// грузы возить вообще.
/// </summary>
internal static class Routes
{
    /// <summary>Сколько остановок может быть в маршруте (ROUTE_LIMIT).</summary>
    public const int RouteLimit = 8;

    /// <summary>Сколько зданий может обслуживать грузовик (SERVE_LIMIT).</summary>
    public const int ServeLimit = 16;

    private const double Epsilon = Cargo.Epsilon;

    /// <summary>Годится ли здание в остановку: своё готовое здание со складом (isStop).</summary>
    public static bool IsStop(View view, uint player, uint building) => view.IsReady(player, building) && view.HasStock(building);

    /// <summary>Что взять на остановке from для следующей to: первый разрешённый груз, который from отдаёт, а to примет (pickFor); нет — −1.</summary>
    private static int PickFor(Pass pass, uint player, int filter, uint from, uint to, int except = -1)
    {
        foreach (var good in Demand.Offers(pass, from))
        {
            if (good == except || !Cargo.Carries(filter, good) || Demand.GivenBy(pass.V, from, good) <= Epsilon)
                continue;
            if (Demand.AcceptsDelivery(pass, player, to, good))
                return good;
        }
        return -1;
    }

    /// <summary>
    /// Маршрут грузовикам (setRoute): свои готовые здания со складом, подряд одинаковые склеиваются; меньше двух — снять.
    /// Прежние приказы снимаются; груз в кузове повезёт по маршруту.
    /// </summary>
    public static bool SetRoute(Pass pass, uint player, IEnumerable<uint> units, IEnumerable<uint> stops)
    {
        var route = new List<uint>();
        foreach (var stop in stops.Take(RouteLimit))
            if (IsStop(pass.V, player, stop) && (route.Count == 0 || route[^1] != stop))
                route.Add(stop);
        if (route.Count > 1 && route[0] == route[^1])
            route.RemoveAt(route.Count - 1);
        var trucks = Hauling.Own(pass, player, units);
        foreach (var truck in trucks)
        {
            // Прежние приказы снимаются, как при release: иначе, когда маршрут кончится, грузовик вернётся к ним.
            truck.Release();
            truck.Route = route.Count > 1 ? [.. route] : [];
            truck.Stop = 0;
            pass.Call.Remove(truck.Entity, S.Path);
            truck.Save(pass.Call);
        }
        return trucks.Count > 0;
    }

    /// <summary>Назначает грузовики обслуживать здания (setServe): свои готовые со складом, без повторов; пусто — снять.</summary>
    public static bool SetServe(Pass pass, uint player, IEnumerable<uint> units, IEnumerable<uint> buildings)
    {
        var serve = buildings.Distinct().Where(building => IsStop(pass.V, player, building)).Take(ServeLimit).ToList();
        var trucks = Hauling.Own(pass, player, units);
        foreach (var truck in trucks)
        {
            truck.Release();
            truck.Serve = [.. serve];
            pass.Call.Remove(truck.Entity, S.Path);
            truck.Save(pass.Call);
        }
        return trucks.Count > 0;
    }

    /// <summary>Фильтр груза (setFilter); пустой — возят всё. Начатая работа с грузом, которого фильтр не пускает, бросается.</summary>
    public static bool SetFilter(Pass pass, uint player, IEnumerable<uint> units, IEnumerable<string> goods)
    {
        var filter = Cargo.Mask(goods);
        var count = 0;
        foreach (var unit in units.Distinct())
        {
            if (!pass.V.IsOwn(player, unit) || pass.TruckOf(unit) is not { } truck)
                continue;
            truck.Filter = filter;
            // Работа на заявку с грузом, который фильтр не пускает, бросается: набранное он отвезёт в хранилище.
            if ((truck.From != 0 || truck.To != 0) && filter != 0 && !truck.Carries(truck.Resource))
            {
                truck.DropJob();
                pass.Call.Remove(truck.Entity, S.Path);
            }
            truck.Save(pass.Call);
            count++;
        }
        return count > 0;
    }

    /// <summary>Грузовик уезжает к остановке stop (leave).</summary>
    private static void Leave(Truck truck, int stop)
    {
        truck.Stop = stop;
        truck.Full = false;
        truck.Amount = 0;
    }

    /// <summary>
    /// Раз в тик (runRoutes): на остановке грузовик сгружает то, что она принимает, и берёт то, что примет следующая, кроме
    /// только что привезённого сюда. Пустой, которому брать нечего, едет к ближайшей остановке с грузом для следующей.
    /// Пропавшие остановки выпадают; с одной маршрут кончается.
    /// </summary>
    public static void Run(Pass pass)
    {
        var view = pass.V;
        var seeking = new List<(Truck Truck, uint Building)>();
        foreach (var truck in pass.Trucks)
        {
            if (truck.Route.Count == 0 || truck.Moving || truck.Converting)
                continue;
            var player = truck.Player;
            if (truck.Route.Exists(stop => !IsStop(view, player, stop)))
            {
                truck.Route = [.. truck.Route.Where(stop => IsStop(view, player, stop))];
                truck.Stop = 0;
            }
            var route = truck.Route;
            if (route.Count < 2)
            {
                truck.Route = [];
                truck.Stop = 0;
                continue;
            }
            var index = (int)truck.Stop % route.Count;
            truck.Stop = index;
            var retry = Core.OnTurn(pass.Tick, truck.Entity, Hauling.RetryTicks);
            var here = route[index];
            if (!Beams.Reaches(view, truck.Entity, here) && !Beams.Reaches(view, here, truck.Entity))
            {
                if (Hauling.SeekDue(pass.Tick, truck))
                    seeking.Add((truck, here));
                truck.Waiting = true;
                continue;
            }
            truck.Waiting = false;
            var next = route[(index + 1) % route.Count];
            // Сначала сгружает всё, что здесь принимают, и только потом грузится.
            if (!truck.Full)
            {
                var carried = view.Carried(truck.Entity);
                var unloaded = carried >= 0 && Demand.AcceptsDelivery(pass, player, here, carried) ? Beams.Transfer(pass, truck.Entity, here, carried) : 0;
                truck.Loading = unloaded > 0;
                if (unloaded > 0)
                {
                    // Привезённое сюда отсюда же и не забирают.
                    truck.Resource = carried;
                    truck.Amount = 1;
                    continue;
                }
                truck.Full = true;
            }
            // Берёт для следующей остановки: тот же груз, что уже в кузове, или первый подходящий.
            var inside = view.Carried(truck.Entity);
            var before = truck.Amount > 0 ? truck.Resource : -1;
            var wanted = inside >= 0 ? inside : PickFor(pass, player, truck.Filter, here, next, before);
            var moved = 0.0;
            if (wanted >= 0 && wanted != before && view.Room(truck.Entity, wanted) > Epsilon && Demand.Offers(pass, here).Contains(wanted)
                && Demand.GivenBy(view, here, wanted) > Epsilon && Demand.AcceptsDelivery(pass, player, next, wanted))
                moved = Beams.Transfer(pass, here, truck.Entity, wanted, Math.Min(view.Room(truck.Entity, wanted), Demand.GivenBy(view, here, wanted)));
            truck.Loading = moved > 0;
            if (moved > 0)
                continue;
            if (view.Carried(truck.Entity) >= 0)
            {
                Leave(truck, (index + 1) % route.Count);
                continue;
            }
            // Пустой и брать здесь нечего: едет туда, где груз есть, а нет его нигде — ждёт.
            if (!retry)
                continue;
            for (var step = 1; step < route.Count; step++)
            {
                var at = (index + step) % route.Count;
                if (PickFor(pass, player, truck.Filter, route[at], route[(at + 1) % route.Count]) >= 0)
                {
                    Leave(truck, at);
                    break;
                }
            }
        }
        foreach (var (truck, building) in seeking)
            Hauling.SeekBeam(pass, truck, building, Beams.BeamFor(view, truck.Entity, building) is var beam and not 0 ? beam : Beams.BeamFor(view, building, truck.Entity));
    }
}
