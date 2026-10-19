using Base.Host.Kernel;
using Base.Host.Sections.Mining;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Logistics;

/// <summary>
/// Грузовики (hauling.ts). Груз между зданием и грузовиком переносит луч грузовика (<see cref="Beams"/>): ему достаточно
/// встать в радиусе, очередей нет. Привязанный к шахте грузовик возит её руду на переработку; посланный за дропом —
/// вывозит его; свободному работу даёт диспетчер (<see cref="Dispatch"/>). Работа — забрать груз со склада From и сгрузить
/// на склад To. Пропал склад, кончился груз или место — грузовик ищет другой или бросает работу. К чему не подъехать,
/// то после нескольких промахов отмечается (Unreachable), а запертый грузовик — сам (trapped).
/// </summary>
internal static class Hauling
{
    /// <summary>Раз во сколько тиков грузовик, не дотянувшийся до луча или не нашедший, куда выгрузиться, пробует снова (RETRY_TICKS).</summary>
    public const int RetryTicks = 20;

    /// <summary>Раз во сколько тиков диспетчер раздаёт работу (DISPATCH_TICKS).</summary>
    public const int DispatchTicks = 10;

    /// <summary>После скольких промахов подъезда подряд подъезда нет (GIVE_UP).</summary>
    public const int GiveUp = 3;

    /// <summary>Сколько секунд к зданию, к которому не подъехать, не дают работы (UNREACHABLE_SECONDS).</summary>
    public const double UnreachableSeconds = 120;

    /// <summary>Самая долгая отсрочка подъезда после промахов — 2^5 × RETRY_TICKS (MAX_BACKOFF).</summary>
    public const int MaxBackoff = 5;

    private const double Epsilon = Cargo.Epsilon;

    // ===== Приказы =====

    /// <summary>Может ли игрок привязать грузовики к этому зданию: это его готовая шахта (canHaul).</summary>
    public static bool CanHaul(View view, uint player, uint mine) => view.IsReady(player, mine) && view.Building(mine)?.Extract == true;

    /// <summary>Свои грузовики из списка, без повторов, по порядку.</summary>
    public static List<Truck> Own(Pass pass, uint player, IEnumerable<uint> units)
    {
        var list = new List<Truck>();
        foreach (var unit in units.Distinct())
            if (pass.Call.Has(unit, S.Unit) && pass.V.IsOwn(player, unit) && pass.TruckOf(unit) is { } truck)
                list.Add(truck);
        return list;
    }

    /// <summary>Привязывает грузовики игрока к его шахте: дальше они возят только её руду на переработку (assignHaulers).</summary>
    public static bool AssignHaulers(Pass pass, uint player, uint mine, IEnumerable<uint> units)
    {
        if (!CanHaul(pass.V, player, mine))
            return false;
        var trucks = Own(pass, player, units);
        foreach (var truck in trucks)
        {
            truck.Release();
            truck.Mine = mine;
            // Тронется сам в ближайший тик: гружёный — в хранилище, пустой — к шахте.
            pass.Call.Remove(truck.Entity, S.Path);
            truck.Save(pass.Call);
        }
        return trucks.Count > 0;
    }

    /// <summary>Нужен ли своему зданию или стройке груз прямо сейчас: тогда грузовики можно послать его обеспечить (canSupply).</summary>
    public static bool CanSupply(Pass pass, uint player, uint target) =>
        pass.V.IsOwn(player, target) && Demand.RequestsOf(pass, player, Flows.None).Exists(request => request.To == target);

    /// <summary>
    /// Посылает грузовики обеспечить здание или стройку (assignSupply): возят только по его заявкам, беря груз где угодно у
    /// игрока, пока заявки не кончатся. Нужное уже в кузове — везут сразу.
    /// </summary>
    public static bool AssignSupply(Pass pass, uint player, uint target, IEnumerable<uint> units)
    {
        if (!CanSupply(pass, player, target))
            return false;
        var trucks = Own(pass, player, units);
        foreach (var truck in trucks)
        {
            truck.Release();
            truck.Supply = target;
            pass.Call.Remove(truck.Entity, S.Path);
            var carried = pass.V.Carried(truck.Entity);
            if (carried >= 0 && Demand.AcceptsDelivery(pass, player, target, carried))
            {
                truck.Resource = carried;
                truck.To = target;
                truck.Full = true;
            }
            truck.Save(pass.Call);
        }
        return trucks.Count > 0;
    }

