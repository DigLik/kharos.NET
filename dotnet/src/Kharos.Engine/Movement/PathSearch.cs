using Kharos.Engine.Numerics;

namespace Kharos.Engine.Movement;

/// <summary>Местность для поиска: можно ли в тайл и во сколько раз шаг по нему дороже шага по ровному.</summary>
public interface IPathGrid
{
    bool Walkable(int x, int y);

    double Slowness(int x, int y) => 1;
}

/// <summary>
/// Поиск пути (path.ts, crates/sim/src/path.rs): A* по тайлам в восемь сторон, не наискось мимо угла; спрямление пути.
/// Та же куча, те же окна и та же норма поиска за тик: пути совпадают с TS тайл в тайл. Один на мир: счёт осмотренного,
/// норма и память окна — общие для всех поисков.
/// </summary>
public sealed class PathSearch
{
    /// <summary>Сколько тайлов поиск осматривает, прежде чем сдаться и повести к ближайшему найденному.</summary>
    public const double SearchLimit = 20000;

    private const double Diagonal = 1.4142135623730951;
    /// <summary>Насколько по бокам от линии движения должно быть свободно, в тайлах.</summary>
    private const double Clearance = 0.3;
    /// <summary>Сторона окна вокруг старта, внутри которого поиск хранит тайлы в массивах.</summary>
    private const int Window = 512;
    private const int Half = Window / 2;
    private const long Span = 1 << 15;

    private static readonly (int Dx, int Dy, double Price)[] Steps =
    [
        (1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1),
        (1, 1, Diagonal), (1, -1, Diagonal), (-1, 1, Diagonal), (-1, -1, Diagonal),
    ];

    private double _cap = double.PositiveInfinity;
    private uint[] _stamps = [];
    private double[] _costs = [];
    private int[] _parents = [];
    private byte[] _passable = [];
    private double[] _prices = [];
    private uint _generation;
    private readonly Heap _open = new();
    private readonly Dictionary<long, double> _farCost = [];
    private readonly Dictionary<long, long> _farParent = [];

    /// <summary>Сколько тайлов осмотрели все поиски (<c>searchedTiles</c>).</summary>
    public double Searched { get; private set; }

    /// <summary>Оборвала ли норма последний поиск (<c>searchCut</c>).</summary>
    public bool Cut { get; private set; }

    /// <summary>Лежит ли цель за окном поиска (<c>beyondWindow</c>).</summary>
    public static bool BeyondWindow(int fromX, int fromY, int toX, int toY) =>
        Math.Abs(toX - fromX) >= Half - 1 || Math.Abs(toY - fromY) >= Half - 1;

    /// <summary>Норма: поиски до <see cref="CloseQuota"/> вместе осматривают не больше tiles тайлов (<c>withSearchQuota</c>).</summary>
    public double OpenQuota(double tiles)
    {
        var previous = _cap;
        _cap = Math.Min(_cap, Searched + Math.Max(0, tiles));
        Cut = false;
        return previous;
    }

    public void CloseQuota(double previous) => _cap = previous;

    /// <summary>Кратчайший путь по тайлам (<c>findPath</c>): тайлы подряд x, y без начального; не дойти — к ближайшему к цели.</summary>
    public void FindPath(IPathGrid grid, int fromX, int fromY, int toX, int toY, double near, double limit, double fastest, List<int> into)
    {
        into.Clear();
        var narrowed = _cap - Searched < limit;
        Cut = false;
        limit = Math.Min(limit, _cap - Searched);
        if (limit <= 0)
        {
            Cut = true;
            return;
        }
        Search(grid, fromX, fromY, toX, toY, near, limit, fastest, into);
        Cut = narrowed && Searched >= _cap;
    }

    private void Search(IPathGrid grid, int fromX, int fromY, int toX, int toY, double near, double limit, double fastest, List<int> into)
    {
        if (!BeyondWindow(fromX, fromY, toX, toY))
        {
            if (NearPath(grid, fromX, fromY, toX, toY, near, limit, fastest, false, into))
                return;
        }
        else if (NearPath(grid, fromX, fromY, toX, toY, near, limit, fastest, true, into) && into.Count > 0)
        {
            return;
        }
        into.Clear();
        limit = Math.Min(limit, _cap - Searched);
        if (limit <= 0)
            return;
        FarPath(grid, fromX, fromY, toX, toY, near, limit, fastest, into);
    }

    private static double Estimate(int x, int y, int toX, int toY, double fastest)
    {
        double dx = Math.Abs(toX - x), dy = Math.Abs(toY - y);
        return (Math.Max(dx, dy) + (Diagonal - 1) * Math.Min(dx, dy)) * fastest;
    }

