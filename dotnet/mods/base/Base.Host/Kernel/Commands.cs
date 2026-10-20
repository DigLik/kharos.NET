using System.Diagnostics.CodeAnalysis;
using Base.Shared.Orders;
using Kharos.Mod.Sdk;
using MemoryPack;

namespace Base.Host.Kernel;

/// <summary>
/// Диспетчер приказов base (commands.ts, orders.ts; docs/dotnet-n5.md, Н5.4). Все приказы — 48 команд TS, свои
/// (stop, союзы) и правки edit.* — принимает ядро (<see cref="Install"/>) и проводит одной логикой accept: приказ точкой
/// → <see cref="Resolve"/> → приказы с целью; Shift → очередь Orders; принятый без Shift забывает очередь юнитов и
/// поездку MCV. Что делает приказ, решает раздел-владелец: он ставит обработчик в своём Setup.
/// <code>
///   Commands.Handle&lt;AttackOrder&gt;(AttackOrder.Name, (call, player, order) =&gt; Attack(call, player, order));
///   Commands.OnRelease((call, unit) =&gt; call.Remove(unit, S.Armed …));   // приказ идти или стоять снимает дело
///   Commands.Resolve(10, (call, player, units, point) =&gt; …);            // правило приказа точкой
///   Commands.QueueAs(BuildOrder.Name, (call, player, order, units) =&gt; …); // что ставить в очередь вместо приказа
/// </code>
/// Обработчик возвращает, принят ли приказ; проверять аргументы и права — его дело (приходит что угодно). Приказа без
/// обработчика ждёт раздел: отказ refused с причиной «ждёт С&lt;n&gt;».
/// </summary>
internal static partial class Commands
{
    /// <summary>Обработчик приказа раздела: true — принят.</summary>
    public delegate bool Handler<in T>(Call call, uint player, T order) where T : class;

    /// <summary>Юнит получил приказ идти или стоять: раздел снимает с него своё дело (стройку, груз, цель, патруль).</summary>
    public delegate void Release(Call call, uint unit);

    /// <summary>Своим юнитам велено идти (после того как им раздали места): поход агрессивных и т. п.</summary>
    public delegate void Moved(Call call, uint player, IReadOnlyList<uint> units);

    /// <summary>
    /// Правило приказа точкой (resolveOrder): приказы с целью — payload-объекты приказов из Base.Shared; null — не моё
    /// (дальше по приоритету, в конце — move).
    /// </summary>
    public delegate IReadOnlyList<object>? Resolver(Call call, uint player, uint[] units, in Point point);

    /// <summary>Что поставить в очередь вместо приказа (стройка закладывается сразу, в очередь — помощь); null — отказ.</summary>
    public delegate object? Queuer(Call call, uint player, object order, uint[] units);

    /// <summary>
    /// Точка приказа: где (X, Y дробные; тайл), видит ли её игрок сейчас (в тумане — всегда движение), и что там видно:
    /// свой и чужой юнит под точкой (с запасом <see cref="OrderMargin"/>), здание на тайле, площадка стройки на тайле.
    /// </summary>
    public readonly record struct Point(double X, double Y, int TileX, int TileY, bool Seen, uint Own, uint Enemy, uint Occupant, uint Site);

    /// <summary>Насколько мимо юнита можно попасть точкой приказа (ORDER_MARGIN).</summary>
    public const double OrderMargin = 0.3;

    /// <summary>Сколько приказов ждёт в очереди юнита (ORDERS_LIMIT).</summary>
    public const int OrdersLimit = 32;

    private static readonly List<Release> Releases = [];
    private static readonly List<Moved> Moves = [];
    private static readonly List<(int Priority, Resolver Resolve)> Resolvers = [];

    /// <summary>Ставит обработчик приказа (раздел-владелец, в своём Setup). Тип payload — класс приказа из Base.Shared.</summary>
    public static void Handle<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(string name, Handler<T> handler) where T : class
    {
        if (!Book.TryGetValue(name, out var entry) || entry is not Entry<T> typed)
            throw new ArgumentException($"приказа {name} с payload {typeof(T).Name} нет в книге приказов");
        typed.Handler = handler;
    }

