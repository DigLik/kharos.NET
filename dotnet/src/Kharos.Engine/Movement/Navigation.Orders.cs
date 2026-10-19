using Kharos.Engine.Numerics;
using Kharos.Engine.World;

namespace Kharos.Engine.Movement;

/// <summary>Чем кончился приказ движения (<c>orderMove</c>).</summary>
public enum Moved : byte
{
    /// <summary>Проложен новый путь.</summary>
    Set,
    /// <summary>Идти некуда: путь снять.</summary>
    Removed,
    /// <summary>Путь остался прежним: юнит врос, поиск оборвала норма тика или затор без пути.</summary>
    Kept,
}

/// <summary>
/// Путь, проложенный приказом движения, — ещё не в мире: движок кладёт его в Path сам (<see cref="Navigation.Apply"/>),
/// мод — командами по ответу Plan. Поля Path при <see cref="Moved.Set"/>: points, goalX, goalY, wait 0, tries, near,
/// roads (у наземных), direct false, stuck false, best −1, slot false.
/// </summary>
public sealed class PathPlan
{
    public Moved Result { get; internal set; } = Moved.Kept;
    public int GoalX { get; internal set; }
    public int GoalY { get; internal set; }

    /// <summary>Точки x, y подряд — по центрам тайлов, спрямлённые.</summary>
    public List<double> Points { get; } = [];

    /// <summary>Путь проложен с дорогой в виду (Path.roads).</summary>
    public bool Roads { get; internal set; }

    /// <summary>Летающему: roads не пишется.</summary>
    public bool Air { get; internal set; }

    public double Tries { get; internal set; }
    public double Near { get; internal set; }

    internal PathPlan Kept()
    {
        Result = Moved.Kept;
        return this;
    }

    internal PathPlan Removed()
    {
        Result = Moved.Removed;
        return this;
    }
}

public sealed partial class Navigation
{
    /// <summary>После скольких перепрокладок подряд юнит считается застрявшим в заторе.</summary>
    public const double JamTries = 3;

    /// <summary>Сколько тайлов осматривает поиск, когда к цели надо только подойти на расстояние.</summary>
    private const double ApproachLimit = 3000;
    /// <summary>Во сколько раз дольше ищется путь, если обычный поиск не дошёл до цели.</summary>
    private const double LongSearch = 4;
    /// <summary>Сколько тайлов заливает проверка кармана перед долгим поиском.</summary>
    private const int LongPocket = 1000;
    /// <summary>На сколько колец от цели, в которую не встать, ищется тайл, куда встать можно.</summary>
    private const int StandRadius = 8;

    private readonly List<int> _tiles = [];
    private readonly List<int> _longer = [];
    private readonly List<double> _centers = [];
    private readonly HashSet<long> _pocket = [];

