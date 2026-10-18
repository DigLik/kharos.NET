namespace Kharos.Engine.World;

/// <summary>Сущности с местом по клеткам 4×4 тайла — для поиска в круге. Строится заново, когда места сдвинулись.</summary>
internal sealed class SpatialGrid
{
    public const double Cell = 4;

    private readonly Dictionary<long, (int Start, int Count)> _cells = [];
    private uint[] _entities = [];
    private double[] _xs = [];
    private double[] _ys = [];
    private long[] _keys = [];

    public ulong Revision { get; private set; } = ulong.MaxValue;

    private static long Key(int cx, int cy) => ((long)cx << 32) | (uint)cy;

    public void Build(GameWorld world)
    {
        var store = world.Store(Builtin.Position);
        var n = store.Count;
        var xs = store.Column(0);
        var ys = store.Column(1);
        if (_keys.Length < n)
        {
            _keys = new long[n];
            _entities = new uint[n];
            _xs = new double[n];
            _ys = new double[n];
        }
        _cells.Clear();
        for (var row = 0; row < n; row++)
        {
            var key = Key((int)Math.Floor(xs[row] / Cell), (int)Math.Floor(ys[row] / Cell));
            _keys[row] = key;
            _cells[key] = _cells.TryGetValue(key, out var cell) ? (0, cell.Count + 1) : (0, 1);
        }
        var start = 0;
        foreach (var key in _cells.Keys.ToArray())
        {
            var count = _cells[key].Count;
            _cells[key] = (start, 0);
            start += count;
        }
        var entities = store.Entities;
        for (var row = 0; row < n; row++)
        {
            ref var cell = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_cells, _keys[row]);
            var at = cell.Start + cell.Count++;
            _entities[at] = entities[row];
            _xs[at] = xs[row];
            _ys[at] = ys[row];
        }
        Revision = world.PositionRevision;
    }

    /// <summary>Добавляет в into сущности в круге — в порядке клеток, не отсортированы.</summary>
    public void Find(double x, double y, double radius, List<uint> into)
    {
        var (left, right) = ((int)Math.Floor((x - radius) / Cell), (int)Math.Floor((x + radius) / Cell));
        var (top, bottom) = ((int)Math.Floor((y - radius) / Cell), (int)Math.Floor((y + radius) / Cell));
        var reach = radius * radius;
        for (var cy = top; cy <= bottom; cy++)
            for (var cx = left; cx <= right; cx++)
            {
                if (!_cells.TryGetValue(Key(cx, cy), out var cell))
                    continue;
                for (var at = cell.Start; at < cell.Start + cell.Count; at++)
                {
                    var (dx, dy) = (_xs[at] - x, _ys[at] - y);
                    if (dx * dx + dy * dy <= reach)
                        into.Add(_entities[at]);
                }
            }
    }
}