    /// <summary>Снятие дел при приказе идти или стоять.</summary>
    public static void OnRelease(Release release) => Releases.Add(release);

    /// <summary>Что делать после приказа идти.</summary>
    public static void OnMoved(Moved moved) => Moves.Add(moved);

    /// <summary>Правило приказа точкой; меньший приоритет — раньше (TS: атака 10, шахта 20, дроп 30, снабжение 40, стройка 50).</summary>
    public static void Resolve(int priority, Resolver resolver)
    {
        Resolvers.Add((priority, resolver));
        Resolvers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }

    /// <summary>Что ставить в очередь вместо приказа name (стройка: заложить площадку, в очередь — помощь на ней).</summary>
    public static void QueueAs(string name, Queuer queuer) => Book[name].Queuer = queuer;

    /// <summary>Объявляет моду все приказы книги (Setup ядра).</summary>
    public static void Install(ModBuilder mod)
    {
        foreach (var name in Book.Keys)
            mod.Order(name, Receive);
    }

    /// <summary>Приказ от хоста: права, разбор payload, accept.</summary>
    private static void Receive(OrderContext ctx)
    {
        var entry = Book[ctx.Name];
        var call = Call.Of(ctx);
        if (entry.Has(Flags.Edit) && (ctx.Rights & (OrderRights.Editor | OrderRights.Admin)) == 0)
        {
            call.Refuse(ctx.Player, ctx.Name, "rights");
            return;
        }
        // Цель по номеру сущности клиент не называет: по номеру можно назвать и то, чего игрок не видит (fromClient).
        if (ctx.Remote && entry.Has(Flags.Internal))
        {
            call.Refuse(ctx.Player, ctx.Name, "internal");
            return;
        }
        var order = entry.Read(ctx.Payload);
        if (order is null)
        {
            call.Refuse(ctx.Player, ctx.Name, "payload");
            return;
        }
        Accept(call, ctx.Player, ctx.Name, order);
    }

    /// <summary>
    /// Принимает приказ игрока изнутри (accept в commands.ts): точкой — разбор в приказы с целью, Shift — в очередь,
    /// иначе — обработчик раздела; принятый без Shift забывает очередь своих юнитов и поездку MCV. Возвращает, принят ли.
    /// </summary>
    public static bool Accept(Call call, uint player, string name, object order)
    {
        if (!Book.TryGetValue(name, out var entry))
            return false;
        if (order is PointOrder point)
            return AcceptPoint(call, player, point);
        if (entry.Has(Flags.Lay))
            return entry.Run(call, player, order);
        if (entry.Queue(order))
            return Enqueue(call, player, entry, order);
        var done = entry.Run(call, player, order);
        // Настройки (стойка, фильтр груза) — не приказы; стройка из меню очередь не трогает: занятым она встаёт в конец.
        if (done && !entry.Has(Flags.Setting) && name != BuildOrder.Name)
        {
            var units = entry.Units(order);
            if (call.Writes)
                call.Stage.Prefetch(units, S.Owner, S.Converting);
            var own = Own(call, player, units);
            ClearOrders(call, own);
            // Другой приказ MCV отменяет поездку на разворачивание.
            foreach (var unit in own)
                if (!call.Has(unit, S.Converting))
                    call.Remove(unit, S.Deploys);
        }
        return done;
    }

    /// <summary>Приказ из payload MemoryPack (правка edit.order, очередь): имя и байты.</summary>
    public static bool AcceptBytes(Call call, uint player, string name, ReadOnlySpan<byte> payload) =>
        Book.TryGetValue(name, out var entry) && entry.Read(payload) is { } order && Accept(call, player, name, order);

    /// <summary>Есть ли такой приказ в книге.</summary>
    public static bool Known(string name) => Book.ContainsKey(name);

    /// <summary>Снимает с юнитов дела приказом идти или стоять (обработчики разделов).</summary>
    public static void Free(Call call, uint unit)
    {
        foreach (var release in Releases)
            release(call, unit);
    }

    /// <summary>Своим юнитам велено идти: обработчики разделов (поход).</summary>
    public static void Went(Call call, uint player, IReadOnlyList<uint> units)
    {
        foreach (var moved in Moves)
            moved(call, player, units);
    }

