using Base.Host.Kernel;
using Base.Host.Sections.Construction;
using Base.Shared.Data;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;
using KernelProduction = Base.Host.Sections.Kernel.Production;

namespace Base.Host.Sections.Logistics;

/// <summary>Заявка: складу To не хватает Amount груза Good; чем выше Priority, тем раньше её везут (Request в logistics.ts).</summary>
internal sealed class Request
{
    public uint To;
    public int Good;
    public double Amount;
    public int Priority;
    public Zones.Zone Zone = null!;
    /// <summary>Где искать груз: в своей зоне (готовое) или в своих шахтах где угодно (руда).</summary>
    public bool Mines;
    /// <summary>Заявка на продажу: ей хранилища отдают и резерв.</summary>
    public bool Sale;
}

/// <summary>
/// Зональные заявки (logistics.ts). Всё, что потребляет груз, само заказывает его у своей зоны строительства: стройка —
/// материалы, производитель — материалы первого заказа, космопорт — товар продажи, переработка — руду из шахт (где
/// угодно), цех — сырьё рецепта, турель и станция поставки — патроны, машина снабжения — патроны туда, где стоит,
/// станция запроса — заказанное игроком. Здесь же — что склад отдаёт, принимает и куда везти груз без заявки.
/// </summary>
internal static class Demand
{
    /// <summary>Важность заявок (PRIORITY): стройка, руда на переработку и патроны, производство, продажа.</summary>
    public const int Site = 4, Refine = 3, Ammo = 3, Production = 2, Trade = 1;

    /// <summary>Турель заказывает патроны, когда расстреляла столько своего запаса (AMMO_REORDER).</summary>
    public const double AmmoReorder = 0.25;

    /// <summary>Сколько тайлов пути перевешивает одна ступень важности (PRIORITY_WEIGHT).</summary>
    public const double PriorityWeight = 1000;

    /// <summary>Сколько добытого должно накопиться, чтобы за ним поехал грузовик: PUSH_MIN или доля склада (PUSH_SHARE).</summary>
    public const double PushMin = 10, PushShare = 0.5;

    /// <summary>Сколько каждого сырья нынешней темы лаборатория держит про запас (LAB_STOCK).</summary>
    public const double LabStock = 10;

    /// <summary>Заявки меньше этого не возят (MIN_JOB).</summary>
    public const double MinJob = 0.5;

    // ===== Склад =====

    /// <summary>Сколько груза склада занято заявкой на продажу (reservedIn).</summary>
    private static double ReservedIn(View view, uint entity, int good) => view.Trade(entity) is { } trade && trade.Good == good ? Delivered(view, entity) : 0;

    /// <summary>Сколько товара продажи уже на складе космопорта (deliveredTo).</summary>
    public static double Delivered(View view, uint port)
    {
        if (view.Trade(port) is not { } trade || trade.Buy)
            return 0;
        return Math.Min(trade.Wanted, view.Amount(port, trade.Good));
    }

    /// <summary>Сколько груза можно потратить на нужды самого здания: всё, кроме товара на продажу (spareOf).</summary>
    public static double Spare(View view, uint entity, int good) =>
        view.HasStock(entity) ? Math.Max(0, view.Amount(entity, good) - ReservedIn(view, entity, good)) : 0;

    /// <summary>Сколько груза склад отдаёт по заявкам и маршрутам: хранилище — сверх резерва; продаже — и резерв (givenBy).</summary>
    public static double GivenBy(View view, uint entity, int good, bool sale = false)
    {
        var spare = Spare(view, entity, good);
        return sale ? spare : Math.Max(0, spare - view.Reserve(entity));
    }

    /// <summary>Сколько лишнего у станции запроса сверх заказа (surplusOf).</summary>
    public static double Surplus(View view, uint entity, int good) => Math.Max(0, Spare(view, entity, good) - view.Wanted(entity, good));

    /// <summary>Станция запроса не берёт у другой станции (barred).</summary>
    public static bool Barred(View view, uint to, uint source) => view.IsRequester(to) && view.IsRequester(source);

    /// <summary>Сколько груза можно забрать, кроме обещанного другим грузовикам (availableIn).</summary>
    public static double Available(View view, Flows flows, uint entity, int good, bool sale = false) =>
        Math.Max(0, GivenBy(view, entity, good, sale) - flows.Out(entity, good));

