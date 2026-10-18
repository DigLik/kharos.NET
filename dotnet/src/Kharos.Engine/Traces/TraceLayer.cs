using Kharos.Engine.Layers;
using Kharos.Engine.Numerics;
using Kharos.Engine.World;

namespace Kharos.Engine.Traces;

/// <summary>
/// Следы на земле (traces.ts, crates/sim/src/traces.rs, docs/layers.md): колея и шаги, гарь, прожоги лучей, остовы —
/// предметы слоя «traces» по ячейкам 16×16. Это данные симуляции, а не картинка: по ним враг видит, кто здесь был.
/// Живут свой срок и не сохраняются.
/// <para>
/// Колею оставляет система движка <c>engine.traces</c> (<see cref="Update"/>) по ходу наземных юнитов — сущностей с
/// <c>Mover</c>, <c>Position</c> и <c>Owner</c>, чья ходовая не летает. Следы боя кладёт мод командой Item
/// (<c>PushItem</c> в SDK): правило слоя (<see cref="LayerSet.Intercept"/>) ставит каждому предмету номер и тик, а полный
/// тайл не пускает. Остальные поля (kind, entity, type, facing, player, size) — от положившего; поля id и tick, что он
/// дал, игнорируются.
/// </para>
/// </summary>
public sealed class TraceLayer
{
    public const string LayerName = "traces";

    /// <summary>Виды следов по порядку — как TRACE_LIFE в traces.ts.</summary>
    public const int Track = 0, Scar = 1, Burn = 2, Wreck = 3;

    /// <summary>Поля предмета по порядку layers.json.</summary>
    public const int Id = 0, Tick = 1, Kind = 2, Entity = 3, Type = 4, Facing = 5, Player = 6, Size = 7;

    private static readonly string[] FieldNames = ["id", "tick", "kind", "entity", "type", "facing", "player", "size"];

    /// <summary>Сколько секунд живёт след каждого вида.</summary>
    public static readonly double[] Life = [90, 240, 240, 600];

    /// <summary>Через сколько тайлов пути юнит оставляет новую точку следа и на сколько радиан должен повернуть, чтобы оставить её раньше.</summary>
    public const double TrackStep = 0.5, TrackTurn = 0.35;

    /// <summary>Как часто убираются истёкшие следы, в секундах.</summary>
    public const double ExpireEvery = 1;

    /// <summary>Сколько живых следов одного вида держит тайл; 0 — предела нет (у остовов).</summary>
    public static readonly int[] Density = [2, 2, 2, 0];

    private readonly LayerSet _layers;
    private readonly int _layer;
    private readonly Func<ulong> _tick;
    private readonly Func<string, bool> _flies;
    private readonly Dictionary<uint, (double X, double Y, double Facing)> _walkers = [];
    private readonly Dictionary<(int X, int Y, int Kind), int> _density = [];
    private readonly HashSet<uint> _seen = [];
    private readonly List<uint> _units = [];
    private readonly Dictionary<double, bool> _airborne = [];
    private uint _next = 1;
    private double _sinceExpire;

    private TraceLayer(LayerSet layers, int layer, Func<ulong> tick, Func<string, bool> flies)
    {
        _layers = layers;
        _layer = layer;
        _tick = tick;
        _flies = flies;
    }

    /// <summary>
    /// Следы партии: ставит правило на слой «traces». Слоя нет или у него не те поля — null (следов у партии нет).
    /// tick — номер тика; flies — летает ли ходовая (запись moves.json).
    /// </summary>
    public static TraceLayer? Create(LayerSet layers, Func<ulong> tick, Func<string, bool> flies)
    {
        if (layers.Find(LayerName) is not { } id)
            return null;
        var spec = layers.Spec(id);
        if (spec.Scope != LayerScope.World || spec.IsGrid || !spec.Fields.Select(field => field.Name).SequenceEqual(FieldNames))
            return null;
        var traces = new TraceLayer(layers, id, tick, flies);
        layers.Intercept(id, traces.Accept);
        return traces;
    }

    /// <summary>Номер слоя следов.</summary>
    public int Layer => _layer;