    /// <summary>Свои живые юниты игрока из списка, без повторов, по порядку.</summary>
    public static List<uint> OwnUnits(Call call, uint player, IEnumerable<uint> units)
    {
        var list = new List<uint>();
        foreach (var unit in units)
            if (!list.Contains(unit) && call.Has(unit, S.Unit) && call.IsOwn(player, unit))
                list.Add(unit);
        return list;
    }

    private static List<uint> Own(Call call, uint player, uint[] units)
    {
        var list = new List<uint>();
        foreach (var unit in units)
            if (!list.Contains(unit) && call.IsOwn(player, unit))
                list.Add(unit);
        return list;
    }

    // ===== Приказ точкой (target.ts) =====

    private static bool AcceptPoint(Call call, uint player, PointOrder order)
    {
        if (!double.IsFinite(order.X) || !double.IsFinite(order.Y))
            return false;
        var units = OwnUnits(call, player, order.Units).ToArray();
        if (units.Length == 0)
            return false;
        var done = false;
        foreach (var resolved in ResolveOrder(call, player, units, order.X, order.Y))
        {
            if (!Book.TryGetValue(NameOf(resolved), out var entry))
                continue;
            entry.SetQueue(resolved, order.Queue);
            done = Accept(call, player, entry.Name, resolved) || done;
        }
        return done;
    }

    /// <summary>Что юнитам делать с точкой (resolveOrder): правила разделов по приоритету, нет — идти.</summary>
    public static IReadOnlyList<object> ResolveOrder(Call call, uint player, uint[] units, double x, double y, bool fog = true)
    {
        var point = PointAt(call, player, x, y, fog);
        foreach (var (_, resolve) in Resolvers)
            if (resolve(call, player, units, point) is { Count: > 0 } commands)
                return commands;
        return [new MoveOrder { Units = units, X = point.TileX, Y = point.TileY }];
    }

    /// <summary>Что видит игрок в точке (x, y).</summary>
    public static Point PointAt(Call call, uint player, double x, double y, bool fog = true)
    {
        var (tileX, tileY) = ((int)Math.Floor(x), (int)Math.Floor(y));
        var seen = !fog || Tools.Sees(player, tileX + 0.5, tileY + 0.5) == 2;
        if (!seen)
            return new Point(x, y, tileX, tileY, false, 0, 0, 0, 0);
        return new Point(x, y, tileX, tileY, true, UnitNear(call, player, x, y, true, OrderMargin, fog), UnitNear(call, player, x, y, false, OrderMargin, fog),
            Tools.Occupant(tileX, tileY), SiteCovering(call, tileX, tileY));
    }