    /// <summary>Можно ли послать грузовики вывезти этот дроп: он ещё лежит (canPickup).</summary>
    public static bool CanPickup(View view, uint drop) => view.Alive(drop) && view.IsDrop(drop);

    /// <summary>Посылает грузовики вывезти дроп рейсами, где бы он ни лежал, пока он не опустеет (assignPickup).</summary>
    public static bool AssignPickup(Pass pass, uint player, uint drop, IEnumerable<uint> units)
    {
        if (!CanPickup(pass.V, drop))
            return false;
        var trucks = Own(pass, player, units);
        foreach (var truck in trucks)
        {
            truck.Release();
            truck.Pickup = drop;
            pass.Call.Remove(truck.Entity, S.Path);
            truck.Save(pass.Call);
        }
        return trucks.Count > 0;
    }

    // ===== Помощники =====

    /// <summary>Можно ли вернуть груз туда, откуда он взят: здание стоит и место под него есть (returnsTo).</summary>
    private static bool ReturnsTo(View view, uint building, int good) => view.HasStock(building) && view.BuildingType(building) is not null && view.Room(building, good) > Epsilon;

    /// <summary>Ближайший к грузовику заказчик этого груза (requesterFor); никто не ждёт — 0.</summary>
    private static uint RequesterFor(Pass pass, Truck truck, int good)
    {
        var view = pass.V;
        var (x, y) = view.At(truck.Entity);
        uint best = 0;
        var bestDistance = double.PositiveInfinity;
        foreach (var request in Demand.RequestsOf(pass, truck.Player, Flows.Of(pass)))
        {
            if (request.Good != good || view.Unreachable(request.To))
                continue;
            var (ax, ay) = view.At(request.To);
            var far = DetMath.Hypot(ax - x, ay - y);
            if (far < bestDistance)
                (best, bestDistance) = (request.To, far);
        }
        return best;
    }

    /// <summary>
    /// Пора ли снова искать подъезд (seekDue): только что получивший работу трогается сразу, остальные — раз в
    /// RETRY_TICKS, а после промахов — всё реже.
    /// </summary>
    public static bool SeekDue(ulong tick, Truck truck) => !truck.Waiting || Core.OnTurn(tick, truck.Entity, RetryTicks << (int)Math.Min(truck.Misses, MaxBackoff));

    /// <summary>
    /// Подводит грузовик к зданию на длину луча (seekBeam). Норма тика вышла — ищет в свою следующую очередь, без промаха;
    /// не нашёл — промах. После GIVE_UP промахов подряд: заперт сам — отметка trapped, иначе здание — Unreachable на
    /// UNREACHABLE_SECONDS. Подъехал — отметки снимаются.
    /// </summary>
    public static void SeekBeam(Pass pass, Truck truck, uint building, uint beam)
    {
        if (beam == 0)
            return;
        var view = pass.V;
        var found = Beams.Seek(view, truck.Entity, building, view.Beam(beam)!.Value.Radius, Beams.SeekPool, Beams.SeekTiles);
        if (found == Beams.Approach.Cut)
            return;
        if (found == Beams.Approach.Found)
        {
            truck.Misses = 0;
            truck.Trapped = false;
            if (view.Marked(building))
                view.Mark(building, 0);
        }
        else if (++truck.Misses >= GiveUp)
        {
            if (Dispatch.WalledIn(view, truck.Entity))
                truck.Trapped = true;
            else
                view.Mark(building, (double)pass.Tick + Core.Ticks(UnreachableSeconds));
        }
    }