    /// <summary>
    /// Путь юнита в тайл (x, y) (<c>orderMove</c>), мира не меняет. Стоящих юнитов путь обходит (кроме ignore — сам
    /// юнит не в счёт всегда); цель занята — свободный тайл рядом; в цель не встать — ближайший тайл, куда можно.
    /// tries — который раз прокладывается путь к этой цели (≥ <see cref="JamTries"/> — затор); near — на сколько тайлов
    /// достаточно подойти; limit — сколько тайлов осмотреть (null — <see cref="PathSearch.SearchLimit"/>).
    /// </summary>
    public PathPlan OrderMove(uint entity, int x, int y, IReadOnlySet<uint>? ignore = null, double tries = 0, double near = 0, double? limit = null)
    {
        var plan = new PathPlan { Tries = tries, Near = near };
        var unit = Unit(entity);
        // Юнит, который разворачивается, с места не трогается.
        if (!unit.Placed || unit.Rooted)
            return plan.Kept();
        var air = unit.Chassis.Air;
        plan.Air = air;
        var jam = tries >= JamTries;
        var group = ignore is null ? new HashSet<uint> { entity } : (ignore.Contains(entity) ? ignore : new HashSet<uint>(ignore) { entity });
        var taken = Standing(group, unit.Radius, air, jam);
        if (near == 0 && taken.Contains(x, y))
        {
            var free = new List<int>(2);
            FreeTilesNear(x, y, 1, 1, taken, air, free);
            if (free.Count == 0)
                return plan.Removed();
            (x, y) = (free[0], free[1]);
        }
        plan.GoalX = x;
        plan.GoalY = y;
        if (air)
        {
            // Летающему преград нет: он летит к цели по прямой.
            if (!InBounds(x, y))
                return plan.Removed();
            plan.Points.Add(x + 0.5);
            plan.Points.Add(y + 0.5);
            plan.Result = Moved.Set;
            return plan;
        }
        var (fromX, fromY) = (PathSearch.Floor(unit.X), PathSearch.Floor(unit.Y));
        var foot = unit.Chassis.Foot;
        if (near == 0 && !(x == fromX && y == fromY) && !IsWalkable(x, y, foot) && NearestStand(x, y, fromX, fromY, foot, taken) is { } stand)
            (x, y) = stand;
        plan.GoalX = x;
        plan.GoalY = y;
        var walker = new Walker(this, fromX, fromY, foot, unit.Chassis, unit.Radius, taken);
        var fastest = unit.Chassis.Fastest > 1 && RoadInSight(entity) ? 1 / unit.Chassis.Fastest : 1;
        var tiles = new List<int>();
        Search.FindPath(walker, fromX, fromY, x, y, near, near != 0 ? ApproachLimit : limit ?? PathSearch.SearchLimit, fastest, tiles);
        bool Reaches(List<int> path)
        {
            if (path.Count == 0)
                return fromX == x && fromY == y;
            double dx = path[^2] - x, dy = path[^1] - y;
            return dx * dx + dy * dy <= near * near;
        }
        // Поиск оборвала норма тика, а не преграда: прежний путь остаётся как был.
        if (Search.Cut && !Reaches(tiles))
            return plan.Kept();
        if (near == 0 && !Reaches(tiles) && !PathSearch.BeyondWindow(fromX, fromY, x, y))
        {
            // Долгий поиск — только если цель не в замкнутом кармане, куда отсюда не попасть.
            VehicleReach(x, y, LongPocket, foot, _pocket);
            if (_pocket.Count >= LongPocket || _pocket.Contains(Key(fromX, fromY)))
            {
                var longer = new List<int>();
                Search.FindPath(walker, fromX, fromY, x, y, near, (limit ?? PathSearch.SearchLimit) * LongSearch, fastest, longer);
                if (Reaches(longer))
                    tiles = longer;
            }
        }
        // Уже достаточно близко, а идти всё равно велят: надо подойти вплотную.
        if (near != 0 && tiles.Count == 0 && (double)((fromX - x) * (fromX - x) + (fromY - y) * (fromY - y)) <= near * near)
            return OrderMove(entity, x, y, ignore, tries, 0, ApproachLimit);
        var centers = new List<double>(tiles.Count);
        foreach (var value in tiles)
            centers.Add(value + 0.5);
        PathSearch.SmoothPath(walker, unit.X, unit.Y, centers, plan.Points);
        if (plan.Points.Count > 0)
        {
            plan.Roads = fastest < 1;
            plan.Result = Moved.Set;
            return plan;
        }
        // Из затора пока не выбраться: приказ остаётся, юнит попробует снова.
        return jam ? plan.Kept() : plan.Removed();
    }

    /// <summary>Кладёт путь в мир: Set — в Path (новый — по умолчанию, есть — поля поверх), Removed — снимает Path.</summary>
    public void Apply(uint entity, PathPlan plan)
    {
        switch (plan.Result)
        {
            case Moved.Set:
                if (!World.Alive(entity))
                    return;
                World.Add(entity, Builtin.Path);
                World.SetBytes(entity, Builtin.Path, F.PathPoints.Index, PointBytes(plan.Points));
                World.SetNumber(entity, Builtin.Path, F.PathGoalX.Index, plan.GoalX);
                World.SetNumber(entity, Builtin.Path, F.PathGoalY.Index, plan.GoalY);
                World.SetNumber(entity, Builtin.Path, F.PathWait.Index, 0);
                World.SetNumber(entity, Builtin.Path, F.PathTriesField.Index, plan.Tries);
                World.SetNumber(entity, Builtin.Path, F.PathNear.Index, plan.Near);
                if (!plan.Air)
                    World.SetNumber(entity, Builtin.Path, F.PathRoads.Index, plan.Roads ? 1 : 0);
                World.SetNumber(entity, Builtin.Path, F.PathDirect.Index, 0);
                World.SetNumber(entity, Builtin.Path, F.PathStuck.Index, 0);
                World.SetNumber(entity, Builtin.Path, F.PathBest.Index, -1);
                World.SetNumber(entity, Builtin.Path, F.PathSlot.Index, 0);
                break;
            case Moved.Removed:
                World.Remove(entity, Builtin.Path);
                break;
        }
    }

    /// <summary>Приказ движения сразу в мир (<c>orderMove</c> TS): путь — в Path.</summary>
    public Moved Order(uint entity, int x, int y, IReadOnlySet<uint>? ignore = null, double tries = 0, double near = 0, double? limit = null)
    {
        var plan = OrderMove(entity, x, y, ignore, tries, near, limit);
        Apply(entity, plan);
        return plan.Result;
    }

