using Base.Host.Kernel;
using Base.Shared;
using Base.Shared.Orders;
using Kharos.Mod.Sdk;
using MemoryPack;

namespace Base.Host.Sections.Tactics;

/// <summary>
/// Система followOrders (orders.ts): раз в тик, после всех дел тика, освободившиеся юниты берут следующий приказ из
/// очереди Shift. Приказ группы ждёт, пока освободятся все её юниты, у которых он следующий, — тогда они получают его
/// вместе (<see cref="Commands.Follow"/>) и встают строем, а не в одну точку. Юнит с делом без конца (<see cref="Endless"/>)
/// группу не держит и бросает это дело, когда приказ взят.
/// <para>Доступ к чужому (Н5-3): кадр — юниты с очередью, очередь — байтами пачкой; дела юнита — через вызов.</para>
/// </summary>
internal sealed class Follow : ISystem
{
    public static SystemDeclaration Declare(SystemDeclaration declaration) => declaration.Query(Names.Orders.Name, Names.Owner.Name);

    public void Bind(SystemBinding binding) => T.Load();

    private sealed class Group(QueuedOrder next, uint player)
    {
        public readonly QueuedOrder Next = next;
        public readonly uint Player = player;
        public readonly List<uint> Units = [];
        public bool Ready = true;
    }

    public void Run(SystemContext ctx)
    {
        if (ctx.Count == 0)
            return;
        var call = Call.Of(ctx);
        var entities = ctx.Entities.ToArray();
        Array.Sort(entities);
        var queues = Tools.BytesMany(entities, S.Orders, S.OrdersList);
        var groups = new Dictionary<long, Group>();
        var order = new List<Group>();
        for (var i = 0; i < entities.Length; i++)
        {
            if (queues[i] is not { Length: > 0 } bytes)
                continue;
            var items = MemoryPackSerializer.Deserialize<OrderQueue>(bytes)?.Items;
            if (items is not { Length: > 0 })
                continue;
            var next = items[0];
            if (!groups.TryGetValue(next.Group, out var group))
            {
                groups[next.Group] = group = new Group(next, call.OwnerOf(entities[i]));
                order.Add(group);
            }
            group.Units.Add(entities[i]);
            group.Ready &= Idle(call, entities[i]);
        }
        foreach (var group in order)
            if (group.Ready)
                Commands.Follow(call, group.Player, group.Next, group.Units);
    }

    /// <summary>
    /// Занят делом, у которого нет конца: грузовик на маршруте, при шахте или зданиях, боец в патруле. Ждать, пока оно
    /// кончится, нечего — приказ из очереди его прерывает, как прервал бы приказ без Shift.
    /// </summary>
    private static bool Endless(Call call, uint entity)
    {
        if (call.Has(entity, S.Hauler)
            && (call.Num(entity, S.Hauler, T.HaulerMine) != 0 || call.Bytes(entity, S.Hauler, T.HaulerRoute) is { Length: > 0 } || call.Bytes(entity, S.Hauler, T.HaulerServe) is { Length: > 0 }))
            return true;
        return call.Has(entity, S.Tactics) && call.Bytes(entity, S.Tactics, T.Patrol) is { Length: > 0 };
    }

    /// <summary>Нечем занят: не едет, не строит, не гонится за целью по приказу и не работает по приказу игрока. Дело без конца — не в счёт.</summary>
    private static bool Idle(Call call, uint entity)
    {
        if (Endless(call, entity))
            return true;
        if (call.Has(entity, S.Path) || call.Has(entity, S.Builds) || call.Has(entity, S.Converting))
            return false;
        if (Ordered(call, entity))
            return false;
        foreach (var turret in Factory.TurretsOf(call, entity))
            if (Ordered(call, turret))
                return false;
        // Не дошёл, куда шёл: отвлёкся на врага по дороге или возвращается после погони.
        if (call.Has(entity, S.Tactics) && call.Flag(entity, S.Tactics, T.Away))
            return false;
        // Заявки зон грузовик возит и сам, без приказа: их очередь прерывает. Разовое поручение игрока — ждёт.
        if (call.Has(entity, S.Hauler) && (call.Num(entity, S.Hauler, T.HaulerPickup) != 0 || call.Num(entity, S.Hauler, T.HaulerSupply) != 0))
            return false;
        return true;
    }

    /// <summary>Гонится ли стрелок за целью по приказу игрока.</summary>
    private static bool Ordered(Call call, uint gunner) =>
        call.Has(gunner, S.Armed) && call.Flag(gunner, S.Armed, T.ArmedOrdered) && call.Num(gunner, S.Armed, T.ArmedTarget) != 0;
}
