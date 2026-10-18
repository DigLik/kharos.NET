using System.Buffers.Binary;
using Kharos.Mod.Sdk;

namespace Base.Host.Kernel;

/// <summary>
/// Приказы движения (orderMove, orderGroupMove, evictUnits, clearGround в units.ts) над инструментами движка Plan,
/// GroupMove, FreeTiles и Clear: мод решает, куда, движок ищет путь, мод кладёт его в Path. Инструменты видят мир до
/// вызова: юниту, созданному в этом же вызове, место ищется без пути (<see cref="Send"/>).
/// </summary>
internal static class Moves
{
    /// <summary>После скольких перепрокладок подряд юнит в заторе (JAM_TRIES).</summary>
    public const double JamTries = 3;

    /// <summary>На сколько тайлов вокруг основания стоящие не мешают выселенному выйти (EVICT_MARGIN).</summary>
    private const int EvictMargin = 2;

    /// <summary>Кладёт путь в Path целиком: точки x, y подряд, цель, как идти.</summary>
    public static void WritePath(Call call, uint entity, ReadOnlySpan<double> points, int goalX, int goalY, bool direct = false, bool roads = false, double tries = 0, double near = 0)
    {
        var bytes = new byte[points.Length * 8];
        for (var i = 0; i < points.Length; i++)
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8), points[i]);
        call.Add(entity, S.Path)
            .Number(S.PathGoalX, goalX).Number(S.PathGoalY, goalY).Number(S.PathWait, 0).Number(S.PathTries, tries).Number(S.PathNear, near)
            .Flag(S.PathRoads, roads).Flag(S.PathDirect, direct).Flag(S.PathStuck, false).Number(S.PathBest, -1).Flag(S.PathSlot, false)
            .Bytes(S.PathPoints, bytes);
    }

    /// <summary>Путь из одной точки — центра тайла (летающим и тем, кто едет напрямую).</summary>
    public static void Aim(Call call, uint entity, int x, int y, bool direct) => WritePath(call, entity, [x + 0.5, y + 0.5], x, y, direct);

    /// <summary>
    /// Посылает юнит в тайл (x, y) (orderMove): путь — инструментом Plan. Found — в Path, None — Path снят, Cut — прежний
    /// путь остаётся (норма тика, затор, юнит врос). ignore — кто из стоящих не препятствие (пусто — сам юнит); near — на
    /// сколько тайлов достаточно подойти; tries — который раз прокладывается путь к этой цели.
    /// </summary>
    public static PlanStatus Order(Call call, uint entity, int x, int y, ReadOnlySpan<uint> ignore = default, double tries = 0, double near = 0, int limit = 0, uint pool = 0, uint budget = 0)
    {
        if (!Tools.Alive(entity))
            return PlanStatus.Cut;
        var flags = tries >= JamTries ? PathFlags.Jam : PathFlags.None;
        var plan = Tools.Plan(entity, x, y, near, limit, flags, pool, budget, ignore);
        switch (plan.Status)
        {
            case PlanStatus.Found:
                WritePath(call, entity, plan.Points, plan.GoalX, plan.GoalY, false, plan.Roads, tries, near);
                break;
            case PlanStatus.None:
                call.Remove(entity, S.Path);
                break;
        }
        return plan.Status;
    }

    /// <summary>
    /// Группа к тайлу (x, y) (orderGroupMove): каждому — своё место рядом (GroupMove). Наземные едут напрямую, путь ищут,
    /// только если упрутся; летающие — путь из одной точки. Юниты, созданные в этом вызове, — <see cref="Send"/>.
    /// </summary>
    public static void Group(Call call, IReadOnlyList<uint> units, int x, int y)
    {
        var known = new List<uint>(units.Count);
        foreach (var unit in units)
        {
            if (Tools.Alive(unit))
                known.Add(unit);
            else if (call.Alive(unit))
                Send(call, unit, x, y);
        }
        if (known.Count == 0)
            return;
        var slots = Tools.GroupMove(x, y, [.. known]);
        for (var i = 0; i < slots.Length; i++)
            if (slots[i].Mode != GroupMode.Skip)
                Aim(call, known[i], slots[i].X, slots[i].Y, slots[i].Direct);
    }

    /// <summary>
    /// Один юнит, которого движок ещё не видит (создан в этом вызове: готовый с завода к точке сбора), — на свободное
    /// место у тайла (x, y), как orderGroupMove одному: наземный напрямую, летающий — путь из одной точки.
    /// </summary>
    public static void Send(Call call, uint unit, int x, int y)
    {
        var spec = Factory.Unit(call.UnitType(unit));
        if (spec is null || call.Has(unit, S.Converting))
            return;
        var air = spec.Kind == "air";
        var tiles = Tools.FreeTiles(x, y, 1, 0, air ? PathFlags.Air : PathFlags.None, spec.Radius);
        if (tiles.Length > 0)
            Aim(call, unit, tiles[0].X, tiles[0].Y, !air);
    }

    /// <summary>Наземные юниты с центром в прямоугольнике (unitsIn), кроме ignore, — по возрастанию номеров.</summary>
    public static uint[] UnitsIn(int x, int y, int w, int h, uint ignore = 0) => Tools.Clear(x, y, w, h, ignore);

    /// <summary>
    /// Отправляет юнитов inside из основания (x, y, w, h) на свободные тайлы рядом (evictUnits): путь наружу идёт и
    /// сквозь тех, кто стоит вплотную, — они уступят.
    /// </summary>
    public static void Evict(Call call, int x, int y, int w, int h, IReadOnlyList<uint> inside)
    {
        if (inside.Count == 0)
            return;
        var group = inside.ToArray();
        var radius = Factory.Unit("mcv")?.Radius ?? 0.8;
        var tiles = Tools.FreeTiles(x + w / 2, y + h / 2, group.Length, 1, PathFlags.None, radius, group);
        if (tiles.Length == 0)
            return;
        var passable = new HashSet<uint>(group);
        passable.UnionWith(UnitsIn(x - EvictMargin, y - EvictMargin, w + EvictMargin * 2, h + EvictMargin * 2));
        var ignore = passable.Order().ToArray();
        for (var i = 0; i < group.Length; i++)
        {
            var at = Math.Min(i, tiles.Length - 1);
            Order(call, group[i], tiles[at].X, tiles[at].Y, ignore);
        }
    }

    /// <summary>
    /// Освобождает прямоугольник под здание игрока и говорит, свободен ли он (clearGround): свои стоящие с evict уходят
    /// сами, едущих не трогают, чужих остаётся ждать. ignore — кто не в счёт; stays — кого не выгонять.
    /// </summary>
    public static bool Clear(Call call, uint player, int x, int y, int w, int h, bool evict, uint ignore = 0, Func<uint, bool>? stays = null)
    {
        var inside = UnitsIn(x, y, w, h, ignore);
        if (inside.Length == 0)
            return true;
        if (evict)
        {
            var own = inside.Where(entity => call.IsOwn(player, entity) && !call.Has(entity, S.Path) && !(stays?.Invoke(entity) ?? false)).ToList();
            Evict(call, x, y, w, h, own);
        }
        return false;
    }
}
