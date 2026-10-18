using Kharos.Mod.Sdk;
using MemoryPack;

namespace Base.Host;

/// <summary>
/// mechanics/production: здание с <c>Producer</c> выпускает юнитов за кредиты. Заказ платится сразу, очередь — по
/// одному, готовый юнит выходит у ворот (<c>exit</c> вида здания) и едет к точке сбора. Приказы: produce, cancel,
/// rally; система <c>mechanics/production.produce</c> считает время. Состояние — в компоненте Producer: unit — что
/// делается (пусто — ничего), progress — сколько секунд идёт, queue — что заказано следом, rally/rallyX/rallyY — сбор.
/// </summary>
internal sealed class Production(BaseMod mod) : ISystem
{
    /// <summary>Строка мира с номером 0 — пустая: так в столбце Producer.unit выглядит «ничего не делается».</summary>
    private const double Empty = 0;

    private Column _unit, _progress, _rally, _rallyX, _rallyY, _x, _y, _player;

    /// <summary>Своё здание с Producer, которое умеет делать unit (null — без проверки вида); иначе null.</summary>
    private ComponentValues? Producer(uint player, uint building, string? unit)
    {
        var p = mod.P;
        if (!Tools.Alive(building))
            return null;
        var owner = Tools.Get(building, p.Owner);
        var state = Tools.Get(building, p.Producer);
        if (owner is null || owner.Number(p.Owner["player"]) != player || state is null)
            return null;
        if (unit is not null)
        {
            var path = Protos.PathOf(p, building);
            if (path is null || !path.StartsWith("buildings/", StringComparison.Ordinal) || !mod.D.Buildings.TryGetValue(path["buildings/".Length..], out var kind))
                return null;
            if (Array.IndexOf(kind.Produces, unit) < 0 || !mod.D.Units.ContainsKey(unit))
                return null;
        }
        return state;
    }

    /// <summary>produce { building, unit }: заказать юнит; кредиты списываются сразу.</summary>
    public void Produce(OrderContext ctx)
    {
        if (Orders.Parse<ProduceOrder>(ctx) is not { } order)
            return;
        var p = mod.P;
        if (Producer(ctx.Player, order.Building, order.Unit) is not { } state)
            return;
        var queue = ProducerQueue.Read(state.Bytes(p.Producer["queue"]));
        var busy = state.Number(p.Producer["unit"]) != Empty ? 1 : 0;
        if (queue.Count + busy >= mod.D.QueueLimit)
            return;
        var unit = mod.D.Units[order.Unit];
        if (!Wallet.Pay(p, ctx.Commands, ctx.Player, unit.Cost))
        {
            ctx.Commands.Signal("refused", MemoryPackSerializer.Serialize(new RefusedSignal { Player = ctx.Player, Reason = "credits" }));
            return;
        }
        if (busy == 0)
        {
            ctx.Commands.Set(order.Building, p.Producer)
                .Text(p.Producer["unit"], order.Unit)
                .Number(p.Producer["progress"], 0);
        }
        else
        {
            queue.Add(order.Unit);
            ctx.Commands.Set(order.Building, p.Producer).Bytes(p.Producer["queue"], ProducerQueue.Write(queue));
        }
        ctx.Commands.Signal("ordered", MemoryPackSerializer.Serialize(new OrderedSignal { Player = ctx.Player, Building = order.Building, Unit = order.Unit }));
    }

    /// <summary>cancel { building }: снять последний заказ (из очереди, иначе текущий) с возвратом кредитов.</summary>
    public void Cancel(OrderContext ctx)
    {
        if (Orders.Parse<CancelOrder>(ctx) is not { } order)
            return;
        var p = mod.P;
        if (Producer(ctx.Player, order.Building, null) is not { } state)
            return;
        var queue = ProducerQueue.Read(state.Bytes(p.Producer["queue"]));
        string? unit = null;
        if (queue.Count > 0)
        {
            unit = queue[^1];
            queue.RemoveAt(queue.Count - 1);
            ctx.Commands.Set(order.Building, p.Producer).Bytes(p.Producer["queue"], ProducerQueue.Write(queue));
        }
        else if (state.Number(p.Producer["unit"]) != Empty)
        {
            unit = Tools.Text((uint)state.Number(p.Producer["unit"]));
            ctx.Commands.Set(order.Building, p.Producer).Text(p.Producer["unit"], "").Number(p.Producer["progress"], 0);
        }
        if (unit is not null && mod.D.Units.TryGetValue(unit, out var kind))
            Wallet.Refund(p, ctx.Commands, ctx.Player, kind.Cost);
    }

