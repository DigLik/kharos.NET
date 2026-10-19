using System.Buffers.Binary;
using Base.Host.Kernel;
using Base.Host.Sections.Construction;
using Base.Host.Sections.Logistics;
using Base.Shared;
using Kharos.Mod.Sdk;
using MemoryPack;

namespace Base.Host.Sections.Pipes;

/// <summary>Пачка в трубах (Batch): откуда, куда, груз (номер в <see cref="Cargo"/>), сколько, когда придёт, развоз ли.</summary>
internal readonly record struct Parcel(uint Entity, uint From, uint To, int Good, double Amount, ulong Arrive, bool Push);

/// <summary>
/// Пачки, ещё не пришедшие, в памяти мода (<see cref="Memory"/>): у пачки нет места в мире, и приказы с запросами (потоки
/// логистики вне её систем) находят её только так. Пишет pipeFlow, когда набор пачек меняется.
/// </summary>
[MemoryPackable]
internal sealed partial class Ledger
{
    public const string Key = "pipes";

    public uint[] Entities { get; set; } = [];
    public uint[] To { get; set; } = [];
    public int[] Goods { get; set; } = [];
    public double[] Amounts { get; set; } = [];
    public ulong[] Arrive { get; set; } = [];

    public static Ledger Of(Call call) =>
        Memory.Of(call).Get(Key) is { Length: > 0 } bytes ? MemoryPackSerializer.Deserialize<Ledger>(bytes) ?? new Ledger() : new Ledger();

    /// <summary>Пачки в пути для потоков логистики (Logistics.Hooks.Batches): куда, что, сколько; живые и не пришедшие.</summary>
    public static List<(uint To, int Good, double Amount)> InFlight(Call call)
    {
        var ledger = Of(call);
        var list = new List<(uint, int, double)>();
        for (var i = 0; i < ledger.Entities.Length; i++)
            if (ledger.Arrive[i] > call.Tick && call.Alive(ledger.Entities[i]))
                list.Add((ledger.To[i], Cargo.Of(ledger.Goods[i]), ledger.Amounts[i]));
        return list;
    }
}

/// <summary>
/// Доставка по трубам (pipes.ts). Внутри сети груз ходит пачками: заказчик (стройка, производство, переработка, цех,
/// турель, космопорт) берёт его у ближайшего по трубам поставщика своей сети, а переработка, цеха и космопорт сами
/// развозят готовое по хранилищам сети. Пачка — до <see cref="Batch"/> единиц одного груза, идёт 1 тайл за тик;
/// следующую по тому же маршруту отправляют через тик после прихода. Труба ничего не держит и не занимается. Перерезали
/// трубу — пачки на ней пропадают.
/// <para>
/// Система pipeFlow: главный запрос — пачки (Batch); склады мира — побочными таблицами логистики (<see cref="Frame"/>,
/// снимок без вызовов инструментов), грузовики для потоков — своими таблицами; свои записи складов — поверх снимка
/// (<see cref="View"/>); сети и их графы — кэш по ревизии (<see cref="Zones"/>, <see cref="Graph"/>).
/// </para>
/// </summary>
internal sealed class Flow : ISystem
{
    /// <summary>Сколько единиц груза самое большее идёт одной пачкой (BATCH).</summary>
    public const double Batch = 5;

    /// <summary>Меньше этого пачкой не шлют, если заказчику нужно больше (MIN_BATCH).</summary>
    private const double MinBatch = 1;

    private const double Epsilon = 1e-9;

    /// <summary>Куда класть готовое: склад отдаёт его, когда накопилось на пачку (OUTLET_MIN).</summary>
    private const double OutletMin = Batch;

    private Frame _frame = null!;
    private Column _from, _to, _resource, _amount, _arrive, _push;
    private Column _haulFrom, _haulTo, _haulResource, _haulAmount, _haulFull;
    private Column _carryFrom, _carryTo, _carryResource, _carryLeft, _carryFull;

    /// <summary>Объявление системы: пачки, снимок логистики, грузовики и их ручные приказы.</summary>
    public static void Declare(ModBuilder mod) =>
        Frame.Declare(Order.System(mod, "pipeFlow", new Flow()).Query(Names.Batch.Name))
            .Table(Names.Hauler.Name, Names.Stock.Name)
            .Table(Names.Carry.Name, Names.Hauler.Name, Names.Stock.Name);