    /// <summary>
    /// Ближайший к (x, y) тайл, где наземный может стоять и нет стоящих (<c>nearestStand</c>), не дальше
    /// <see cref="StandRadius"/> колец; среди равноудалённых — ближний к (fromX, fromY).
    /// </summary>
    private (int X, int Y)? NearestStand(int x, int y, int fromX, int fromY, bool foot, StandingTiles taken)
    {
        for (var ring = 1; ring <= StandRadius; ring++)
        {
            (int, int)? best = null;
            var bestOrder = double.PositiveInfinity;
            for (var dy = -ring; dy <= ring; dy++)
                for (var dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                        continue;
                    var (tileX, tileY) = (x + dx, y + dy);
                    if (!IsWalkable(tileX, tileY, foot) || taken.Contains(tileX, tileY))
                        continue;
                    var order = (double)(dx * dx + dy * dy) * 1e6 + (double)(tileX - fromX) * (tileX - fromX) + (double)(tileY - fromY) * (tileY - fromY);
                    if (order >= bestOrder)
                        continue;
                    best = (tileX, tileY);
                    bestOrder = order;
                }
            if (best is not null)
                return best;
        }
        return null;
    }

    /// <summary>Сетка поиска пути юнита: свой тайл проходим всегда, занятое стоящими — нет; цена шага — по местности.</summary>
    private readonly struct Walker(Navigation nav, int fromX, int fromY, bool foot, Chassis chassis, double radius, StandingTiles taken) : IPathGrid
    {
        public bool Walkable(int x, int y) => (x == fromX && y == fromY) || (nav.IsWalkable(x, y, foot) && !taken.Contains(x, y));

        public double Slowness(int x, int y) => 1 / nav.TerrainSpeed(chassis, radius, x, y);
    }

    /// <summary>Сетка «где может стоять это тело» без цены шага: проверка прямой мимо занятой точки.</summary>
    internal readonly struct StandGrid(Navigation nav, bool air, bool foot) : IPathGrid
    {
        public bool Walkable(int x, int y) => nav.CanStand(air, x, y, foot);

        public double Slowness(int x, int y) => 1;
    }

    // Группа.

    /// <summary>Место юнита в группе (<see cref="GroupMove"/>).</summary>
    public enum GroupMode : byte
    {
        /// <summary>Летающий: путь из одной точки — центра тайла (direct false).</summary>
        Air = 0,
        /// <summary>Наземный: ехать к тайлу напрямую (direct true), путь — только если упрётся.</summary>
        Direct = 1,
        /// <summary>Не трогать: юнит врос, его нет или мест не нашлось.</summary>
        Skip = 2,
    }

    /// <summary>
    /// Места группе у тайла (x, y) (<c>orderGroupMove</c>): каждому — свой тайл через шаг группы, от ближних к дальним, по
    /// порядку списка; мест меньше — лишние идут в последнее. Летающие и наземные расходятся порознь. Мира не меняет.
    /// </summary>
    public void GroupMove(IReadOnlyList<uint> all, int x, int y, Span<(int X, int Y, GroupMode Mode)> into)
    {
        for (var i = 0; i < all.Count; i++)
            into[i] = (x, y, GroupMode.Skip);
        var placed = new List<int>();
        for (var i = 0; i < all.Count; i++)
            if (World.Has(all[i], Builtin.Position))
                placed.Add(i);
        GroupMove(all, placed, x, y, into);
    }

    private void GroupMove(IReadOnlyList<uint> all, List<int> indexes, int x, int y, Span<(int X, int Y, GroupMode Mode)> into)
    {
        if (indexes.Count == 0)
            return;
        var air = ChassisOf(all[indexes[0]]).Air;
        var units = indexes.FindAll(i => ChassisOf(all[i]).Air == air);
        if (units.Count < indexes.Count)
            GroupMove(all, indexes.FindAll(i => ChassisOf(all[i]).Air != air), x, y, into);
        var group = new HashSet<uint>();
        var radius = double.NegativeInfinity;
        foreach (var i in units)
        {
            group.Add(all[i]);
            radius = Math.Max(radius, Unit(all[i]).Radius);
        }
        var taken = Standing(group, radius, air, false);
        var tiles = new List<int>();
        FreeTilesNear(x, y, units.Count, 0, taken, air, tiles);
        if (tiles.Count == 0)
            return;
        for (var k = 0; k < units.Count; k++)
        {
            var entity = all[units[k]];
            var at = Math.Min(k * 2, tiles.Count - 2);
            if (air)
            {
                // Как sendToTiles: orderMove каждому, друг другу юниты группы не помеха.
                var plan = OrderMove(entity, tiles[at], tiles[at + 1], group);
                into[units[k]] = plan.Result == Moved.Set ? (plan.GoalX, plan.GoalY, GroupMode.Air) : (tiles[at], tiles[at + 1], GroupMode.Skip);
            }
            else if (!Unit(entity).Rooted)
            {
                into[units[k]] = (tiles[at], tiles[at + 1], GroupMode.Direct);
            }
        }
    }