    /// <summary>Номер, который получит следующий след.</summary>
    public uint NextId => _next;

    /// <summary>Правило слоя: номер и тик, предел густоты; неизвестный вид не пускается.</summary>
    private LayerItem? Accept(LayerOwner owner, LayerItem item)
    {
        var raw = item.Fields[Kind];
        if (!owner.IsWorld || !(raw >= 0 && raw < Life.Length))
            return null;
        var kind = (int)raw;
        if (Density[kind] > 0)
        {
            var key = DensityKey(kind, item.X, item.Y);
            _density.TryGetValue(key, out var count);
            if (count >= Density[kind])
                return null;
            _density[key] = count + 1;
        }
        var fields = (double[])item.Fields.Clone();
        fields[Id] = _next++;
        fields[Tick] = _tick();
        return item with { Fields = fields };
    }

    private static (int X, int Y, int Kind) DensityKey(int kind, double x, double y) => ((int)Math.Floor(x), (int)Math.Floor(y), kind);

    /// <summary>
    /// Система <c>engine.traces</c>, конец тика: точки пути наземных юнитов и истёкшие следы. tick и dt — тика, что идёт.
    /// </summary>
    public void Update(GameWorld world, ulong tick, double dt)
    {
        Walk(world);
        _sinceExpire += dt;
        if (_sinceExpire < ExpireEvery)
            return;
        _sinceExpire = 0;
        var now = (double)tick;
        foreach (var cell in _layers.Cells(_layer, LayerOwner.World))
            _layers.Retain(_layer, LayerOwner.World, cell, item =>
            {
                var kind = (int)item.Fields[Kind];
                if ((now - item.Fields[Tick]) * dt < Life[kind])
                    return true;
                Release(kind, item.X, item.Y);
                return false;
            });
    }

    private void Release(int kind, double x, double y)
    {
        if (Density[kind] == 0)
            return;
        var key = DensityKey(kind, x, y);
        if (_density.TryGetValue(key, out var count) && count > 1)
            _density[key] = count - 1;
        else
            _density.Remove(key);
    }

    /// <summary>Колея: наземный юнит оставляет точку, пройдя <see cref="TrackStep"/> или повернув на <see cref="TrackTurn"/>.</summary>
    private void Walk(GameWorld world)
    {
        _units.Clear();
        world.Query([Builtin.Mover, Builtin.Position, Builtin.Owner], _units);
        _units.Sort();
        _seen.Clear();
        var strings = world.Strings;
        foreach (var entity in _units)
        {
            var chassis = world.Number(entity, Builtin.Mover, 0);
            if (!_airborne.TryGetValue(chassis, out var air))
                _airborne[chassis] = air = chassis > 0 && _flies(strings.Text((uint)chassis));
            if (air)
                continue;
            _seen.Add(entity);
            var (x, y) = (world.Number(entity, Builtin.Position, 0), world.Number(entity, Builtin.Position, 1));
            var facing = world.TryNumber(entity, Builtin.Facing, 0, out var angle) ? angle : 0;
            if (_walkers.TryGetValue(entity, out var last))
            {
                var moved = DetMath.Hypot(x - last.X, y - last.Y);
                if (moved < TrackStep && Math.Abs(AngleBetween(facing, last.Facing)) < TrackTurn)
                    continue;
                if (moved < 1e-3)
                    continue;
            }
            _walkers[entity] = (x, y, facing);
            // Первая точка — тоже точка: от неё колея и потянется.
            var type = world.TryNumber(entity, Builtin.Proto, 0, out var proto) ? proto : 0;
            var player = world.Number(entity, Builtin.Owner, 0);
            _layers.Push(_layer, LayerOwner.World, new LayerItem(x, y, [0, 0, Track, entity, type, facing, player, 0]));
        }
        if (_walkers.Count > _seen.Count)
            foreach (var entity in _walkers.Keys.Where(entity => !_seen.Contains(entity)).ToList())
                _walkers.Remove(entity);
    }

    private static double AngleBetween(double a, double b) => DetMath.Atan2(DetMath.Sin(a - b), DetMath.Cos(a - b));
}
