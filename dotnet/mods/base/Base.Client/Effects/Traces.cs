using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Колея и отпечатки ног — перенос effects/traces.luau (decalsPass.ts и createWalkers из sim-ts/traces.ts). Наземный
/// юнит оставляет точку пути, пройдя Step тайла или повернув на Turn; соседняя точка, если она рядом, соединяется с
/// новой колеёй под каждым колесом (TraceSpec.Sides) или отпечатками ступней через полшага. Колея — тень вдавленного
/// грунта: на скале почти не видна, на болоте и горах её нет; живёт Life секунд и с четверти срока растворяется
/// пиксель за пикселем.
/// </summary>
internal sealed class Traces(BaseState state)
{
    private const double Step = 0.5;
    private const double Turn = 0.35;

    /// <summary>Точки пути дальше этого друг от друга — не шаг, а скачок: колея между ними не тянется.</summary>
    private const double Link = 1.5;

    private const float Life = 90;

    /// <summary>Точек колеи на тайл разом: в толпе колея не густеет бесконечно (DENSITY.track).</summary>
    private const int PerTile = 2;

    /// <summary>Через сколько тайлов пути пехотинец оставляет отпечаток ступни и какой он длины.</summary>
    private const double StepLength = 0.3;

    private const double Footprint = 0.1;

    private static readonly (float R, float G, float B) Color = (0.16f, 0.11f, 0.06f);

    /// <summary>Непрозрачность по местности: болото, песок, скала, гора.</summary>
    private static readonly float[] Opacity = [0, 0.4f, 0.18f, 0];

    /// <summary>Какая доля пикселей колеи закрашена: колея зернистая, как продавленный песок.</summary>
    private const float Density = 0.8f;

    private sealed class Point(double x, double y, double facing)
    {
        public double X { get; } = x;
        public double Y { get; } = y;
        public double Facing { get; } = facing;
        public int Foot { get; set; }
    }

    // Где каждый юнит оставил последнюю точку и какая точка легла в колею последней.
    private readonly Dictionary<uint, Point> _walkers = [];
    private readonly Dictionary<uint, Point> _paths = [];

    // Точек колеи по тайлам и когда они уйдут — по порядку.
    private readonly Dictionary<long, int> _counts = [];
    private readonly Queue<(double Time, long Key)> _leaving = [];

    private Dictionary<int, TraceSpec>? _traces;
    private readonly HashSet<uint> _seen = [];
    private readonly List<uint> _gone = [];

    private static double AngleBetween(double a, double b) => Math.Atan2(Math.Sin(a - b), Math.Cos(a - b));

    private static long TileKey(double x, double y) => (long)(Math.Floor(x) + 32768) * 65536 + (long)Math.Floor(y) + 32768;

    private static float OpacityAt(ClientContext c, double x, double y) => c.Terrain(x, y, out var info) ? Opacity[info.Terrain & 3] : Opacity[1];

    private static void Lay(ClientContext c, double fromX, double fromY, double toX, double toY, float width, float opacity) =>
        c.Decal(fromX, fromY, toX, toY, width, Color, opacity, Density, Life);