    /// <summary>Группа к тайлу сразу в мир: Path напрямую наземным, путь из точки летающим.</summary>
    public void OrderGroup(IReadOnlyList<uint> units, int x, int y)
    {
        var slots = new (int X, int Y, GroupMode Mode)[units.Count];
        GroupMove(units, x, y, slots);
        for (var i = 0; i < units.Count; i++)
        {
            var (goalX, goalY, mode) = slots[i];
            if (mode == GroupMode.Skip)
                continue;
            var plan = new PathPlan { Result = Moved.Set, GoalX = goalX, GoalY = goalY, Air = mode == GroupMode.Air };
            plan.Points.Add(goalX + 0.5);
            plan.Points.Add(goalY + 0.5);
            Apply(units[i], plan);
            if (mode == GroupMode.Direct)
                World.SetNumber(units[i], Builtin.Path, F.PathDirect.Index, 1);
        }
    }

    // Норма поиска инструментов.

    private readonly Dictionary<uint, (ulong Tick, double Spent)> _pools = [];

    /// <summary>
    /// Поиски в норме pool: все поиски с этим номером в тике tick вместе осматривают не больше budget тайлов (как
    /// <c>withSearchQuota</c> на общий счётчик за тик в hauling.ts и combat.ts). pool 0 — без нормы. false — норма тика
    /// уже вышла, run не звался.
    /// </summary>
    public bool WithPool(uint pool, double budget, ulong tick, Action run)
    {
        if (pool == 0)
        {
            run();
            return true;
        }
        var (at, spent) = _pools.GetValueOrDefault(pool);
        if (at != tick)
            spent = 0;
        var left = budget - spent;
        if (left <= 0)
        {
            _pools[pool] = (tick, spent);
            return false;
        }
        var before = Search.Searched;
        var previous = Search.OpenQuota(left);
        try
        {
            run();
        }
        finally
        {
            Search.CloseQuota(previous);
            _pools[pool] = (tick, spent + (Search.Searched - before));
        }
        return true;
    }

    // Подъезд.

    /// <summary>Итог подъезда (<see cref="Approach"/>).</summary>
    public enum StandResult : byte
    {
        /// <summary>Путь к месту — в плане: положить в Path.</summary>
        Found = 0,
        /// <summary>Норма тика вышла до поиска: ничего не менять, это не промах.</summary>
        Cut = 1,
        /// <summary>Места нет: снять Path, промах.</summary>
        None = 2,
        /// <summary>Уже на месте: Path не трогать.</summary>
        Here = 3,
    }

    /// <summary>На сколько тайлов ближе радиуса луча встаёт подъезжающий.</summary>
    private const double ApproachMargin = 0.5;
    /// <summary>Сколько мест у цели пробует подъезд, прежде чем сдаться.</summary>
    private const int ApproachTries = 4;
    /// <summary>Карман — замкнутый пятачок меньше стольких тайлов (POCKET в inventory.ts).</summary>
    public const int Pocket = 48;
    /// <summary>В каком поле вокруг цели стоящие не мешают пути к ней, если иначе не проехать.</summary>
    private const double AsideReach = 6;

