using Kharos.Engine.World;

namespace Kharos.Engine.Movement;

/// <summary>
/// Тайлы, занятые стоящими юнитами (<c>standingUnits</c>), — набор для поиска пути и мест. Тот же набор, что
/// <see cref="Navigation.StandingUnits"/> кладёт в HashSet, но без обхода всех корпусов на каждый поиск: покрытие
/// стоящих считается раз на состояние мира (номера правок Body, Position, Path, Mover) и корпус, а ignore вычитается
/// из него. Годится, пока мир не менялся; после правки — ошибка, а не неверный ответ.
/// </summary>
public sealed class StandingTiles
{
    private readonly Navigation.Coverage? _coverage;
    private readonly int _stamp;
    private readonly Dictionary<int, int>? _minus;
    private readonly IReadOnlySet<uint>? _ignore;
    private readonly HashSet<long>? _exact;

    internal StandingTiles(Navigation.Coverage coverage, IReadOnlySet<uint>? ignore, Dictionary<int, int>? minus)
    {
        _coverage = coverage;
        _stamp = coverage.Stamp;
        _ignore = ignore;
        _minus = minus;
    }

    internal StandingTiles(HashSet<long> exact) => _exact = exact;

    public bool Contains(long key)
    {
        var (x, y) = Navigation.Unkey(key);
        return Contains(x, y);
    }

    public bool Contains(int x, int y)
    {
        if (_exact is not null)
            return _exact.Contains(Navigation.Key(x, y));
        var coverage = _coverage!;
        if (coverage.Stamp != _stamp)
            throw new InvalidOperationException("стоящие юниты: мир изменился, пока набор был в ходу");
        var index = coverage.Index(x, y);
        if (index < 0 || coverage.Overflow)
            return coverage.Covers(x, y, _ignore);
        int count = coverage.Counts[index];
        if (count == 0)
            return false;
        return _minus is null || count > _minus.GetValueOrDefault(index);
    }
}

public sealed partial class Navigation
{
    /// <summary>Сколько покрытий (ходовая, затор, корпус) помнится сразу.</summary>
    private const int Coverages = 8;

    private readonly List<Coverage> _coverages = [];
    private readonly Candidates[] _candidates = [new(), new(), new(), new()];
    private int _coverageStamp;
    private long _coverageUse;

    /// <summary>Номера правок того, из чего складываются стоящие (Body, Position, Path, Mover).</summary>
    private (ulong, ulong, ulong, ulong) StandingVersion() => (World.Store(Builtin.Body).Version, World.Store(Builtin.Position).Version,
        World.Store(Builtin.Path).Version, World.Store(Builtin.Mover).Version);

    /// <summary>
    /// Тайлы, занятые стоящими юнитами (<c>standingUnits</c>) — тот же набор, что у <see cref="StandingUnits"/>: тайл
    /// занят, если, встав в его центр, юнит радиуса radius задел бы стоящего; ignore — кого не считать; air — считать
    /// летающих, а не наземных; jammed — считать и застрявших в заторе. Набор годится, пока мир не менялся.
    /// </summary>
    public StandingTiles Standing(IReadOnlySet<uint>? ignore, double radius, bool air, bool jammed)
    {
        if (Bounds is not { } bounds || (long)bounds.Width * bounds.Height > int.MaxValue / 2)
        {
            var exact = new HashSet<long>();
            StandingUnits(ignore, radius, air, jammed, exact);
            return new StandingTiles(exact);
        }
        var version = StandingVersion();
        var coverage = CoverageOf(bounds, version, radius, air, jammed);
        Dictionary<int, int>? minus = null;
        if (ignore is not null && ignore.Count > 0 && !coverage.Overflow)
        {
            var bodies = World.Store(Builtin.Body);
            var radii = bodies.RawColumn(F.BodyRadius);
            foreach (var entity in ignore)
            {
                var row = bodies.RowOf(entity);
                if (row < 0 || !IsStanding(entity, air, jammed, out var x, out var y))
                    continue;
                minus ??= [];
                coverage.Mark(x, y, radii[row] + radius, minus);
            }
        }
        return new StandingTiles(coverage, ignore, minus);
    }