    // ===== Материалы =====

    /// <summary>Материалы, которых ждёт сущность: стройка — на здание, производитель — на первый заказ, пока не начат (materialsFor).</summary>
    public static IReadOnlyDictionary<string, double>? MaterialsFor(View view, uint entity)
    {
        if (view.SiteSpec(entity) is { } site)
            return view.Demolish(entity) ? null : site.Materials;
        if (view.Producing(entity) is not { } progress || progress > 0)
            return null;
        var queue = KernelProduction.Queue(view.Call, entity);
        return queue.Count == 0 ? null : Factory.Unit(queue[0])?.Materials;
    }

    /// <summary>Чего из материалов ещё нет на складе (missingFor): груз → недостача.</summary>
    public static List<(int Good, double Lack)> MissingFor(View view, uint entity)
    {
        var missing = new List<(int, double)>();
        if (MaterialsFor(view, entity) is not { } materials)
            return missing;
        // Порядок — по грузам, как entriesOf в TS (порядок записи материалов в данных).
        foreach (var (name, amount) in materials)
        {
            var good = Cargo.Index(name);
            if (good < 0)
                continue;
            var lack = amount - Spare(view, entity, good);
            // Крошку меньше погрешности не ждут: стройка её прощает (materialShare).
            if (lack > 1e-6)
                missing.Add((good, lack));
        }
        return missing;
    }

    /// <summary>Нужен ли сущности этот груз как материал.</summary>
    public static bool NeedsMaterial(View view, uint entity, int good) =>
        MaterialsFor(view, entity) is { } materials && materials.GetValueOrDefault(Cargo.Name(good)) != 0;

    // ===== Что отдаёт =====

    private static readonly int[] Nothing = [];

    /// <summary>
    /// Что здание отдаёт по заявкам (offersOf): хранилище и переработка — готовое, шахта — свою руду, цех — всё, кроме
    /// сырья рецепта, лаборатория — всё, что не нужно её темам, дроп и станция запроса — что лежит.
    /// </summary>
    public static int[] Offers(Pass pass, uint entity)
    {
        var view = pass.V;
        // Без склада отдавать нечего (givenBy — ноль); так трубы и стены сети не стоят ни одного вызова.
        if (!view.HasStock(entity))
            return Nothing;
        if (view.IsDrop(entity) || view.IsRequester(entity))
        {
            var lying = new List<int>();
            for (var good = 0; good < Cargo.Count; good++)
                if (view.Amount(entity, good) > 1e-9)
                    lying.Add(good);
            return [.. lying];
        }
        if (view.Building(entity) is not { } spec || view.IsSite(entity))
            return Nothing;
        if (spec.Stores || spec.Refines is not null)
            return Wares;
        if (spec.Trades)
        {
            var trade = view.Trade(entity);
            var materials = MaterialsFor(view, entity);
            return [.. Wares.Where(ware => !(trade is { Buy: false } open && open.Good == ware) && materials?.GetValueOrDefault(Cargo.Name(ware)) is null or 0.0)];
        }
        if (spec.Assembles is not null)
        {
            var inputs = view.Inputs(entity);
            return [.. Wares.Where(ware => !inputs.Contains(ware))];
        }
        if (spec.Researches)
        {
            var player = view.Owner(entity);
            return [.. Enumerable.Range(0, Cargo.Count).Where(good => !pass.ResearchNeeds(player, good))];
        }
        if (!spec.Extract)
            return Nothing;
        var ore = pass.MineOre(entity);
        return ore < 0 ? Nothing : [ore];
    }

    private static int[] _wares = [];

    /// <summary>Готовое по порядку грузов (WARES).</summary>
    public static int[] Wares => _wares.Length == Cargo.Wares ? _wares : _wares = [.. Enumerable.Range(0, Cargo.Wares)];

    /// <summary>Кто в сети что отдаёт (offerIndex): здания сети со складом, отдающие груз, по порядку зданий сети; на вызов.</summary>
    public sealed class OfferIndex(Pass pass)
    {
        private readonly Dictionary<Zones.Zone, Dictionary<int, List<uint>>> _zones = new(ReferenceEqualityComparer.Instance);