    /// <summary>
    /// Подъезд (<c>approach</c> в inventory.ts): юнит встаёт так, чтобы до цели дотянулся луч длиной radius, — на
    /// ближайший к нему свободный тайл, откуда хватает и куда можно доехать; места в чужом кармане (меньше
    /// <see cref="Pocket"/> тайлов) не пробуются. Цель — прямоугольник (x, y, w, h) от левого верхнего угла (здание,
    /// дроп) или круг радиуса r с центром (x, y) (юнит). Мира не меняет: путь — в plan.
    /// </summary>
    public StandResult Approach(uint entity, double x, double y, double w, double h, double r, double radius, out PathPlan plan)
    {
        plan = new PathPlan();
        var unit = Unit(entity);
        if (!unit.Placed)
            return StandResult.None;
        var (atX, atY) = r > 0 ? (x - r, y - r) : (x, y);
        var width = w != 0 ? w : r * 2;
        var height = h != 0 ? h : r * 2;
        var size = unit.Radius;
        var reach = Math.Max(0, radius - ApproachMargin);
        var taken = Standing(new HashSet<uint> { entity }, size, false, false);
        var span = Math.Ceiling(reach + size);
        var foot = unit.Chassis.Foot;
        var places = new List<(int X, int Y, double Order, int Index)>();
        for (var tileY = PathSearch.Floor(atY - span); tileY < atY + height + span; tileY++)
            for (var tileX = PathSearch.Floor(atX - span); tileX < atX + width + span; tileX++)
            {
                var dx = Math.Max(0, Math.Max(atX - tileX - 0.5, tileX + 0.5 - atX - width));
                var dy = Math.Max(0, Math.Max(atY - tileY - 0.5, tileY + 0.5 - atY - height));
                if (DetMath.Hypot(dx, dy) - size > reach)
                    continue;
                if (!IsWalkable(tileX, tileY, foot) || Occupancy.SiteAt(tileX, tileY) != 0)
                    continue;
                var distance = DetMath.Hypot(tileX + 0.5 - unit.X, tileY + 0.5 - unit.Y);
                places.Add((tileX, tileY, distance + (taken.Contains(tileX, tileY) ? 1e6 : 0), places.Count));
            }
        // Сортировка JS устойчива: равные — в порядке обхода.
        places.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Index.CompareTo(b.Index));
        var tries = new List<(int X, int Y)>();
        var from = Key(PathSearch.Floor(unit.X), PathSearch.Floor(unit.Y));
        var pockets = new List<HashSet<long>>();
        foreach (var place in places)
        {
            if (tries.Count >= ApproachTries)
                break;
            var key = Key(place.X, place.Y);
            var pocket = pockets.Find(tiles => tiles.Contains(key));
            if (pocket is null)
            {
                pocket = [];
                VehicleReach(place.X, place.Y, Pocket, false, pocket);
                pockets.Add(pocket);
            }
            if (pocket.Count >= Pocket || pocket.Contains(from))
                tries.Add((place.X, place.Y));
        }
        // Путь юнита по ходу подбора мест — как его меняли бы orderMove в TS.
        var virtualPath = new List<double>();
        var hasPath = ReadPoints(entity, virtualPath);
        PathPlan? last = null;
        var changed = false;
        bool GoTo(int placeX, int placeY, IReadOnlySet<uint>? ignore)
        {
            if (PathSearch.Floor(unit.X) == placeX && PathSearch.Floor(unit.Y) == placeY)
                return true;
            var next = OrderMove(entity, placeX, placeY, ignore);
            if (next.Result == Moved.Set)
            {
                (hasPath, changed, last) = (true, true, next);
                virtualPath.Clear();
                virtualPath.AddRange(next.Points);
            }
            else if (next.Result == Moved.Removed)
            {
                (hasPath, changed, last) = (false, true, null);
                virtualPath.Clear();
            }
            if (PathSearch.BeyondWindow(PathSearch.Floor(unit.X), PathSearch.Floor(unit.Y), placeX, placeY))
                return hasPath && virtualPath.Count > 0;
            return hasPath && virtualPath.Count > 0 && PathSearch.Floor(virtualPath[^2]) == placeX && PathSearch.Floor(virtualPath[^1]) == placeY;
        }
        var success = false;
        foreach (var (placeX, placeY) in tries)
            if (GoTo(placeX, placeY, null))
            {
                success = true;
                break;
            }
        if (!success)
        {
            // Проход загородили стоящие: путь — сквозь них, подъехав, юнит попросит их посторониться.
            var near = new HashSet<uint> { entity };
            var bodies = World.Store(Builtin.Body).Entities;
            var positions = World.Store(Builtin.Position);
            foreach (var other in bodies)
            {
                var row = positions.RowOf(other);
                if (row < 0)
                    continue;
                var (px, py) = (positions.RawColumn(0)[row], positions.RawColumn(1)[row]);
                if (DetMath.Hypot(px - atX - width / 2, py - atY - height / 2) < span + Math.Max(width, height) + AsideReach)
                    near.Add(other);
            }
            foreach (var (placeX, placeY) in tries)
                if (GoTo(placeX, placeY, near))
                {
                    success = true;
                    break;
                }
        }
        if (success)
        {
            if (!changed)
                return StandResult.Here;
            if (last is not null)
            {
                plan = last;
                return StandResult.Found;
            }
        }
        plan = new PathPlan { Result = Moved.Removed };
        return StandResult.None;
    }
}
