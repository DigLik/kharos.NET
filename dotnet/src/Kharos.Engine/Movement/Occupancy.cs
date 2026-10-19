using Kharos.Engine.World;

namespace Kharos.Engine.Movement;

/// <summary>Прямоугольник карты в тайлах: [Left, Right) × [Top, Bottom).</summary>
public readonly record struct TileBounds(int Left, int Top, int Right, int Bottom)
{
    public bool Contains(int x, int y) => x >= Left && y >= Top && x < Right && y < Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Занятость тайлов зданиями (createOccupancy в buildings.ts): номер здания на тайле, 0 — свободен. Здание — сущность с
/// Position и Footprint.solid (стоит по левому верхнему тайлу); основание берётся, когда здание появилось, а при уходе
/// тайл освобождается, только если на нём всё ещё это здание. Footprint без solid — площадка: тайлов не занимает, но на
/// ней не встают (siteAt). Ведётся по разнице: пересчёт — когда сменился состав мира или места
/// (<see cref="GameWorld.Revision"/>, <see cref="GameWorld.PositionRevision"/>), и только для изменившихся зданий.
/// </summary>
public sealed class Occupancy
{
    private readonly GameWorld _world;
    private readonly TileBounds? _bounds;
    private readonly uint[] _grid;
    private readonly Dictionary<long, uint> _outside = [];
    private readonly Dictionary<uint, Placed> _placed = [];
    private readonly HashSet<uint> _seen = [];
    private readonly List<uint> _gone = [];
    private readonly List<(uint Entity, Placed Place)> _arrived = [];
    private readonly List<(uint Entity, double X, double Y, double W, double H)> _sites = [];
    private readonly int _w, _h, _solid, _open;
    private (ulong World, ulong Positions) _key = (ulong.MaxValue, ulong.MaxValue);

    public Occupancy(GameWorld world, TileBounds? bounds)
    {
        _world = world;
        _bounds = bounds;
        _grid = bounds is { } map ? new uint[(long)map.Width * map.Height] : [];
        var def = world.Schema[Builtin.Footprint];
        _w = def.Field("w")!.Slot;
        _h = def.Field("h")!.Slot;
        _solid = def.Field("solid")!.Slot;
        _open = def.Field("open")!.Slot;
    }

    /// <summary>Номер правки занятости: растёт, когда тайл занят или освобождён (ключ кэша проходимости).</summary>
    public ulong Version { get; private set; }

    /// <summary>Сколько зданий занимают тайлы.</summary>
    public int Count
    {
        get
        {
            Refresh();
            return _placed.Count;
        }
    }

    /// <summary>Здание на тайле; 0 — свободен.</summary>
    public uint At(int x, int y)
    {
        Refresh();
        return Read(x, y);
    }

    /// <summary>Открытые ли это ворота (Footprint.open): через них ходят.</summary>
    public bool IsOpen(uint building)
    {
        var store = _world.Store(Builtin.Footprint);
        var row = store.RowOf(building);
        return row >= 0 && store.RawColumn(_open)[row] != 0;
    }

    /// <summary>Площадка (Footprint без solid), основание которой накрывает тайл; 0 — нет.</summary>
    public uint SiteAt(int x, int y)
    {
        Refresh();
        foreach (var (entity, sx, sy, w, h) in _sites)
            if (x >= sx && x < sx + w && y >= sy && y < sy + h)
                return entity;
        return 0;
    }

    /// <summary>Сверяет занятость с миром, если состав или места менялись.</summary>
    public void Refresh()
    {
        var key = (_world.Revision, _world.PositionRevision);
        if (key == _key)
            return;
        _key = key;
        var footprints = _world.Store(Builtin.Footprint);
        var positions = _world.Store(Builtin.Position);
        var entities = footprints.Entities;
        var w = footprints.RawColumn(_w);
        var h = footprints.RawColumn(_h);
        var solid = footprints.RawColumn(_solid);
        var xs = positions.RawColumn(0);
        var ys = positions.RawColumn(1);
        _seen.Clear();
        _arrived.Clear();
        _gone.Clear();
        _sites.Clear();
        for (var row = 0; row < entities.Length; row++)
        {
            var entity = entities[row];
            var at = positions.RowOf(entity);
            if (at < 0)
                continue;
            if (solid[row] == 0)
            {
                _sites.Add((entity, xs[at], ys[at], w[row], h[row]));
                continue;
            }
            _seen.Add(entity);
            var place = new Placed(xs[at], ys[at], w[row], h[row]);
            if (_placed.TryGetValue(entity, out var before))
            {
                if (before == place)
                    continue;
                _gone.Add(entity);
            }
            _arrived.Add((entity, place));
        }
        foreach (var entity in _placed.Keys)
            if (!_seen.Contains(entity))
                _gone.Add(entity);
        foreach (var entity in _gone)
        {
            var place = _placed[entity];
            _placed.Remove(entity);
            for (var y = place.Y; y < place.Y + place.H; y++)
                for (var x = place.X; x < place.X + place.W; x++)
                {
                    var (tileX, tileY) = (PathSearch.Floor(x), PathSearch.Floor(y));
                    if (Read(tileX, tileY) == entity)
                        Write(tileX, tileY, 0);
                }
        }
        foreach (var (entity, place) in _arrived)
        {
            _placed[entity] = place;
            for (var y = place.Y; y < place.Y + place.H; y++)
                for (var x = place.X; x < place.X + place.W; x++)
                    Write(PathSearch.Floor(x), PathSearch.Floor(y), entity);
        }
    }

    private uint Read(int x, int y)
    {
        if (_bounds is { } map && map.Contains(x, y))
            return _grid[(long)(y - map.Top) * map.Width + (x - map.Left)];
        return _outside.Count == 0 ? 0 : _outside.GetValueOrDefault(Key(x, y));
    }

    private void Write(int x, int y, uint entity)
    {
        Version++;
        if (_bounds is { } map && map.Contains(x, y))
        {
            _grid[(long)(y - map.Top) * map.Width + (x - map.Left)] = entity;
            return;
        }
        if (entity == 0)
            _outside.Remove(Key(x, y));
        else
            _outside[Key(x, y)] = entity;
    }

    private static long Key(int x, int y) => ((long)y << 32) | (uint)x;

    /// <summary>Основание, каким его заняли: левый верхний угол и размер.</summary>
    private readonly record struct Placed(double X, double Y, double W, double H);
}