    public void Bind(SystemBinding binding)
    {
        P.Load(binding.Component);
        _from = binding.Column(Names.Batch.Name, Names.Batch.From);
        _to = binding.Column(Names.Batch.Name, Names.Batch.To);
        _resource = binding.Column(Names.Batch.Name, Names.Batch.Resource);
        _amount = binding.Column(Names.Batch.Name, Names.Batch.Amount);
        _arrive = binding.Column(Names.Batch.Name, Names.Batch.Arrive);
        _push = binding.Column(Names.Batch.Name, Names.Batch.Push);
        _frame = new Frame(0);
        _frame.Bind(binding);
        var hauler = binding.Table(Frame.Count);
        _haulFrom = hauler.Column(Names.Hauler.Name, Names.Hauler.From);
        _haulTo = hauler.Column(Names.Hauler.Name, Names.Hauler.To);
        _haulResource = hauler.Column(Names.Hauler.Name, Names.Hauler.Resource);
        _haulAmount = hauler.Column(Names.Hauler.Name, Names.Hauler.Amount);
        _haulFull = hauler.Column(Names.Hauler.Name, Names.Hauler.Full);
        var carry = binding.Table(Frame.Count + 1);
        _carryFrom = carry.Column(Names.Carry.Name, Names.Carry.From);
        _carryTo = carry.Column(Names.Carry.Name, Names.Carry.To);
        _carryResource = carry.Column(Names.Carry.Name, Names.Carry.Resource);
        _carryLeft = carry.Column(Names.Carry.Name, Names.Carry.Left);
        _carryFull = carry.Column(Names.Carry.Name, Names.Carry.Full);
    }

    public void Run(SystemContext ctx)
    {
        var call = Call.Of(ctx);
        var parcels = Parcels(ctx);
        var zones = Zones.All();
        // Ни пачек, ни сетей — делать нечего (и снимок складов не нужен).
        if (parcels.Count == 0 && zones.Count == 0)
            return;
        var read = _frame.Take(ctx);
        var view = new View(call, read.Snapshot);
        var pass = new Pass(view, Trucks(ctx), read.Fetchers, read.Batches, read.Holders);
        var spawned = new List<Parcel>();
        var finished = Step(pass, parcels, zones, spawned);
        Remember(call, parcels, finished, spawned);
    }

    /// <summary>Пачки мира по возрастанию номеров.</summary>
    private List<Parcel> Parcels(in SystemContext ctx)
    {
        var entities = ctx.Entities;
        var from = ctx.Column(_from);
        var to = ctx.Column(_to);
        var resource = ctx.Column(_resource);
        var amount = ctx.Column(_amount);
        var arrive = ctx.Column(_arrive);
        var push = ctx.Column(_push);
        var list = new List<Parcel>(entities.Length);
        for (var row = 0; row < entities.Length; row++)
            list.Add(new Parcel(entities[row], (uint)from[row], (uint)to[row], Cargo.Of(resource[row]), amount[row], (ulong)Math.Max(0, arrive[row]), push[row] != 0));
        list.Sort((a, b) => a.Entity.CompareTo(b.Entity));
        return list;
    }

    /// <summary>Грузовики для потоков (flowsOf): работа и ручной приказ из таблиц, по возрастанию номеров.</summary>
    private List<Truck> Trucks(in SystemContext ctx)
    {
        var trucks = new Dictionary<uint, Truck>();
        var table = ctx.Table(Frame.Count);
        var from = table.Column(_haulFrom);
        var to = table.Column(_haulTo);
        var resource = table.Column(_haulResource);
        var amount = table.Column(_haulAmount);
        var full = table.Column(_haulFull);
        for (var row = 0; row < table.Count; row++)
            trucks[table.Entities[row]] = new Truck
            {
                Entity = table.Entities[row],
                From = (uint)from[row],
                To = (uint)to[row],
                Resource = Cargo.Of(resource[row]),
                Amount = amount[row],
                Full = full[row] != 0,
            };
        table = ctx.Table(Frame.Count + 1);
        from = table.Column(_carryFrom);
        to = table.Column(_carryTo);
        resource = table.Column(_carryResource);
        var left = table.Column(_carryLeft);
        full = table.Column(_carryFull);
        for (var row = 0; row < table.Count; row++)
            if (trucks.TryGetValue(table.Entities[row], out var truck))
                truck.Order = new CarryJob { From = (uint)from[row], To = (uint)to[row], Resource = Cargo.Of(resource[row]), Left = left[row], Full = full[row] != 0 };
        var list = trucks.Values.ToList();
        list.Sort((a, b) => a.Entity.CompareTo(b.Entity));
        return list;
    }

