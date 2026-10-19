using Base.Host.Kernel;
using Base.Host.Sections.Construction;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Logistics;

/// <summary>
/// Диспетчер (dispatch в logistics.ts): раздаёт работу свободным грузовикам. Каждому — лучшая из заявок его игрока:
/// важнее и ближе (путь до склада, откуда брать, и оттуда до заказчика). Готовое берётся в хранилищах зоны заказчика
/// (нет там — в любой своей зоне), руда — в шахтах игрока, где бы они ни стояли. Заявок, которые можно выполнить, нет —
/// грузовик увозит готовое из переработки и цехов в хранилище с местом. Руду без заявки не возит никто.
/// </summary>
internal static class Dispatch
{
    /// <summary>Работа грузовику: откуда, куда, что, сколько; Score — чем больше, тем лучше.</summary>
    private sealed record Job(uint From, uint To, int Good, double Amount, double Score);

    /// <summary>Карман — замкнутый пятачок меньше стольких тайлов (POCKET).</summary>
    public const int Pocket = 48;

    /// <summary>Заперт ли грузовик: из его тайла технике не выехать дальше кармана (walledIn).</summary>
    public static bool WalledIn(View view, uint truck)
    {
        var (x, y) = view.At(truck);
        return !double.IsNaN(x) && Tools.Reach((int)Math.Floor(x), (int)Math.Floor(y), Pocket).Length < Pocket;
    }

    /// <summary>Свободен ли грузовик для диспетчера: без маршрута, приказа, шахты, дропа, работы и груза, стоит (isIdle).</summary>
    private static bool Idle(View view, Truck truck) =>
        truck.Route.Count == 0 && truck.Order is null && truck.Mine == 0 && truck.Pickup == 0 && truck.From == 0 && truck.To == 0
        && view.Load(truck.Entity) <= 1e-9 && !truck.Moving && !truck.Converting;

    /// <summary>Раздаёт работу свободным грузовикам (раз в DISPATCH_TICKS).</summary>
    public static void Run(Pass pass, IEnumerable<uint> marked)
    {
        var view = pass.V;
        // Отметки «не подъехать», чей срок вышел, снимаются: здание снова получает работу.
        foreach (var entity in marked)
            if (view.Marked(entity) && view.Until(entity) <= pass.Tick)
                view.Mark(entity, 0);
        var idle = new Dictionary<uint, List<Truck>>();
        var players = new List<uint>();
        foreach (var truck in pass.Trucks)
        {
            if (!view.IsUnit(truck.Entity) || !Idle(view, truck))
                continue;
            // Запертому работы не дают, пока ему не откроют проход.
            if (truck.Trapped && (truck.Trapped = WalledIn(view, truck.Entity)))
                continue;
            if (!idle.TryGetValue(truck.Player, out var list))
            {
                idle[truck.Player] = list = [];
                players.Add(truck.Player);
            }
            list.Add(truck);
        }
        if (idle.Count == 0)
            return;
        var flows = Flows.Of(pass);
        var offering = new Demand.OfferIndex(pass);
        // Хранилища сети: их в ней десятки, а зданий — тысячи.
        var stores = new Dictionary<Zones.Zone, List<uint>>(ReferenceEqualityComparer.Instance);
        List<uint> StoresIn(Zones.Zone network)
        {
            if (!stores.TryGetValue(network, out var found))
                stores[network] = found = [.. network.Buildings.Where(entity => view.HasStock(entity) && view.IsStore(entity))];
            return found;
        }

        // Что склады отдают — раз на обход: грузы за обход не двигаются, только раздаются работы.
        var offers = new Dictionary<uint, int[]>();
        int[] OffersOf(uint entity)
        {
            if (!offers.TryGetValue(entity, out var found))
                offers[entity] = found = Demand.Offers(pass, entity);
            return found;
        }

        foreach (var player in players)
            Player(pass, player, idle[player], flows, offering, StoresIn, OffersOf);
    }

