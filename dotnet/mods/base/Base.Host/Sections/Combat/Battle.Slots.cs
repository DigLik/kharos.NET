using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Combat;

// Места вокруг цели для гонящихся (slotsAround): каждому — точка на выстреле от края цели со своей стороны.
internal sealed partial class Battle
{
    private const double SlotGap = 0.4, SlotCell = 2.5, GuardRange = 4, FullRing = 0.25;

    /// <summary>Гонящийся за целью (Pursuer): носитель, на сколько ему подойти к краю цели и стоит ли он уже на выстреле.</summary>
    private sealed class Pursuer
    {
        public uint Entity;
        public double X, Y, Radius, Reach;
        public bool Foot, Settled;
        public uint Player;
        public int Order;
    }

    /// <summary>
    /// Раздача мест вокруг одной цели. Место каждого зависит только от тех, кто в порядке раздачи перед ним, поэтому
    /// раздаётся лениво — до того, кто спросил: в толпе спрашивают немногие, и обходить всех ради них незачем. Итог тот же,
    /// что у раздачи всем сразу.
    /// </summary>
    private sealed class Ring
    {
        public required Mark Target;
        public required List<Pursuer> Order;
        public required Dictionary<uint, int> At;
        public double CenterX, CenterY;
        public int Next;
        public bool Fetched;
        public readonly Dictionary<long, List<(double X, double Y, double Radius)>> Taken = [];
        public readonly List<(double Ring, double Radius)> Full = [];
        public readonly Dictionary<uint, (double X, double Y)> Slots = [];
    }

    /// <summary>Кто за какой целью гонится, на начало тика: цель → носители по порядку появления.</summary>
    private readonly Dictionary<uint, List<Pursuer>> _pursuers = [];

    /// <summary>Места вокруг целей; раздаются, когда за местом впервые в тике обращаются.</summary>
    private readonly Dictionary<uint, Ring> _slots = [];

    /// <summary>Проходимость тайлов на тик (isWalkable): тайл, пехота.</summary>
    private readonly Dictionary<(int, int, bool), bool> _walkable = [];

    /// <summary>Наземные носители в погоне: у носителя с несколькими турелями подход — по самой короткой из погонь.</summary>
    private void CollectPursuers()
    {
        for (var i = 0; i < _armCount; i++)
        {
            var row = _shooters[i];
            if (!_armChase[row] || _armTarget[row] == 0)
                continue;
            var entity = _armEntity[row];
            var carrier = CarrierOf(entity);
            var body = MarkOf(carrier);
            var weapon = GunOf(entity);
            if (body?.Unit is not { } unit || weapon is null || unit.Air)
                continue;
            var reach = Math.Max(weapon.MinRange + SlotMargin, weapon.Range - SlotMargin);
            var targetEntity = (uint)_armTarget[row];
            var target = MarkOf(targetEntity);
            if (!_pursuers.TryGetValue(targetEntity, out var around))
                _pursuers[targetEntity] = around = [];
            var known = around.Find(item => item.Entity == carrier);
            if (known is not null && known.Reach <= reach)
                continue;
            var settled = !HasPath(carrier) && target is not null && target.DistanceTo(body.X, body.Y) <= weapon.Range;
            var pursuer = new Pursuer
            {
                Entity = carrier, X = body.X, Y = body.Y, Radius = unit.Spec.Radius, Reach = reach, Foot = unit.Foot, Settled = settled,
                Player = body.Player, Order = known?.Order ?? around.Count,
            };
            if (known is not null)
                around[known.Order] = pursuer;
            else
                around.Add(pursuer);
        }
    }

    /// <summary>Место носителя carrier у цели; не нашлось — null.</summary>
    private (double X, double Y)? SlotOf(Mark target, uint carrier)
    {
        if (!_slots.TryGetValue(target.Entity, out var ring))
            _slots[target.Entity] = ring = RingOf(target, _pursuers.GetValueOrDefault(target.Entity) ?? []);
        if (!ring.At.TryGetValue(carrier, out var at))
            return null;
        while (ring.Next <= at)
            Place(ring, ring.Order[ring.Next++]);
        return ring.Slots.TryGetValue(carrier, out var slot) ? slot : null;
    }

    /// <summary>
    /// Порядок раздачи мест (slotsAround): сначала места занимают уже стреляющие, потом гонящиеся — от ближних к дальним.
    /// </summary>
    private static Ring RingOf(Mark target, List<Pursuer> pursuers)
    {
        var centerX = target.Left + target.Width / 2;
        var centerY = target.Top + target.Height / 2;
        var order = pursuers
            .OrderBy(item => item.Settled ? 0 : 1)
            .ThenBy(item => Distance(item.X - centerX, item.Y - centerY))
            .ThenBy(item => item.Order)
            .ToList();
        var at = new Dictionary<uint, int>(order.Count);
        for (var i = 0; i < order.Count; i++)
            at[order[i].Entity] = i;
        return new Ring { Target = target, Order = order, At = at, CenterX = centerX, CenterY = centerY };
    }

    /// <summary>Есть ли у места другие враги, кроме самой цели.</summary>
    private bool Guarded(Mark target, double x, double y, uint player)
    {
        foreach (var mark in _grid.Near(x, y, GuardRange))
            if (mark.Entity != target.Entity && Hostile(player, mark.Player) && !Allied(player, mark.Player) && !mark.Dead && mark.DistanceTo(x, y) <= GuardRange)
                return true;
        return false;
    }