    /// <summary>
    /// Юнит под точкой (unitNear): ближайший из тех, в чей круг с запасом margin она попала; own — свой или чужой. С fog
    /// чужой, которого игрок сейчас не видит, не находится.
    /// </summary>
    public static uint UnitNear(Call call, uint player, double x, double y, bool own, double margin = OrderMargin, bool fog = false)
    {
        uint best = 0;
        var bestDistance = double.PositiveInfinity;
        foreach (var entity in Tools.Near(x, y, 1 + margin, S.Unit))
        {
            if (!call.Has(entity, S.Owner) || (call.OwnerOf(entity) == player) != own)
                continue;
            var (px, py) = call.At(entity);
            var distance = Math.Sqrt((px - x) * (px - x) + (py - y) * (py - y));
            var radius = call.Num(entity, S.Body, S.BodyRadius);
            if (distance > radius + margin || distance >= bestDistance)
                continue;
            if (fog && !own && Tools.Sees(player, px, py) != 2)
                continue;
            best = entity;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>Стройка (Site), чьё основание накрывает тайл (siteAt); нет — 0.</summary>
    public static uint SiteCovering(Call call, int x, int y)
    {
        foreach (var entity in Tools.Near(x + 0.5, y + 0.5, 6, S.Site))
        {
            if (!call.Has(entity, S.Footprint))
                continue;
            var (px, py) = call.At(entity);
            if (x >= px && x < px + call.Num(entity, S.Footprint, S.FootprintW) && y >= py && y < py + call.Num(entity, S.Footprint, S.FootprintH))
                return entity;
        }
        return 0;
    }

    // ===== Очередь Shift (orders.ts) =====

    /// <summary>Очередь приказов юнита (Orders.list).</summary>
    public static QueuedOrder[] QueueOf(Call call, uint unit) =>
        call.Bytes(unit, S.Orders, S.OrdersList) is { Length: > 0 } bytes ? MemoryPackSerializer.Deserialize<OrderQueue>(bytes)?.Items ?? [] : [];

    /// <summary>Кладёт очередь юнита (пустая — компонент остаётся пустым).</summary>
    public static void SetQueue(Call call, uint unit, IReadOnlyList<QueuedOrder> list)
    {
        var bytes = list.Count == 0 ? [] : MemoryPackSerializer.Serialize(new OrderQueue { Items = [.. list] });
        if (call.Has(unit, S.Orders))
            call.Set(unit, S.Orders).Bytes(S.OrdersList, bytes);
        else if (list.Count > 0)
            call.Add(unit, S.Orders).Bytes(S.OrdersList, bytes);
    }

    /// <summary>Приказ без Shift забывает очередь (clearOrders).</summary>
    public static void ClearOrders(Call call, IEnumerable<uint> units)
    {
        foreach (var unit in units)
            if (call.Has(unit, S.Orders) && call.Bytes(unit, S.Orders, S.OrdersList) is { Length: > 0 })
                call.Set(unit, S.Orders).Bytes(S.OrdersList, []);
    }

    /// <summary>
    /// Ставит приказ своим юнитам в конец очереди (queueOrder). Стройку закладывает сразу (<see cref="QueueAs"/>), в очередь
    /// идёт помощь на ней. Кому приказ, решает номер группы: юнитов followOrders собирает сам.
    /// </summary>
    private static bool Enqueue(Call call, uint player, Entry entry, object order)
    {
        if (!entry.Has(Flags.Queued))
            return false;
        var asked = entry.Units(order);
        var units = OwnUnits(call, player, asked).Where(unit => QueueOf(call, unit).Length < OrdersLimit).ToArray();
        if (entry.Queuer is { } queuer)
        {
            // Закладывать некому: очереди всех полны.
            if (units.Length == 0 && asked.Length > 0)
                return false;
            if (queuer(call, player, order, units) is not { } replaced || !Book.TryGetValue(NameOf(replaced), out var next))
                return false;
            (entry, order) = (next, replaced);
        }
        if (units.Length == 0)
            return entry.Name == AssistOrder.Name;
        var queued = entry.Copy(order);
        entry.SetUnits(queued, []);
        entry.SetQueue(queued, false);
        var item = new QueuedOrder { Group = NextGroup(call), Name = entry.Name, Payload = entry.Write(queued) };
        foreach (var unit in units)
            SetQueue(call, unit, [.. QueueOf(call, unit), item]);
        return true;
    }

    /// <summary>Номер группы: в пределах тика — по порядку; разным тикам номера не пересекаются.</summary>
    private static long NextGroup(Call call)
    {
        var memory = Memory.Of(call);
        if (memory.GroupTick != call.Tick)
        {
            memory.GroupTick = call.Tick;
            memory.GroupSeq = 0;
        }
        var group = (long)call.Tick * 1024 + memory.GroupSeq++;
        memory.Save(call);
        return group;
    }

    /// <summary>
    /// Следующий приказ очереди — группе юнитов (followOrders): приказ получают все units вместе, как без Shift; остаток
    /// очереди каждого кладётся обратно (accept забывает очередь). Решать, готова ли группа, — дело раздела Tactics.
    /// </summary>
    public static bool Follow(Call call, uint player, QueuedOrder next, IReadOnlyList<uint> units)
    {
        if (!Book.TryGetValue(next.Name, out var entry) || entry.Read(next.Payload) is not { } order)
            return false;
        var rest = units.Select(unit => QueueOf(call, unit).Skip(1).ToArray()).ToList();
        entry.SetUnits(order, [.. units]);
        entry.SetQueue(order, false);
        var done = Accept(call, player, entry.Name, order);
        for (var i = 0; i < units.Count; i++)
            if (call.Alive(units[i]))
                SetQueue(call, units[i], rest[i]);
        return done;
    }
}