    private static void Player(Pass pass, uint player, List<Truck> trucks, Flows flows, Demand.OfferIndex offering, Func<Zones.Zone, List<uint>> storesIn, Func<uint, int[]> offersOf)
    {
        var view = pass.V;
        var requests = Demand.RequestsOf(pass, player, flows);
        // Откуда брать: переработка и цеха отдают готовое, шахты — руду. Хранилища груз только принимают.
        var outlets = new List<uint>();
        var mines = new List<uint>();
        foreach (var entity in pass.Buildings)
        {
            if (!view.IsReady(player, entity) || view.Converting(entity) || view.Building(entity) is not { } spec)
                continue;
            if (spec.Refines is not null || spec.Assembles is not null || spec.Trades || spec.Requester)
                outlets.Add(entity);
            else if (spec.Extract)
                mines.Add(entity);
        }
        // Дропы ничьи: их подбирает любой, но сам — только в своих зонах.
        var zones = pass.ZonesOf(player);
        var drops = new List<uint>();
        foreach (var zone in zones)
            foreach (var drop in Demand.DropsIn(pass, zone))
                if (!drops.Contains(drop))
                    drops.Add(drop);
        outlets.AddRange(drops);
        mines.AddRange(drops);
        var zoneSources = new Dictionary<Zones.Zone, List<uint>>(ReferenceEqualityComparer.Instance);
        List<uint> SourcesOf(Zones.Zone zone)
        {
            if (!zoneSources.TryGetValue(zone, out var found))
                zoneSources[zone] = found = [.. zone.Buildings, .. Demand.DropsIn(pass, zone)];
            return found;
        }
        // У шахты с привязанным грузовиком есть свой возчик: свободные её руду не трогают.
        var bound = new HashSet<uint>();
        foreach (var other in pass.Trucks)
            if (other.Mine != 0)
                bound.Add(other.Mine);
        List<uint>? everywhere = null;
        List<Request>? needs = null;
        List<uint> AnyZone()
        {
            if (everywhere is null)
            {
                var seen = new HashSet<uint>();
                everywhere = [];
                foreach (var entity in zones.SelectMany(zone => zone.Buildings).Concat(drops))
                    if (seen.Add(entity))
                        everywhere.Add(entity);
            }
            return everywhere;
        }
        // Есть ли в сети поставщик груза для заказчика — тогда его везут трубы.
        bool HasLocal(Zones.Zone network, uint to, int good, double least, bool sale) =>
            offering[network, good].Any(source => !Demand.Barred(view, to, source) && Demand.GivenBy(view, source, good, sale) >= least);
        // Есть ли в сети хранилище, куда этот груз ещё помещается.
        bool HasStore(Zones.Zone network, int good) => storesIn(network).Any(store => Demand.SpaceFor(view, flows, 0, store, good) > 1e-9);

        foreach (var truck in trucks)
        {
            var room = view.Capacity(truck.Entity);
            // Снесённые и потерянные здания из назначения выпадают.
            if (truck.Serve.Count > 0)
                truck.Serve = [.. truck.Serve.Where(building => view.IsReady(player, building) && view.HasStock(building))];
            // Приказ обеспечить исполнен, когда у цели не осталось заявок, даже тех, что уже везут другие.
            if (truck.Supply != 0 && !(needs ??= Demand.RequestsOf(pass, player, Flows.None)).Exists(request => request.To == truck.Supply))
                truck.Supply = 0;
            HashSet<uint>? serving = truck.Supply != 0 ? [truck.Supply] : truck.Serve.Count > 0 ? [.. truck.Serve] : null;
            Job? best = null;
            foreach (var request in requests)
            {
                if (request.Amount < Demand.MinJob || !truck.Carries(request.Good))
                    continue;
                if ((serving is not null && !serving.Contains(request.To)) || view.Unreachable(request.To))
                    continue;
                // Внутри сети груз идёт по трубам: грузовик везёт только то, чего в сети заказчика нет.
                var network = serving is not null ? null : Pass.NetworkOf(request.To);
                if (network is not null && HasLocal(network, request.To, request.Good, Math.Min(request.Amount, Demand.MinJob), request.Sale))
                    continue;
                var found = false;
                void Look(IReadOnlyList<uint> sources)
                {
                    foreach (var source in sources)
                    {
                        if (source == request.To || !offersOf(source).Contains(request.Good) || view.Unreachable(source))
                            continue;
                        if ((network is not null && ReferenceEquals(Pass.NetworkOf(source), network)) || Demand.Barred(view, request.To, source))
                            continue;
                        // Руду шахты, у которой уже есть привязанный грузовик, свободные не возят.
                        if (request.Mines && bound.Contains(source))
                            continue;
                        var available = Demand.Available(view, flows, source, request.Good, request.Sale);
                        if (available < Math.Min(request.Amount, Demand.MinJob))
                            continue;
                        found = true;
                        var score = request.Priority * Demand.PriorityWeight - view.Distance(truck.Entity, source) - view.Distance(source, request.To);
                        if (best is not null && score <= best.Score)
                            continue;
                        best = new Job(source, request.To, request.Good, Math.Min(Math.Min(request.Amount, available), room), score);
                    }
                }
                var local = !request.Mines && serving is null && network is null;
                Look(request.Mines ? mines : local ? SourcesOf(request.Zone) : AnyZone());
                // В своей зоне взять нечего — из любой своей.
                if (local && !found)
                    Look(AnyZone());
            }
            if (best is null && serving is null)
                best = Outlet(pass, truck, flows, outlets, room, HasStore, offersOf);
            if (best is null)
                continue;
            truck.From = best.From;
            truck.To = best.To;
            truck.Resource = best.Good;
            truck.Amount = best.Amount;
            truck.Full = false;
            truck.Waiting = false;
            flows.AddOut(best.From, best.Good, best.Amount, truck.Entity);
            if (best.To != 0)
            {
                flows.AddIn(best.To, best.Good, best.Amount, truck.Entity);
                if (requests.Find(item => item.To == best.To && item.Good == best.Good) is { } request)
                    request.Amount -= best.Amount;
            }
        }
    }

