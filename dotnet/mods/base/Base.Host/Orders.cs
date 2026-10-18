using System.Diagnostics.CodeAnalysis;
using Kharos.Mod.Sdk;
using MemoryPack;

namespace Base.Host;

/// <summary>
/// mechanics/orders: приказы юнитам — ехать в точку и стоять. Группа встаёт вокруг точки спиралью
/// (<see cref="Geom.Spread"/>, шаг — data/mechanics.json, orders.spacing), ближний к месту занимает ближнее.
/// </summary>
internal sealed class Orders(BaseMod mod)
{
    /// <summary>Больше юнитов в одном приказе не берётся: раздача мест — O(n²), а приходит что угодно.</summary>
    internal const int MaxGroup = 1000;

    /// <summary>Payload приказа; не разобрался (приходит что угодно) — null.</summary>
    internal static T? Parse<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(OrderContext ctx) where T : class
    {
        try
        {
            return ctx.Read<T>();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Свой живой юнит с ходовой.</summary>
    private bool Mine(uint entity, uint player) =>
        Tools.Alive(entity) && Tools.Has(entity, mod.P.Drive)
        && Tools.Get(entity, mod.P.Owner) is { } owner && owner.Number(mod.P.Owner["player"]) == player;

    /// <summary>move { units, x, y }: свои юниты с ходовой едут к точке.</summary>
    public void Move(OrderContext ctx)
    {
        if (Parse<MoveOrder>(ctx) is not { } order || !double.IsFinite(order.X) || !double.IsFinite(order.Y))
            return;
        var p = mod.P;
        var mine = new List<(uint Entity, double X, double Y)>();
        foreach (var entity in order.Units.Take(MaxGroup))
            if (Mine(entity, ctx.Player) && Tools.Get(entity, p.Position) is { } at)
                mine.Add((entity, at.Number(p.Position["x"]), at.Number(p.Position["y"])));
        if (mine.Count == 0)
            return;
        var places = Geom.Spread(mine.Count, order.X, order.Y, mod.D.Spacing);
        // Жадно: каждому месту — ближайший из оставшихся юнитов.
        foreach (var (px, py) in places)
        {
            int best = 0;
            var distance = double.PositiveInfinity;
            for (var index = 0; index < mine.Count; index++)
            {
                double dx = mine[index].X - px, dy = mine[index].Y - py;
                var d = dx * dx + dy * dy;
                if (d < distance)
                    (best, distance) = (index, d);
            }
            var entity = mine[best].Entity;
            mine.RemoveAt(best);
            ctx.Commands.Set(entity, p.Drive)
                .Number(p.Drive["tx"], px)
                .Number(p.Drive["ty"], py)
                .Flag(p.Drive["moving"], true);
        }
        ctx.Commands.Signal("moved", MemoryPackSerializer.Serialize(new MovedSignal { Player = ctx.Player, X = order.X, Y = order.Y }));
    }

    /// <summary>stop { units }: стоять.</summary>
    public void Stop(OrderContext ctx)
    {
        if (Parse<StopOrder>(ctx) is not { } order)
            return;
        foreach (var entity in order.Units.Take(MaxGroup))
            if (Mine(entity, ctx.Player))
                ctx.Commands.Set(entity, mod.P.Drive).Flag(mod.P.Drive["moving"], false);
    }
}