    /// <summary>Бросает весь груз грузовика на землю дропом под ним и снимает с него работу (unload).</summary>
    private static void Unload(Pass pass, Truck truck)
    {
        var view = pass.V;
        var items = new Dictionary<string, double>();
        for (var good = 0; good < Cargo.Count; good++)
            if (view.Amount(truck.Entity, good) is var amount && amount != 0)
                items[Cargo.Name(good)] = amount;
        var (x, y) = view.At(truck.Entity);
        var drop = Drops.Items(pass.Call, x, y, items);
        if (drop != 0)
            view.Forget(drop);
        foreach (var name in items.Keys)
            view.Set(truck.Entity, Cargo.Index(name), 0);
        pass.Spawned();
        truck.DropJob();
    }

    /// <summary>Принимает ли этот груз хоть одно здание игрока, к которому его грузовикам сейчас не подъехать (blockedTaker).</summary>
    private static bool BlockedTaker(Pass pass, uint player, int good, IEnumerable<uint> marked)
    {
        foreach (var entity in marked)
            if (pass.V.Unreachable(entity) && Demand.AcceptsDelivery(pass, player, entity, good))
                return true;
        return false;
    }

    /// <summary>
    /// Грузовик бросает работу, к которой ему не подъехать (abandon): груз едет туда, куда подъехать можно, — в хранилище,
    /// на переработку, заказчику или обратно; нет такого места — ложится дропом. Запертый грузовик груз держит.
    /// </summary>
    private static void Abandon(Pass pass, Truck truck, uint building)
    {
        var view = pass.V;
        truck.Misses = 0;
        if (truck.Pickup == building)
            truck.Pickup = 0;
        var good = view.Carried(truck.Entity);
        if (good < 0)
        {
            truck.DropJob();
            return;
        }
        if (truck.Trapped)
            return;
        var from = truck.From;
        var to = Demand.DeliveryFor(pass, Flows.Of(pass), truck.Entity, good, building);
        if (to == 0)
            to = RequesterFor(pass, truck, good);
        if (to == 0 && from != 0 && from != building && !view.Unreachable(from) && ReturnsTo(view, from, good))
            to = from;
        if (to != 0)
        {
            (truck.To, truck.Resource, truck.Full, truck.Waiting) = (to, good, true, false);
            return;
        }
        Unload(pass, truck);
    }

    /// <summary>Подводит грузовик к зданию на длину луча, который перенесёт груз между ними (seek).</summary>
    private static void Seek(Pass pass, Truck truck, uint building, bool toBuilding)
    {
        var view = pass.V;
        SeekBeam(pass, truck, building, toBuilding ? Beams.BeamFor(view, truck.Entity, building) : Beams.BeamFor(view, building, truck.Entity));
        if (truck.Misses >= GiveUp && (truck.Trapped || view.Unreachable(building)))
            Abandon(pass, truck, building);
    }

    /// <summary>Выработана ли шахта: в месторождении и в её складе пусто (isSpent).</summary>
    private static bool IsSpent(Pass pass, uint mine)
    {
        var view = pass.V;
        var ore = pass.MineOre(mine);
        if (!view.HasStock(mine) || ore < 0)
            return true;
        return pass.ReserveLeft(mine) <= 0 && view.Amount(mine, ore) <= Epsilon;
    }

    // ===== Тик =====