    /// <summary>
    /// Тик труб (pipeFlow): пришедшие пачки сгружаются, освободившиеся маршруты шлют следующие. Сначала заказы (по
    /// важности, как у грузовиков, см. <see cref="Demand.RequestsOf"/>), потом развоз готового по хранилищам. Возвращает
    /// отжившие пачки (уничтожены); новые — в spawned.
    /// </summary>
    private static List<Parcel> Step(Pass pass, List<Parcel> parcels, IReadOnlyDictionary<uint, List<Zones.Zone>> zones, List<Parcel> spawned)
    {
        var view = pass.V;
        var call = pass.Call;
        var tick = call.Tick;
        var finished = new List<Parcel>();
        // Занятые маршруты: заказ — получатель и груз, развоз — отправитель и груз.
        var busy = new HashSet<(bool Push, uint Entity, int Good)>();
        var arrived = parcels.Where(parcel => parcel.Arrive == tick).ToList();
        var paths = arrived.Count == 0 ? [] : Tools.BytesMany([.. arrived.Select(parcel => parcel.Entity)], P.Batch, P.BatchPath);
        var at = 0;
        foreach (var parcel in parcels)
        {
            if (tick > parcel.Arrive)
            {
                finished.Add(parcel);
                continue;
            }
            busy.Add(parcel.Push ? (true, parcel.From, parcel.Good) : (false, parcel.To, parcel.Good));
            if (tick == parcel.Arrive)
                Deliver(view, parcel, paths[at++] ?? []);
        }
        foreach (var parcel in finished)
            call.Destroy(parcel.Entity);

        if (zones.Count == 0)
            return finished;
        var flows = Flows.Of(pass);
        // Что здание отдаёт, пачки не меняют.
        var offering = new Demand.OfferIndex(pass);
        foreach (var (player, list) in zones)
        {
            if (player == 0)
                continue;
            foreach (var request in Demand.RequestsOf(pass, player, flows))
            {
                var key = (false, request.To, request.Good);
                if (busy.Contains(key))
                    continue;
                if (Zones.NetworkOf(request.To) is not { } zone)
                    continue;
                var routes = Graph.RoutesFrom(zone, request.To);
                uint best = 0;
                var bestLength = double.PositiveInfinity;
                var bestAmount = 0.0;
                foreach (var source in offering[zone, request.Good])
                {
                    if (source == request.To || !routes.Dist.TryGetValue(source, out var length) || length >= bestLength || Demand.Barred(view, request.To, source))
                        continue;
                    var available = Demand.GivenBy(view, source, request.Good, request.Sale);
                    if (available < Math.Min(request.Amount, MinBatch) - Epsilon)
                        continue;
                    best = source;
                    bestLength = length;
                    bestAmount = Math.Min(Batch, Math.Min(request.Amount, available));
                }
                if (best == 0)
                    continue;
                busy.Add(key);
                Send(view, player, best, request.To, request.Good, bestAmount, bestLength, routes, false, spawned);
            }
            foreach (var zone in list)
                PushOutlets(pass, player, zone, busy, flows, spawned);
        }
        return finished;
    }