    /// <summary>Стоящий ли корпус по правилам <see cref="StandingUnits"/> (без ignore) и где он.</summary>
    private bool IsStanding(uint entity, bool air, bool jammed, out double x, out double y)
    {
        (x, y) = (0, 0);
        var positions = World.Store(Builtin.Position);
        var at = positions.RowOf(entity);
        if (at < 0 || ChassisOf(entity).Air != air)
            return false;
        var paths = World.Store(Builtin.Path);
        var path = paths.RowOf(entity);
        if (path >= 0 && !(jammed && paths.RawColumn(F.PathTries)[path] >= JamTries))
            return false;
        (x, y) = (positions.RawColumn(0)[at], positions.RawColumn(1)[at]);
        return true;
    }

    private Coverage CoverageOf(TileBounds bounds, (ulong, ulong, ulong, ulong) version, double radius, bool air, bool jammed)
    {
        var use = ++_coverageUse;
        Coverage? found = null;
        foreach (var coverage in _coverages)
            if (coverage.Air == air && coverage.Jammed == jammed && BitConverter.DoubleToInt64Bits(coverage.Radius) == BitConverter.DoubleToInt64Bits(radius) && coverage.Bounds == bounds)
            {
                found = coverage;
                break;
            }
        if (found is null)
        {
            if (_coverages.Count >= Coverages)
            {
                var oldest = 0;
                for (var i = 1; i < _coverages.Count; i++)
                    if (_coverages[i].Use < _coverages[oldest].Use)
                        oldest = i;
                // Старое покрытие не переиспользуется: набор, что ещё в ходу, держит его как было.
                _coverages.RemoveAt(oldest);
            }
            found = new Coverage(bounds, radius, air, jammed);
            _coverages.Add(found);
        }
        found.Use = use;
        if (!found.Built || found.Version != version)
        {
            var candidates = CandidatesOf(version, air, jammed);
            found.Fill(candidates, version, ++_coverageStamp);
        }
        return found;
    }

    private Candidates CandidatesOf((ulong, ulong, ulong, ulong) version, bool air, bool jammed)
    {
        var candidates = _candidates[(air ? 2 : 0) + (jammed ? 1 : 0)];
        if (candidates.Built && candidates.Version == version)
            return candidates;
        candidates.Clear();
        var bodies = World.Store(Builtin.Body);
        var entities = bodies.Entities;
        var radii = bodies.RawColumn(F.BodyRadius);
        var positions = World.Store(Builtin.Position);
        var paths = World.Store(Builtin.Path);
        var tries = paths.RawColumn(F.PathTries);
        var xs = positions.RawColumn(0);
        var ys = positions.RawColumn(1);
        for (var row = 0; row < entities.Length; row++)
        {
            var entity = entities[row];
            var at = positions.RowOf(entity);
            if (at < 0 || ChassisOf(entity).Air != air)
                continue;
            var path = paths.RowOf(entity);
            if (path >= 0 && !(jammed && tries[path] >= JamTries))
                continue;
            candidates.Add(entity, xs[at], ys[at], radii[row]);
        }
        candidates.Version = version;
        candidates.Built = true;
        return candidates;
    }

    /// <summary>Стоящие корпуса (без ignore) при одном состоянии мира: номер, место, радиус.</summary>
    internal sealed class Candidates
    {
        public uint[] Entities = new uint[64];
        public double[] X = new double[64], Y = new double[64], R = new double[64];
        public int Count;
        public bool Built;
        public (ulong, ulong, ulong, ulong) Version;
        /// <summary>Растёт с каждым пересчётом: покрытие видит, что его стоящих пересчитали.</summary>
        public int Stamp;

        public void Clear()
        {
            Count = 0;
            Built = false;
            Stamp++;
        }