    /// <summary>
    /// Раз в тик (haul): грузовики возят; потом маршруты, ручные приказы, пустые дропы исчезают, раз в DISPATCH_TICKS —
    /// диспетчер; поиски подъезда — в конце, по норме тика. marked — сущности с отметкой «не подъехать».
    /// </summary>
    public static void Run(Pass pass, IEnumerable<uint> marked)
    {
        var view = pass.V;
        var tick = pass.Tick;
        var seeking = new List<(Truck Truck, uint Building, bool ToBuilding)>();
        var working = new List<Truck>();
        var released = new List<Truck>();
        var dropped = new List<Truck>();
        // Набравшие груз, которым надо решить, куда его везти.
        var homeless = new List<Truck>();

        foreach (var truck in pass.Trucks)
        {
            truck.Loading = false;
            // Грузовик с маршрутом ездит по нему сам, с ручным приказом — по приказу (Routes, Carrying).
            if (truck.Route.Count > 0 || truck.Order is not null)
                continue;
            if (truck.Mine != 0 && !CanHaul(view, truck.Player, truck.Mine))
            {
                // Шахты больше нет: грузовик свободен. Груз остаётся в кузове.
                released.Add(truck);
                continue;
            }
            if (truck.Pickup != 0 && !CanPickup(view, truck.Pickup))
            {
                // Дроп вывезли или застроили.
                released.Add(truck);
                continue;
            }
            if (truck.Moving || truck.Converting)
                continue;
            var retry = Core.OnTurn(tick, truck.Entity, RetryTicks);
            var cargo = truck.Entity;

            if (truck.From == 0 && truck.To == 0)
            {
                var carried = view.Carried(cargo);
                if (carried >= 0)
                {
                    // Остался груз — сначала в хранилище или переработку.
                    truck.Resource = carried;
                    truck.Full = true;
                }
                else if (truck.Mine != 0)
                {
                    var ore = pass.MineOre(truck.Mine);
                    // Шахта выработана и вывезена: грузовик свободен.
                    if (ore < 0 || IsSpent(pass, truck.Mine))
                    {
                        released.Add(truck);
                        continue;
                    }
                    // Руду, которую некуда везти, не берёт: ждёт, пока на переработке не освободится место; место
                    // бронирует сразу и набирает не больше него.
                    if (!retry)
                        continue;
                    // Переработку в сети шахты руда достигает по трубам: грузовик возит только в другие сети.
                    var flows = Flows.Of(pass);
                    var to = Demand.RefineryFor(pass, flows, cargo, ore, 0, Pass.NetworkOf(truck.Mine));
                    if (to == 0)
                        continue;
                    (truck.From, truck.To, truck.Resource) = (truck.Mine, to, ore);
                    truck.Amount = Math.Min(view.Capacity(cargo), Demand.SpaceFor(view, flows, cargo, to, ore));
                    truck.Full = false;
                }
                else if (truck.Pickup != 0)
                {
                    // Следующий рейс с дропа: первый груз, который пускает фильтр и которому есть куда ехать.
                    var drop = truck.Pickup;
                    var goods = Demand.Offers(pass, drop).Where(truck.Carries).ToList();
                    if (goods.Count == 0)
                    {
                        released.Add(truck);
                        continue;
                    }
                    if (!retry)
                        continue;
                    var flows = Flows.Of(pass);
                    var job = goods.Select(good => (Good: good, To: Demand.DeliveryFor(pass, flows, cargo, good))).FirstOrDefault(pair => pair.To != 0);
                    if (job.To == 0)
                        continue;
                    (truck.From, truck.To, truck.Resource) = (drop, job.To, job.Good);
                    truck.Amount = Math.Min(Math.Min(view.Capacity(cargo), Demand.SpaceFor(view, flows, cargo, job.To, job.Good)), Demand.Spare(view, drop, job.Good));
                    truck.Full = false;
                }
                else
                    continue;
            }

            if (truck.Full)
            {
                var back = truck.To != 0 && truck.To == truck.From && ReturnsTo(view, truck.To, truck.Resource);
                if (truck.To == 0 || !(back || Demand.AcceptsDelivery(pass, truck.Player, truck.To, truck.Resource)))
                {
                    if (retry || truck.To != 0)
                        homeless.Add(truck);
                    truck.To = 0;
                    continue;
                }
            }
            else if (!Demand.OffersPickup(view, truck.Player, truck.From, truck.Resource))
            {
                // Брать нечего. Шахта ещё добывает — ждёт у неё; иначе везёт, что набрал, или бросает работу.
                var mining = truck.From == truck.Mine && !IsSpent(pass, truck.From);
                if (!mining)
                {
                    if (view.Amount(cargo, truck.Resource) > Epsilon)
                        truck.Full = true;
                    else
                        dropped.Add(truck);
                    continue;
                }
            }

            var building = truck.Full ? truck.To : truck.From;
            var toBuilding = truck.Full;
            if (!(toBuilding ? Beams.Reaches(view, cargo, building) : Beams.Reaches(view, building, cargo)))
            {
                if (SeekDue(tick, truck))
                    seeking.Add((truck, building, toBuilding));
                truck.Waiting = true;
                continue;
            }
            truck.Waiting = false;
            working.Add(truck);
        }

        foreach (var truck in released)
            truck.Release();
        foreach (var truck in dropped)
            truck.DropJob();

        foreach (var truck in working)
        {
            var cargo = truck.Entity;
            var good = truck.Resource;
            if (truck.Full)
            {
                truck.Loading = Beams.Transfer(pass, cargo, truck.To, good) > 0;
                if (view.Amount(cargo, good) <= Epsilon)
                {
                    // Доли копятся с погрешностью: пустой кузов — ровно пустой.
                    if (view.Amount(cargo, good) != 0)
                        view.Set(cargo, good, 0);
                    truck.DropJob();
                }
                else if (view.Room(truck.To, good) <= Epsilon)
                    // Места больше нет — остаток повезёт в другое хранилище.
                    homeless.Add(truck);
                continue;
            }
            var want = truck.Amount - view.Amount(cargo, good);
            truck.Loading = Beams.Transfer(pass, truck.From, cargo, good, want) > 0;
            if (view.Amount(cargo, good) >= truck.Amount - Epsilon || view.Room(cargo, good) <= Epsilon)
            {
                // Доли копятся с погрешностью: полный кузов — ровно полный.
                if (view.Room(cargo, good) <= Epsilon)
                    view.Put(cargo, good, Epsilon);
                truck.Full = true;
                if (truck.To == 0)
                    homeless.Add(truck);
            }
            else if (truck.From == truck.Mine && IsSpent(pass, truck.From))
                // Шахта выработана: довозит набранное и освобождается.
                truck.Release();
        }

        foreach (var truck in homeless)
            Homeless(pass, truck, marked);

        Routes.Run(pass);
        Carrying.Run(pass);
        SweepDrops(pass);
        if (tick % DispatchTicks == 0)
            Dispatch.Run(pass, marked);
        foreach (var (truck, building, toBuilding) in seeking)
            Seek(pass, truck, building, toBuilding);
    }