    /// <summary>
    /// Развоз готового (pushOutlets): переработка, цеха и космопорт отправляют его в хранилища своей сети — по пачке, когда
    /// накопилось на пачку, а остатки (сырьё прежнего рецепта цеха, закупку космопорта, запас лаборатории от прежней
    /// темы, лишнее станции запроса) — сколько есть. Хранилище — самое пустое по этому грузу с учётом едущего туда, из
    /// равных — ближайшее по трубам.
    /// </summary>
    private static void PushOutlets(Pass pass, uint player, Zones.Zone zone, HashSet<(bool, uint, int)> busy, Flows flows, List<Parcel> spawned)
    {
        var view = pass.V;
        // Склада у трубы нет: так тысячи труб сети не стоят ни одного вызова.
        var stores = zone.Buildings.Where(entity => view.HasStock(entity) && view.IsStore(entity)).ToList();
        if (stores.Count == 0)
            return;
        foreach (var source in zone.Buildings)
        {
            if (!view.HasStock(source) || view.Building(source) is not { } spec)
                continue;
            if (spec.Refines is null && spec.Assembles is null && !spec.Trades && !spec.Researches && !spec.Requester)
                continue;
            foreach (var good in Demand.Offers(pass, source))
            {
                var key = (true, source, good);
                if (busy.Contains(key))
                    continue;
                // Станция запроса отдаёт в хранилища только лишнее сверх заказа.
                var available = spec.Requester ? Demand.Surplus(view, source, good) : Demand.Spare(view, source, good);
                var leftover = spec.Requester || spec.Trades || spec.Researches || (view.Assembly(source) is not null && !Cargo.IsProduct(good))
                    || (spec.Refines is not null && view.Amount(source, Cargo.Refines(spec)) <= Epsilon);
                if (available < (leftover ? Epsilon : OutletMin))
                    continue;
                var routes = Graph.RoutesFrom(zone, source);
                uint best = 0;
                var (bestFill, bestLength, bestSpace) = (double.PositiveInfinity, double.PositiveInfinity, 0.0);
                foreach (var store in stores)
                {
                    if (!routes.Dist.TryGetValue(store, out var length))
                        continue;
                    var space = view.Room(store, good) - flows.In(store, good);
                    if (space <= Epsilon)
                        continue;
                    var capacity = view.Capacity(store);
                    var limit = view.Limit(store, good);
                    var holds = Math.Min(limit < 0 ? capacity : limit, capacity);
                    var fill = Math.Floor((1 - space / holds) * 10 + 0.5);
                    if (fill > bestFill || (fill == bestFill && length >= bestLength))
                        continue;
                    (best, bestFill, bestLength, bestSpace) = (store, fill, length, space);
                }
                if (best == 0)
                    continue;
                var amount = Math.Min(Batch, Math.Min(available, bestSpace));
                busy.Add(key);
                flows.AddIn(best, good, amount);
                Send(view, player, source, best, good, amount, bestLength, routes, true, spawned);
            }
        }
    }

    /// <summary>Отправляет пачку (send): груз уходит со склада from сразу и придёт на склад to через столько тиков, сколько тайлов пути.</summary>
    private static void Send(View view, uint player, uint from, uint to, int good, double amount, double length, Routes routes, bool push, List<Parcel> spawned)
    {
        var taken = view.Take(from, good, amount);
        if (taken <= Epsilon)
            return;
        var call = view.Call;
        var tick = call.Tick;
        // Длина в тайлах: на тайл — тик, но не меньше одного.
        var arrive = tick + (ulong)Math.Max(1, Math.Floor(length + 0.5));
        var nodes = push ? PathOf(view, routes, to, from) : PathOf(view, routes, from, to);
        if (push)
            nodes.Reverse();
        var entity = call.Spawn();
        call.Add(entity, P.Batch).Number(P.BatchFrom, from).Number(P.BatchTo, to).Number(P.BatchResource, Cargo.Proto(good)).Number(P.BatchAmount, taken)
            .Number(P.BatchSent, tick).Number(P.BatchArrive, arrive).Flag(P.BatchPush, push).Bytes(P.BatchPath, Tiles(view, nodes));
        call.Add(entity, S.Owner).Number(S.OwnerPlayer, player);
        call.Add(entity, P.Sighting).Number(P.SightingRule, P.Allies);
        spawned.Add(new Parcel(entity, from, to, good, taken, arrive, push));
    }

    /// <summary>
    /// Путь пачки (pathOf) от узла from по кратчайшим путям routes (посчитанным от to): узлы, где путь поворачивает, вместе
    /// с началом и концом. Между соседними узлами списка путь прямой — так его рисует клиент.
    /// </summary>
    private static List<uint> PathOf(View view, Routes routes, uint from, uint to)
    {
        var nodes = new List<uint> { from };
        for (var node = from; node != to;)
            nodes.Add(node = routes.Next[node]);
        if (nodes.Count <= 2)
            return nodes;
        var path = new List<uint> { from };
        (int, int) Direction(uint a, uint b)
        {
            var (ax, ay) = Center(view, a);
            var (bx, by) = Center(view, b);
            return (Math.Sign(bx - ax), Math.Sign(by - ay));
        }
        for (var i = 1; i < nodes.Count - 1; i++)
            if (Direction(nodes[i - 1], nodes[i]) != Direction(nodes[i], nodes[i + 1]))
                path.Add(nodes[i]);
        path.Add(to);
        return path;
    }