    /// <summary>Проходимость квадрата вокруг (x, y) радиусом radius — в кэш тика, одним вызовом Walkable.</summary>
    private void Prefetch(double x, double y, double radius, bool foot)
    {
        var (left, top) = ((int)Math.Floor(x - radius), (int)Math.Floor(y - radius));
        var size = (int)Math.Floor(x + radius) - left + 1;
        var xy = new List<int>(size * size * 2);
        for (var ty = top; ty < top + size; ty++)
            for (var tx = left; tx < left + size; tx++)
                if (!_walkable.ContainsKey((tx, ty, foot)))
                {
                    xy.Add(tx);
                    xy.Add(ty);
                }
        if (xy.Count == 0)
            return;
        var into = new bool[xy.Count / 2];
        Tools.Walkable(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(xy), into, foot ? PathFlags.Foot : PathFlags.None);
        for (var i = 0; i < into.Length; i++)
            _walkable[(xy[i * 2], xy[i * 2 + 1], foot)] = into[i];
    }

    private bool Walkable(int x, int y, bool foot)
    {
        if (!_walkable.TryGetValue((x, y, foot), out var walkable))
            _walkable[(x, y, foot)] = walkable = Tools.Walkable(x, y, foot ? PathFlags.Foot : PathFlags.None);
        return walkable;
    }

    private static void Take(Ring ring, double x, double y, double radius)
    {
        var key = TileKey((int)Math.Floor(x / SlotCell), (int)Math.Floor(y / SlotCell));
        if (!ring.Taken.TryGetValue(key, out var cell))
            ring.Taken[key] = cell = [];
        cell.Add((x, y, radius));
    }

    private static bool Free(Ring ring, double x, double y, double radius)
    {
        var cellX = (int)Math.Floor(x / SlotCell);
        var cellY = (int)Math.Floor(y / SlotCell);
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                if (ring.Taken.TryGetValue(TileKey(cellX + dx, cellY + dy), out var cell))
                    foreach (var other in cell)
                        if (Distance(other.X - x, other.Y - y) < other.Radius + radius + SlotGap)
                            return false;
        return true;
    }

    /// <summary>
    /// Место одного гонящегося (шаг slotsAround): точка на выстреле от края цели, как можно ближе к его стороне и не ближе
    /// SLOT_GAP к соседям. Немногие встают дугой со своей стороны, на дуге тесно — лишние дальше по кругу; за цель — только
    /// если там нет других врагов. Кольца, на которых места уже не нашлось, такому же или крупнее не пробуются.
    /// </summary>
    private void Place(Ring ring, Pursuer pursuer)
    {
        var target = ring.Target;
        if (pursuer.Settled)
        {
            Take(ring, pursuer.X, pursuer.Y, pursuer.Radius);
            return;
        }
        if (!ring.Fetched)
        {
            // Проходимость кольца — одним вызовом инструмента на ходовую, а не по тайлу на каждую пробу места.
            ring.Fetched = true;
            double reach = 0;
            bool foot = false, wheels = false;
            foreach (var other in ring.Order)
                if (!other.Settled)
                {
                    reach = Math.Max(reach, other.Reach);
                    foot |= other.Foot;
                    wheels |= !other.Foot;
                }
            var around = reach + target.Radius + Math.Max(target.Width, target.Height) + 1;
            if (foot)
                Prefetch(ring.CenterX, ring.CenterY, around, true);
            if (wheels)
                Prefetch(ring.CenterX, ring.CenterY, around, false);
        }
        var (centerX, centerY) = (ring.CenterX, ring.CenterY);
        var bearing = DetMath.Atan2(pursuer.Y - centerY, pursuer.X - centerX);
        var radius = pursuer.Reach + target.Radius + Math.Min(target.Width, target.Height) / 2;
        if (ring.Full.Exists(other => Math.Abs(other.Ring - radius) < FullRing && other.Radius <= pursuer.Radius))
            return;
        // Шаг по кругу — полместа: соседи встают плотно, но друг в друга не заходят.
        var step = (pursuer.Radius + SlotGap / 2) / Math.Max(1, radius);
        for (var k = 0; k * step <= Math.PI; k++)
        {
            (double X, double Y)? found = null;
            for (var side = 0; side < (k == 0 ? 1 : 2); side++)
            {
                var angle = k == 0 ? bearing : side == 0 ? bearing + k * step : bearing - k * step;
                var dx = DetMath.Cos(angle);
                var dy = DetMath.Sin(angle);
                // Точка, где луч из центра выходит из основания цели, и от неё — на выстрел наружу.
                var edge = Math.Min(dx != 0 ? target.Width / 2 / Math.Abs(dx) : double.PositiveInfinity, dy != 0 ? target.Height / 2 / Math.Abs(dy) : double.PositiveInfinity);
                var outward = (double.IsFinite(edge) ? edge : 0) + target.Radius + pursuer.Reach;
                var x = centerX + dx * outward;
                var y = centerY + dy * outward;
                if (!Walkable((int)Math.Floor(x), (int)Math.Floor(y), pursuer.Foot) || !Free(ring, x, y, pursuer.Radius))
                    continue;
                if (k * step > Math.PI / 2 && Guarded(target, x, y, pursuer.Player))
                    continue;
                found = (x, y);
                break;
            }
            if (found is not { } spot)
                continue;
            ring.Slots[pursuer.Entity] = spot;
            Take(ring, spot.X, spot.Y, pursuer.Radius);
            return;
        }
        ring.Full.Add((radius, pursuer.Radius));
    }
}