    /// <summary>Колея или шаги от точки пути from до to одного юнита.</summary>
    private static void Walk(ClientContext c, Point from, Point to, TraceSpec trace)
    {
        var width = (float)(trace.Width / 16);
        if (!trace.Legs)
        {
            foreach (var side in trace.Sides)
            {
                double fromX = from.X - Math.Sin(from.Facing) / 16 * side, fromY = from.Y + Math.Cos(from.Facing) / 16 * side;
                double toX = to.X - Math.Sin(to.Facing) / 16 * side, toY = to.Y + Math.Cos(to.Facing) / 16 * side;
                Lay(c, fromX, fromY, toX, toY, width, OpacityAt(c, toX, toY));
            }
            return;
        }
        // Ноги шагают по очереди: отпечатки ступней вдоль хода, через полшага, то одной, то другой.
        var length = Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y));
        double alongX = Math.Cos(to.Facing) * Footprint / 2, alongY = Math.Sin(to.Facing) * Footprint / 2;
        double acrossX = -Math.Sin(to.Facing) / 16, acrossY = Math.Cos(to.Facing) / 16;
        var step = 0;
        while (step * (StepLength / 2) < length)
        {
            var share = step * (StepLength / 2) / length;
            var side = trace.Sides[(from.Foot + step) % trace.Sides.Length];
            var x = from.X + (to.X - from.X) * share + acrossX * side;
            var y = from.Y + (to.Y - from.Y) * share + acrossY * side;
            Lay(c, x - alongX, y - alongY, x + alongX, y + alongY, width, OpacityAt(c, x, y));
            step++;
            to.Foot = from.Foot + step;
        }
    }

    /// <summary>Занимает место под точку колеи; false — тайл полон.</summary>
    private bool Take(double x, double y, double now)
    {
        var key = TileKey(x, y);
        _counts.TryGetValue(key, out var count);
        if (count >= PerTile)
            return false;
        _counts[key] = count + 1;
        _leaving.Enqueue((now + Life, key));
        return true;
    }

    private void Release(double now)
    {
        while (_leaving.TryPeek(out var next) && next.Time <= now)
        {
            _leaving.Dequeue();
            var count = _counts.GetValueOrDefault(next.Key, 1) - 1;
            if (count > 0)
                _counts[next.Key] = count;
            else
                _counts.Remove(next.Key);
        }
    }

    /// <summary>Следы по номерам видов: у каких видов колея, какая.</summary>
    private Dictionary<int, TraceSpec> Resolve(ClientContext c)
    {
        var traces = new Dictionary<int, TraceSpec>();
        foreach (var (path, trace) in UnitArt.TraceByPath())
            if (c.ProtoId(path) is var id and >= 0)
                traces[id] = trace;
        return traces;
    }

    public void Update(ClientContext c)
    {
        var now = c.Clock;
        Release(now);
        _traces ??= Resolve(c);
        _seen.Clear();
        var all = c.Each(state.P.Position, state.P.Proto);
        var protos = new double[all.Length];
        c.Column(all, state.P.Proto, "id", protos);
        var walkers = new List<uint>();
        var specs = new List<TraceSpec>();
        for (var i = 0; i < all.Length; i++)
            if (_traces.TryGetValue((int)protos[i], out var spec))
            {
                walkers.Add(all[i]);
                specs.Add(spec);
            }
        var poses = new (double X, double Y, double Angle)[walkers.Count];
        c.Poses([.. walkers], poses);
        for (var i = 0; i < walkers.Count; i++)
        {
            var entity = walkers[i];
            var trace = specs[i];
            _seen.Add(entity);
            var (x, y, angle) = poses[i];
            if (_walkers.TryGetValue(entity, out var last))
            {
                var moved = Math.Sqrt((x - last.X) * (x - last.X) + (y - last.Y) * (y - last.Y));
                if (moved < 1e-3 || (moved < Step && Math.Abs(AngleBetween(angle, last.Facing)) < Turn))
                    continue;
            }
            var point = new Point(x, y, angle);
            _walkers[entity] = point;
            // Первая точка — тоже точка: от неё колея и потянется. Скачок узнаётся по расстоянию и не соединяется.
            if (!Take(point.X, point.Y, now))
                continue;
            if (_paths.TryGetValue(entity, out var before) && Math.Sqrt((point.X - before.X) * (point.X - before.X) + (point.Y - before.Y) * (point.Y - before.Y)) <= Link)
                Walk(c, before, point, trace);
            _paths[entity] = point;
        }
        _gone.Clear();
        foreach (var entity in _walkers.Keys)
            if (!_seen.Contains(entity))
                _gone.Add(entity);
        foreach (var entity in _gone)
        {
            _walkers.Remove(entity);
            _paths.Remove(entity);
        }
    }
}