    /// <summary>Основание узла сети: здание и труба — из кэша зон, не начатая стройка — по снимку складов.</summary>
    private static (int X, int Y, int W, int H) BaseOf(View view, uint entity)
    {
        if (Zones.BaseOf(entity) is { } found)
            return found;
        var (x, y) = view.At(entity);
        var spec = view.SiteSpec(entity) ?? view.Building(entity);
        return ((int)x, (int)y, (int)(spec?.Width ?? 1), (int)(spec?.Height ?? 1));
    }

    /// <summary>Середина узла сети (centerOf): центр основания.</summary>
    private static (double X, double Y) Center(View view, uint entity)
    {
        var (x, y, w, h) = BaseOf(view, entity);
        return (x + w / 2.0, y + h / 2.0);
    }

    /// <summary>Путь пачки байтами: левые верхние тайлы узлов, i32 x, y подряд.</summary>
    private static byte[] Tiles(View view, List<uint> nodes)
    {
        var bytes = new byte[nodes.Count * 8];
        for (var i = 0; i < nodes.Count; i++)
        {
            var (x, y, _, _) = BaseOf(view, nodes[i]);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 8), x);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 8 + 4), y);
        }
        return bytes;
    }

    /// <summary>
    /// Цел ли путь пачки (intact): все узлы пути после отправителя стоят и в одной сети с получателем. Иначе трубу
    /// перерезали, и пачка пропала. Отправитель мог сгореть, когда пачка уже ушла: его не проверяют.
    /// </summary>
    private static bool Intact(Zones.Zone zone, byte[] path)
    {
        var count = path.Length / 8;
        // Получатель — в сети (так её и нашли); проверять — узлы между отправителем и им.
        if (count <= 2)
            return true;
        var xy = new int[(count - 2) * 2];
        for (var i = 1; i < count - 1; i++)
        {
            xy[(i - 1) * 2] = BinaryPrimitives.ReadInt32LittleEndian(path.AsSpan(i * 8));
            xy[(i - 1) * 2 + 1] = BinaryPrimitives.ReadInt32LittleEndian(path.AsSpan(i * 8 + 4));
        }
        var occupants = new uint[count - 2];
        Tools.Occupants(xy, occupants);
        foreach (var node in occupants)
            if (node == 0 || !ReferenceEquals(Zones.NetworkOf(node), zone))
                return false;
        return true;
    }

    /// <summary>Пачка пришла (deliver): груз ложится на склад получателя; не поместилось — обратно отправителю или в хранилище сети.</summary>
    private static void Deliver(View view, Parcel parcel, byte[] path)
    {
        var to = parcel.To;
        if (!view.Alive(to) || Zones.NetworkOf(to) is not { } zone || !Intact(zone, path))
            return;
        var left = parcel.Amount;
        if (view.HasStock(to))
            left -= view.Put(to, parcel.Good, left);
        if (left <= Epsilon)
            return;
        var from = parcel.From;
        foreach (var other in zone.Buildings.Prepend(from))
        {
            if (left <= Epsilon)
                break;
            if (!view.HasStock(other) || !view.Alive(other) || !ReferenceEquals(Zones.NetworkOf(other), zone) || (other != from && !view.IsStore(other)))
                continue;
            left -= view.Put(other, parcel.Good, left);
        }
    }

    /// <summary>Пачки в пути — в память мода (<see cref="Ledger"/>), когда их набор поменялся.</summary>
    private static void Remember(Call call, List<Parcel> parcels, List<Parcel> finished, List<Parcel> spawned)
    {
        var ledger = Ledger.Of(call);
        var alive = parcels.Where(parcel => call.Tick <= parcel.Arrive).Concat(spawned).ToList();
        if (finished.Count == 0 && spawned.Count == 0 && ledger.Entities.Length == alive.Count)
            return;
        var fresh = new Ledger
        {
            Entities = [.. alive.Select(parcel => parcel.Entity)],
            To = [.. alive.Select(parcel => parcel.To)],
            Goods = [.. alive.Select(parcel => Cargo.Proto(parcel.Good))],
            Amounts = [.. alive.Select(parcel => parcel.Amount)],
            Arrive = [.. alive.Select(parcel => parcel.Arrive)],
        };
        Memory.Of(call).Put(call, Ledger.Key, MemoryPackSerializer.Serialize(fresh));
    }
}