    /// <summary>rally { building, x, y }: точка сбора.</summary>
    public void Rally(OrderContext ctx)
    {
        if (Orders.Parse<RallyOrder>(ctx) is not { } order || !double.IsFinite(order.X) || !double.IsFinite(order.Y))
            return;
        var p = mod.P;
        if (Producer(ctx.Player, order.Building, null) is null)
            return;
        ctx.Commands.Set(order.Building, p.Producer)
            .Flag(p.Producer["rally"], true)
            .Number(p.Producer["rallyX"], order.X)
            .Number(p.Producer["rallyY"], order.Y);
    }

    public void Bind(SystemBinding binding)
    {
        _unit = binding.Column("Producer", "unit");
        _progress = binding.Column("Producer", "progress");
        _rally = binding.Column("Producer", "rally");
        _rallyX = binding.Column("Producer", "rallyX");
        _rallyY = binding.Column("Producer", "rallyY");
        _x = binding.Column("Position", "x");
        _y = binding.Column("Position", "y");
        _player = binding.Column("Owner", "player");
    }

    /// <summary>Тик производства: время растёт; готово — юнит у ворот, едет к точке сбора, следующий из очереди.</summary>
    public void Run(SystemContext ctx)
    {
        var p = mod.P;
        var unitColumn = ctx.Column(_unit);
        var progress = ctx.Column(_progress);
        var rally = ctx.Column(_rally);
        var rallyX = ctx.Column(_rallyX);
        var rallyY = ctx.Column(_rallyY);
        var x = ctx.Column(_x);
        var y = ctx.Column(_y);
        var owner = ctx.Column(_player);
        for (var i = 0; i < ctx.Count; i++)
        {
            var current = unitColumn[i];
            if (current == Empty)
                continue;
            var name = Tools.Text((uint)current);
            if (!mod.D.Units.TryGetValue(name, out var unit))
                continue;
            var elapsed = progress[i] + ctx.Dt;
            if (elapsed < unit.Time)
            {
                progress[i] = elapsed;
                continue;
            }
            // Готово: юнит у ворот, едет к точке сбора или чуть вперёд.
            var building = ctx.Entities[i];
            var kind = Protos.PathOf(p, building) is { } path && path.StartsWith("buildings/", StringComparison.Ordinal)
                ? mod.D.Buildings.GetValueOrDefault(path["buildings/".Length..])
                : null;
            var (exitX, exitY) = kind is null ? (0.0, 1.0) : (kind.ExitX, kind.ExitY);
            double ox = x[i] + exitX, oy = y[i] + exitY;
            double tx = ox, ty = oy + 1.5;
            if (rally[i] != 0)
                (tx, ty) = (rallyX[i], rallyY[i]);
            var player = (uint)owner[i];
            var made = Entities.Unit(p, ctx.Commands, unit, new UnitSpawn(ox, oy, Math.Atan2(ty - oy, tx - ox), player, tx, ty, true));
            ctx.Commands.Signal("produced", MemoryPackSerializer.Serialize(new ProducedSignal { Player = player, Building = building, Unit = name, Entity = made, X = ox, Y = oy }));
            // Следующий из очереди; очередь и вид — командой (строка мира заводится движком).
            var queue = Tools.Get(building, p.Producer) is { } state ? ProducerQueue.Read(state.Bytes(p.Producer["queue"])) : [];
            string? next = null;
            if (queue.Count > 0)
            {
                next = queue[0];
                queue.RemoveAt(0);
            }
            progress[i] = 0;
            ctx.Commands.Set(building, p.Producer)
                .Text(p.Producer["unit"], next ?? "")
                .Bytes(p.Producer["queue"], ProducerQueue.Write(queue));
        }
    }
}