    /// <summary>Набравший груз решает, куда его везти: обратно в хранилище, в другое, заказчику, назад — или на землю.</summary>
    private static void Homeless(Pass pass, Truck truck, IEnumerable<uint> marked)
    {
        var view = pass.V;
        var good = view.Carried(truck.Entity);
        if (good < 0)
        {
            truck.DropJob();
            return;
        }
        // Хранилища полны — груз остаётся в кузове, грузовик пробует снова раз в RETRY_TICKS.
        (truck.Resource, truck.Full, truck.Waiting) = (good, true, false);
        // Взятое из хранилища и ставшее ненужным едет обратно туда же: возить из хранилища в хранилище незачем.
        var from = truck.From;
        if (from != 0 && view.IsStore(from) && !view.Unreachable(from) && Demand.AcceptsDelivery(pass, truck.Player, from, good))
        {
            truck.To = from;
            return;
        }
        // Остальное туда, откуда взял, не везёт; другого места нет — обратно: хранилище у груза бывает одно.
        var other = Demand.DeliveryFor(pass, Flows.Of(pass), truck.Entity, good, from);
        truck.To = other != 0 ? other : Demand.DeliveryFor(pass, Flows.Of(pass), truck.Entity, good);
        // Хранилищ с местом нет — заказчику, а нет и таких — обратно, если там есть место.
        if (truck.To == 0)
            truck.To = RequesterFor(pass, truck, good);
        if (truck.To == 0 && from != 0 && !view.Unreachable(from) && ReturnsTo(view, from, good))
            truck.To = from;
        // Места нет только там, куда не подъехать, — груз ложится на землю; просто полные хранилища — груз ждёт в кузове.
        if (truck.To == 0 && !truck.Trapped && BlockedTaker(pass, truck.Player, good, marked))
            Unload(pass, truck);
    }

    /// <summary>Пустые дропы исчезают (sweepDrops; в TS — конец haul).</summary>
    private static void SweepDrops(Pass pass)
    {
        var view = pass.V;
        var gone = false;
        foreach (var drop in pass.Drops)
            if (view.Load(drop) <= Epsilon)
            {
                pass.Call.Destroy(drop);
                view.Gone.Add(drop);
                gone = true;
            }
        if (gone)
            pass.Spawned();
    }
}