        public IReadOnlyList<uint> this[Zones.Zone zone, int good]
        {
            get
            {
                if (!_zones.TryGetValue(zone, out var goods))
                {
                    _zones[zone] = goods = [];
                    // Трубы и электростанции склада не имеют и не отдают ничего: их в сети тысячи, перебирать — дорого.
                    foreach (var source in zone.Buildings)
                    {
                        if (!pass.V.HasStock(source))
                            continue;
                        foreach (var offered in Offers(pass, source))
                        {
                            if (!goods.TryGetValue(offered, out var list))
                                goods[offered] = list = [];
                            list.Add(source);
                        }
                    }
                }
                return goods.TryGetValue(good, out var found) ? found : [];
            }
        }
    }

    // ===== Заявки =====

    /// <summary>Ближайшая к точке зона: по расстоянию до края её ближайшего круга (nearestZone).</summary>
    private static Zones.Zone? NearestZone(IReadOnlyList<Zones.Zone> zones, double x, double y)
    {
        Zones.Zone? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var zone in zones)
            for (var i = 0; i + 2 < zone.Circles.Count; i += 3)
            {
                var distance = DetMath.Hypot(x - zone.Circles[i], y - zone.Circles[i + 1]) - zone.Circles[i + 2];
                if (distance < bestDistance)
                    (best, bestDistance) = (zone, distance);
            }
        return best;
    }

    /// <summary>Зона, в чьих кругах точка; нет — null.</summary>
    public static Zones.Zone? ZoneAt(IReadOnlyList<Zones.Zone> zones, double x, double y)
    {
        foreach (var zone in zones)
            if (BuildRules.InCircles(zone.Circles, x, y))
                return zone;
        return null;
    }

    /// <summary>Заявки зон игрока (requestsOf): что, куда и сколько везти, с учётом того, что уже едет; важные — первыми.</summary>
    public static List<Request> RequestsOf(Pass pass, uint player, Flows flows)
    {
        var view = pass.V;
        var zones = pass.ZonesOf(player);
        var requests = new List<Request>();
        if (zones.Count == 0)
            return requests;

        // Зона, которая кормит здание; anywhere — и ближайшая, если здание ни в какую не входит.
        Zones.Zone? ZoneOf(uint entity, bool anywhere)
        {
            var (x, y) = view.Center(entity);
            return Pass.NetworkOf(entity) ?? ZoneAt(zones, x, y) ?? (anywhere ? NearestZone(zones, x, y) : null);
        }

        void Need(uint to, int good, double amount, int priority, bool mines = false, bool anywhere = false, bool sale = false)
        {
            var left = amount - flows.In(to, good);
            if (left < MinJob)
                return;
            if (ZoneOf(to, anywhere) is { } zone)
                requests.Add(new Request { To = to, Good = good, Amount = left, Priority = priority, Zone = zone, Mines = mines, Sale = sale });
        }

        // Свои склады игрока по возрастанию номеров.
        var own = new List<uint>();
        foreach (var entity in pass.Holders)
            if (view.IsOwn(player, entity))
                own.Add(entity);

        // Стройки и производство: материалы, которых нет на месте.
        foreach (var entity in own)
        {
            var site = view.IsSite(entity);
            if (!site && view.Producing(entity) is null)
                continue;
            // Выключенному производству материалы не везут, пока не включат.
            if (!site && (!view.IsReady(player, entity) || view.Converting(entity) || view.Off(entity)))
                continue;
            // Недостачу заказывают целыми единицами: остаток меньше MIN_JOB иначе не привёз бы никто.
            foreach (var (good, lack) in MissingFor(view, entity))
                Need(entity, good, Math.Ceiling(lack - 1e-6), site ? Site : Production);
        }
        // Переработка: своя руда из своих шахт, в какой бы зоне они ни стояли.
        foreach (var entity in own)
        {
            if (view.Building(entity) is not { Refines: { } name } || Cargo.Index(name) is var ore && ore < 0)
                continue;
            if (!view.IsReady(player, entity) || view.Converting(entity) || view.Off(entity))
                continue;
            // Больше буфера руды завод не просит, пока не переработает привезённое.
            var room = view.Room(entity, ore);
            if (room > 0)
                Need(entity, ore, room, Refine, mines: true);
        }
        // Лаборатории: сырьё нынешней и головной темы — по LAB_STOCK, не больше своей доли того, что теме осталось.
        var labs = own.Where(entity => view.Building(entity)?.Researches == true && view.IsReady(player, entity) && !view.Off(entity)).ToList();
        if (labs.Count > 0)
            foreach (var (good, left) in pass.ResearchNeeds(player))
            {
                var share = Math.Min(LabStock, Math.Ceiling(left / labs.Count - 1e-6));
                foreach (var lab in labs)
                    Need(lab, good, share - view.Amount(lab, good), Production, mines: Cargo.IsOre(good));
            }
        // Включённые цеха: сырьё рецепта, сколько его держат про запас.
        foreach (var entity in own)
        {
            if (view.Assembly(entity) != true || !view.IsReady(player, entity) || view.Building(entity) is not { } spec)
                continue;
            foreach (var (name, amount) in Cargo.Recipe(spec))
                if (Cargo.Index(name) is var good and >= 0)
                    Need(entity, good, amount * Cargo.StockedCycles - view.Amount(entity, good), Production);
        }
        // Турели и реакторы: патроны и стержни, когда израсходована четверть запаса. Оборону кормит и ближайшая зона.
        foreach (var entity in own)
        {
            if (view.Building(entity) is not { } spec || Cargo.Feed(spec) is var feed && feed < 0 || !view.IsReady(player, entity))
                continue;
            var room = view.Room(entity, feed);
            if (room >= view.Capacity(entity) * AmmoReorder)
                Need(entity, feed, room, Ammo, anywhere: true);
        }
        // Машины снабжения: патроны, когда израсходована четверть склада. Стоящая в сети с патронами грузится сама.
        OfferIndex? offering = null;
        var ammo = Cargo.Index("ammo");
        foreach (var entity in own)
        {
            if (!view.IsUnit(entity) || Factory.Unit(view.UnitType(entity))?.Supplies is null)
                continue;
            var room = view.Room(entity, ammo);
            if (room < view.Capacity(entity) * AmmoReorder)
                continue;
            var (x, y) = view.At(entity);
            if (ZoneAt(zones, x, y) is { } zone && (offering ??= new OfferIndex(pass))[zone, ammo].Any(source => GivenBy(view, source, ammo) >= MinJob))
                continue;
            Need(entity, ammo, room, Ammo, anywhere: true);
        }
        // Станции запроса: недостающее до заказа игрока.
        foreach (var entity in pass.Holders)
        {
            if (!view.HasWanted(entity) || !view.IsReady(player, entity))
                continue;
            for (var good = 0; good < Cargo.Count; good++)
                if (view.Wanted(entity, good) is var wanted and > 0)
                    Need(entity, good, wanted - view.Amount(entity, good), Production);
        }
        // Продажа: товар заявки, целыми единицами (как стройка).
        foreach (var entity in pass.Holders)
        {
            if (view.Trade(entity) is not { } trade || trade.Total > 0 || !view.IsReady(player, entity))
                continue;
            Need(entity, trade.Good, Math.Ceiling(trade.Wanted - Delivered(view, entity) - 1e-6), Trade, sale: true);
        }
        // Важные — первыми; равные — в порядке появления (сортировка JS устойчива).
        return [.. requests.OrderByDescending(request => request.Priority)];
    }

    // ===== Куда везти =====

    /// <summary>Сколько груза ещё поместится на склад для этого грузовика: место за вычетом едущего туда от других (spaceFor).</summary>
    public static double SpaceFor(View view, Flows flows, uint truck, uint to, int good) =>
        view.HasStock(to) ? view.Room(to, good) - flows.In(to, good, truck) : 0;

    /// <summary>Своё хранилище, куда грузовик отвезёт груз: самое пустое по нему с учётом едущего, из равных — ближайшее (storeFor).</summary>
    public static uint StoreFor(Pass pass, Flows flows, uint truck, int good, uint except = 0)
    {
        var view = pass.V;
        var player = view.Owner(truck);
        uint best = 0;
        var (bestFill, bestDistance) = (double.PositiveInfinity, double.PositiveInfinity);
        foreach (var entity in pass.Buildings)
        {
            if (entity == except || !view.IsStore(entity) || !view.IsReady(player, entity) || view.Converting(entity) || view.Unreachable(entity))
                continue;
            var space = SpaceFor(view, flows, truck, entity, good);
            if (space <= 1e-9)
                continue;
            var capacity = view.Capacity(entity);
            var limit = view.Limit(entity, good);
            var holds = Math.Min(limit < 0 ? capacity : limit, capacity);
            // Доля занятого с учётом едущего — до десятых: почти равные считаются равными, и решает близость.
            var fill = Math.Floor((1 - space / holds) * 10 + 0.5);
            var far = view.Distance(truck, entity);
            if (fill < bestFill || (fill == bestFill && far < bestDistance))
                (best, bestFill, bestDistance) = (entity, fill, far);
        }
        return best;
    }

    /// <summary>Ближайшая к грузовику своя переработка этой руды, куда она поместится, не из сети skip (refineryFor).</summary>
    public static uint RefineryFor(Pass pass, Flows flows, uint truck, int ore, uint except = 0, Zones.Zone? skip = null)
    {
        var view = pass.V;
        var player = view.Owner(truck);
        uint best = 0;
        var bestDistance = double.PositiveInfinity;
        foreach (var entity in pass.Buildings)
        {
            if (entity == except || view.Building(entity) is not { } spec || Cargo.Index(spec.Refines) != ore || ore < 0)
                continue;
            if (!view.IsReady(player, entity) || view.Converting(entity) || view.Off(entity))
                continue;
            if (SpaceFor(view, flows, truck, entity, ore) <= 1e-9)
                continue;
            if ((skip is not null && ReferenceEquals(Pass.NetworkOf(entity), skip)) || view.Unreachable(entity))
                continue;
            var far = view.Distance(truck, entity);
            if (far < bestDistance)
                (best, bestDistance) = (entity, far);
        }
        return best;
    }

    /// <summary>Куда везти груз без заявки: готовое — в хранилище, руду — на переработку (deliveryFor).</summary>
    public static uint DeliveryFor(Pass pass, Flows flows, uint truck, int good, uint except = 0) =>
        Cargo.IsOre(good) ? RefineryFor(pass, flows, truck, good, except) : StoreFor(pass, flows, truck, good, except);

    /// <summary>
    /// Годится ли склад, чтобы выгрузить в него груз (acceptsDelivery): своё хранилище с местом, переработка с местом под
    /// руду или то, что этот груз заказывало (стройка, производитель, космопорт, станция, цех, турель, лаборатория).
    /// </summary>
    public static bool AcceptsDelivery(Pass pass, uint player, uint to, int good)
    {
        var view = pass.V;
        if (good < 0 || !view.HasStock(to) || !view.IsOwn(player, to) || view.Room(to, good) <= 1e-9)
            return false;
        if (view.IsSite(to))
            return NeedsMaterial(view, to, good);
        // Машина снабжения принимает груз своих отсеков где угодно: к ней едут в поле.
        if (view.IsUnit(to))
            return Factory.Unit(view.UnitType(to))?.Supplies is not null;
        if (!view.IsReady(player, to) || view.Converting(to))
            return false;
        if (view.IsStore(to))
            return true;
        // Станция запроса — только заказанное и не сверх заказа.
        if (view.IsRequester(to))
            return view.Amount(to, good) < view.Wanted(to, good) - 1e-9;
        if (view.Trade(to) is { } trade)
            return trade.Total <= 0 && trade.Good == good;
        if (NeedsMaterial(view, to, good))
            return true;
        if (view.Inputs(to).Contains(good))
            return true;
        if (view.Building(to) is not { } spec)
            return false;
        if (spec.Researches && pass.ResearchNeeds(player, good))
            return true;
        return Cargo.Refines(spec) == good || Cargo.Feed(spec) == good;
    }

    // ===== Дропы и источники =====

    /// <summary>Дропы, лежащие в зоне (dropsIn).</summary>
    public static List<uint> DropsIn(Pass pass, Zones.Zone zone)
    {
        var found = new List<uint>();
        foreach (var drop in pass.Drops)
        {
            var (x, y) = pass.V.At(drop);
            if (BuildRules.InCircles(zone.Circles, x + 0.5, y + 0.5))
                found.Add(drop);
        }
        return found;
    }

    /// <summary>Можно ли забирать груз со склада: своё готовое здание или дроп, где он есть (offersPickup).</summary>
    public static bool OffersPickup(View view, uint player, uint from, int good) =>
        (view.IsReady(player, from) || view.IsDrop(from)) && Spare(view, from, good) > 1e-9;
}