    /// <summary>Заявок нет — увозит готовое из переработки, цехов и дропов в хранилище; куда — решается сразу.</summary>
    private static Job? Outlet(Pass pass, Truck truck, Flows flows, List<uint> outlets, double room, Func<Zones.Zone, int, bool> hasStore, Func<uint, int[]> offersOf)
    {
        var view = pass.V;
        Job? best = null;
        foreach (var source in outlets)
        {
            if (view.Unreachable(source))
                continue;
            foreach (var good in offersOf(source))
            {
                if (!truck.Carries(good))
                    continue;
                // Есть куда отправить по трубам своей сети — грузовик не нужен.
                if (Pass.NetworkOf(source) is { } network && hasStore(network, good))
                    continue;
                // Станция запроса отдаёт в хранилища только лишнее сверх заказа.
                var requester = view.IsRequester(source);
                var available = requester ? Math.Min(Demand.Available(view, flows, source, good), Demand.Surplus(view, source, good)) : Demand.Available(view, flows, source, good);
                var capacity = view.Capacity(source);
                var limit = view.Limit(source, good);
                var holds = Math.Min(limit < 0 ? capacity : limit, capacity);
                var spec = view.Building(source);
                var refines = spec is null ? -1 : Cargo.Refines(spec);
                // Переработка, которой нечего перерабатывать, новое готовое не сделает: остаток увозят весь.
                var stopped = refines >= 0 && view.Amount(source, refines) <= 1e-9 && flows.In(source, refines) == 0;
                // Остаток сырья прежнего рецепта, закупку космопорта, остатки лаборатории, дроп и лишнее станции — до крошки.
                var leftover = (view.Assembly(source) is not null && !Cargo.IsProduct(good)) || spec?.Trades == true || spec?.Researches == true || stopped || view.IsDrop(source) || requester;
                if (available < (leftover ? 1e-9 : Math.Min(Math.Min(Demand.PushMin, room), holds * Demand.PushShare)))
                    continue;
                var score = -view.Distance(truck.Entity, source);
                if (best is not null && score <= best.Score)
                    continue;
                // Груз, который некуда везти, не берут: куда — решается сразу, и не больше, чем там поместится.
                var to = Demand.DeliveryFor(pass, flows, truck.Entity, good, source);
                if (to == 0)
                    continue;
                var space = view.Room(to, good) - flows.In(to, good);
                if (space <= 1e-9)
                    continue;
                best = new Job(source, to, good, Math.Min(Math.Min(available, room), space), score);
            }
        }
        return best;
    }
}