        public void Add(uint entity, double x, double y, double r)
        {
            if (Count == Entities.Length)
            {
                Array.Resize(ref Entities, Count * 2);
                Array.Resize(ref X, Count * 2);
                Array.Resize(ref Y, Count * 2);
                Array.Resize(ref R, Count * 2);
            }
            (Entities[Count], X[Count], Y[Count], R[Count]) = (entity, x, y, r);
            Count++;
        }
    }

    /// <summary>Сколько стоящих задевает каждый тайл карты для корпуса radius.</summary>
    internal sealed class Coverage(TileBounds bounds, double radius, bool air, bool jammed)
    {
        public readonly TileBounds Bounds = bounds;
        public readonly double Radius = radius;
        public readonly bool Air = air, Jammed = jammed;
        public readonly ushort[] Counts = new ushort[bounds.Width * bounds.Height];
        private readonly List<int> _touched = [];
        private Candidates _candidates = null!;
        private int _candidatesStamp;
        public (ulong, ulong, ulong, ulong) Version;
        public bool Built;
        /// <summary>Тайл задели больше 65535 стоящих: ответы — перебором.</summary>
        public bool Overflow;
        public int Stamp;
        public long Use;

        public int Index(int x, int y) =>
            x >= Bounds.Left && y >= Bounds.Top && x < Bounds.Right && y < Bounds.Bottom ? (y - Bounds.Top) * Bounds.Width + (x - Bounds.Left) : -1;

        public void Fill(Candidates candidates, (ulong, ulong, ulong, ulong) version, int stamp)
        {
            foreach (var index in _touched)
                Counts[index] = 0;
            _touched.Clear();
            Overflow = false;
            _candidates = candidates;
            _candidatesStamp = candidates.Stamp;
            for (var i = 0; i < candidates.Count && !Overflow; i++)
                Mark(candidates.X[i], candidates.Y[i], candidates.R[i] + Radius, null);
            Version = version;
            Built = true;
            Stamp = stamp;
        }

        /// <summary>Тайлы, которые задевает стоящий в (x, y) при сумме радиусов reach, — тот же обход, что в StandingUnits.</summary>
        public void Mark(double x, double y, double reach, Dictionary<int, int>? minus)
        {
            var (right, bottom) = (PathSearch.Floor(x + reach), PathSearch.Floor(y + reach));
            for (var tileY = PathSearch.Floor(y - reach); tileY <= bottom; tileY++)
            {
                var dy = tileY + 0.5 - y;
                for (var tileX = PathSearch.Floor(x - reach); tileX <= right; tileX++)
                {
                    var dx = tileX + 0.5 - x;
                    if (!(dx * dx + dy * dy < reach * reach))
                        continue;
                    var index = Index(tileX, tileY);
                    if (index < 0)
                        continue;
                    if (minus is not null)
                    {
                        System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(minus, index, out _)++;
                        continue;
                    }
                    ref var count = ref Counts[index];
                    if (count == 0)
                        _touched.Add(index);
                    else if (count == ushort.MaxValue)
                    {
                        Overflow = true;
                        return;
                    }
                    count++;
                }
            }
        }

        /// <summary>Перебором: задевает ли тайл кто-то из стоящих, кроме ignore (тайлы вне карты и переполнение).</summary>
        public bool Covers(int tileX, int tileY, IReadOnlySet<uint>? ignore)
        {
            var c = _candidates;
            if (c.Stamp != _candidatesStamp)
                throw new InvalidOperationException("стоящие юниты: мир изменился, пока набор был в ходу");
            for (var i = 0; i < c.Count; i++)
            {
                if (ignore is not null && ignore.Contains(c.Entities[i]))
                    continue;
                var (x, y) = (c.X[i], c.Y[i]);
                var reach = c.R[i] + Radius;
                if (tileX < PathSearch.Floor(x - reach) || tileX > PathSearch.Floor(x + reach) || tileY < PathSearch.Floor(y - reach) || tileY > PathSearch.Floor(y + reach))
                    continue;
                double dx = tileX + 0.5 - x, dy = tileY + 0.5 - y;
                if (dx * dx + dy * dy < reach * reach)
                    return true;
            }
            return false;
        }
    }
}