    private static bool Arrived(int x, int y, int toX, int toY, double near) =>
        near != 0 ? (double)((x - toX) * (x - toX) + (y - toY) * (y - toY)) <= near * near : x == toX && y == toY;

    /// <summary>Поиск внутри окна вокруг старта (<c>findNearPath</c>); false — цель не найдена, а поиск упёрся в край окна.</summary>
    private bool NearPath(IPathGrid grid, int fromX, int fromY, int toX, int toY, double near, double limit, double fastest, bool partial, List<int> into)
    {
        const int size = Window * Window;
        if (_stamps.Length == 0)
        {
            _stamps = new uint[size];
            _costs = new double[size];
            _parents = new int[size];
            _passable = new byte[size];
            _prices = new double[size];
        }
        var current = unchecked(++_generation);
        var (originX, originY) = (fromX - Half, fromY - Half);
        const int start = Half * Window + Half;
        _stamps[start] = current;
        _costs[start] = 0;
        _passable[start] = 1;
        var open = _open;
        open.Clear();
        open.Push(start, Estimate(fromX, fromY, toX, toY, fastest));
        var closest = start;
        var closestDistance = Estimate(fromX, fromY, toX, toY, fastest);
        double visited = 0;
        bool found = false, edge = false;
        while (open.Count > 0)
        {
            var go = visited < limit;
            visited++;
            if (!go)
                break;
            var at = (int)open.Pop();
            var localX = at % Window;
            var localY = (at - localX) / Window;
            var (x, y) = (localX + originX, localY + originY);
            if (Arrived(x, y, toX, toY, near))
            {
                closest = at;
                found = true;
                break;
            }
            var reached = _costs[at];
            foreach (var (dx, dy, price) in Steps)
            {
                var (nextX, nextY) = (localX + dx, localY + dy);
                if (nextX < 0 || nextY < 0 || nextX >= Window || nextY >= Window)
                {
                    edge = true;
                    continue;
                }
                var next = nextY * Window + nextX;
                if (!Free(grid, current, next, x + dx, y + dy))
                    continue;
                if (dx != 0 && dy != 0 && (!Free(grid, current, at + dx, x + dx, y) || !Free(grid, current, at + dy * Window, x, y + dy)))
                    continue;
                var total = reached + price * _prices[next];
                if (total >= _costs[next])
                    continue;
                _costs[next] = total;
                _parents[next] = at;
                var left = Estimate(x + dx, y + dy, toX, toY, fastest);
                if (left < closestDistance)
                {
                    closest = next;
                    closestDistance = left;
                }
                open.Push(next, total + left);
            }
        }
        Searched += visited;
        if (!found && edge && !partial)
            return false;
        into.Clear();
        for (var at = closest; at != start; at = _parents[at])
        {
            into.Add(at / Window + originY);
            into.Add(at % Window + originX);
        }
        into.Reverse();
        return true;
    }

    private bool Free(IPathGrid grid, uint current, int index, int x, int y)
    {
        if (_stamps[index] != current)
        {
            _stamps[index] = current;
            _costs[index] = double.PositiveInfinity;
            var passable = grid.Walkable(x, y);
            _passable[index] = passable ? (byte)1 : (byte)0;
            _prices[index] = passable ? grid.Slowness(x, y) : double.PositiveInfinity;
        }
        return _passable[index] == 1;
    }

    /// <summary>То же без окна: тайлы — в словарях (<c>findFarPath</c>).</summary>
    private void FarPath(IPathGrid grid, int fromX, int fromY, int toX, int toY, double near, double limit, double fastest, List<int> into)
    {
        long Key(int x, int y) => ((long)y - fromY + Span / 2) * Span + ((long)x - fromX + Span / 2);
        var cost = _farCost;
        var parent = _farParent;
        cost.Clear();
        parent.Clear();
        var open = _open;
        open.Clear();
        var start = Key(fromX, fromY);
        cost[start] = 0;
        open.Push(start, Estimate(fromX, fromY, toX, toY, fastest));
        var closest = start;
        var closestDistance = Estimate(fromX, fromY, toX, toY, fastest);
        double visited = 0;
        while (open.Count > 0)
        {
            var go = visited < limit;
            visited++;
            if (!go)
                break;
            var current = open.Pop();
            var x = (int)(current % Span) + fromX - (int)(Span / 2);
            var y = (int)(current / Span) + fromY - (int)(Span / 2);
            if (Arrived(x, y, toX, toY, near))
            {
                closest = current;
                break;
            }
            var reached = cost[current];
            foreach (var (dx, dy, price) in Steps)
            {
                var (nextX, nextY) = (x + dx, y + dy);
                if (!grid.Walkable(nextX, nextY))
                    continue;
                if (dx != 0 && dy != 0 && (!grid.Walkable(nextX, y) || !grid.Walkable(x, nextY)))
                    continue;
                var next = Key(nextX, nextY);
                var total = reached + price * grid.Slowness(nextX, nextY);
                if (total >= cost.GetValueOrDefault(next, double.PositiveInfinity))
                    continue;
                cost[next] = total;
                parent[next] = current;
                var left = Estimate(nextX, nextY, toX, toY, fastest);
                if (left < closestDistance)
                {
                    closest = next;
                    closestDistance = left;
                }
                open.Push(next, total + left);
            }
        }
        Searched += visited;
        into.Clear();
        for (var at = closest; at != start; at = parent[at])
        {
            into.Add((int)(at / Span) + fromY - (int)(Span / 2));
            into.Add((int)(at % Span) + fromX - (int)(Span / 2));
        }
        into.Reverse();
    }

    /// <summary>Свободен ли прямой путь между точками с запасом по бокам и не медленнее концов (<c>isClear</c>).</summary>
    public static bool IsClear(IPathGrid grid, double fromX, double fromY, double toX, double toY)
    {
        var (dx, dy) = (toX - fromX, toY - fromY);
        var length = DetMath.Hypot(dx, dy);
        if (length == 0)
            return grid.Walkable(Floor(fromX), Floor(fromY));
        var sideX = -dy / length * Clearance;
        var sideY = dx / length * Clearance;
        var steps = Math.Ceiling(length / 0.2);
        var from = grid.Slowness(Floor(fromX), Floor(fromY));
        var to = grid.Slowness(Floor(toX), Floor(toY));
        // С дороги прямая не сходит: срезав по обочине, юнит ехал бы медленнее, чем по найденному пути.
        var slowest = Math.Min(from, to) < 1 ? Math.Min(from, to) : Math.Max(from, to);
        for (double i = 0; i <= steps; i++)
        {
            var x = fromX + dx * i / steps;
            var y = fromY + dy * i / steps;
            if (!grid.Walkable(Floor(x + sideX), Floor(y + sideY)) || !grid.Walkable(Floor(x - sideX), Floor(y - sideY)))
                return false;
            if (grid.Slowness(Floor(x), Floor(y)) > slowest)
                return false;
        }
        return true;
    }

    /// <summary>Спрямляет путь (<c>smoothPath</c>): дальняя видная точка — скачками вдвое, потом делением пополам.</summary>
    public static void SmoothPath(IPathGrid grid, double fromX, double fromY, List<double> points, List<double> into)
    {
        into.Clear();
        var (anchorX, anchorY) = (fromX, fromY);
        var at = 0;
        while (at < points.Count)
        {
            var next = at;
            var step = 2;
            while (next + step < points.Count && IsClear(grid, anchorX, anchorY, points[next + step], points[next + step + 1]))
            {
                next += step;
                step *= 2;
            }
            step >>= 1;
            while (step >= 2)
            {
                var far = Math.Min(next + step, points.Count - 2);
                if (far > next && IsClear(grid, anchorX, anchorY, points[far], points[far + 1]))
                    next = far;
                step >>= 1;
            }
            anchorX = points[next];
            anchorY = points[next + 1];
            into.Add(anchorX);
            into.Add(anchorY);
            at = next + 2;
        }
    }

    internal static int Floor(double value) => JsMath.ToInt32(Math.Floor(value));

    /// <summary>Куча с наименьшим приоритетом наверху — как Heap в path.ts, с тем же порядком равных.</summary>
    private sealed class Heap
    {
        private long[] _items = new long[256];
        private double[] _priorities = new double[256];

        public int Count { get; private set; }

        public void Clear() => Count = 0;

        public void Push(long item, double priority)
        {
            if (Count == _items.Length)
            {
                Array.Resize(ref _items, Count * 2);
                Array.Resize(ref _priorities, Count * 2);
            }
            var at = Count++;
            while (at > 0)
            {
                var parent = (at - 1) >> 1;
                if (_priorities[parent] <= priority)
                    break;
                _items[at] = _items[parent];
                _priorities[at] = _priorities[parent];
                at = parent;
            }
            _items[at] = item;
            _priorities[at] = priority;
        }

        public long Pop()
        {
            var top = _items[0];
            var size = --Count;
            var item = _items[size];
            var priority = _priorities[size];
            if (size > 0)
            {
                var at = 0;
                while (true)
                {
                    var child = at * 2 + 1;
                    if (child >= size)
                        break;
                    if (child + 1 < size && _priorities[child + 1] < _priorities[child])
                        child++;
                    if (_priorities[child] >= priority)
                        break;
                    _items[at] = _items[child];
                    _priorities[at] = _priorities[child];
                    at = child;
                }
                _items[at] = item;
                _priorities[at] = priority;
            }
            return top;
        }
    }
}
