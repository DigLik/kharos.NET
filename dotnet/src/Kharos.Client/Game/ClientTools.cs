using System.Text;
using Kharos.Client.Ui;
using Kharos.Content;
using Kharos.Engine.Terrain;
using Kharos.Engine.Tools;
using Kharos.Engine.World;
using Kharos.Mod.Abi;
using Kharos.Protocol;

namespace Kharos.Client.Game;

/// <summary>
/// Сглаживание мест между тиками (ClientState.prev и pose в script.rs): сдвинутая дельтой сущность едет от того места,
/// где её показывали, к новому за span тиков (хост прислал её с опозданием — столько же и догоняет).
/// </summary>
public sealed class Poses
{
    private readonly Dictionary<uint, Glide> _glides = [];
    private readonly List<uint> _done = [];

    /// <summary>Доля тика с прихода последней дельты, 0..1 (для модов).</summary>
    public double Alpha { get; set; } = 1;

    /// <summary>Время кадра, с: по нему считается, сколько проехано.</summary>
    public double Time { get; set; }

    /// <summary>Сколько сущностей сейчас в пути.</summary>
    public int Gliding => _glides.Count;

    /// <summary>Дельта применена в time: сдвинутым — путь от показанного места к новому длиной span × dt.</summary>
    public void Moved(IReadOnlyList<PoseMove> moves, double time, double dt)
    {
        foreach (var move in moves)
        {
            var (x, y, angle) = (move.X, move.Y, move.Angle);
            // Несколько дельт за кадр: путь — от места перед последней (отстаём не больше чем на тик, как раньше).
            if (_glides.TryGetValue(move.Entity, out var glide) && time > glide.Start && time < glide.Start + glide.Duration)
                (x, y, angle) = glide.At(time, move.X, move.Y, move.Angle);
            _glides[move.Entity] = new Glide(x, y, angle, time, Math.Max(dt * move.Span, 1e-6));
        }
        // Доехавшие — прочь, чтобы словарь не рос.
        if (_glides.Count > 64 && _glides.Count > moves.Count * 2)
        {
            _done.Clear();
            foreach (var (entity, glide) in _glides)
                if (time >= glide.Start + glide.Duration)
                    _done.Add(entity);
            foreach (var entity in _done)
                _glides.Remove(entity);
        }
    }

    /// <summary>Мир сменился снимком: путей нет.</summary>
    public void Forget() => _glides.Clear();

    /// <summary>Место и направление сущности сейчас; нет Position — false.</summary>
    public bool TryGet(GameWorld world, uint entity, out double x, out double y, out double angle)
    {
        var position = world.Store(Builtin.Position);
        var row = position.RowOf(entity);
        if (row < 0)
        {
            (x, y, angle) = (double.NaN, double.NaN, double.NaN);
            return false;
        }
        x = position.Column(0)[row];
        y = position.Column(1)[row];
        var facing = world.Store(Builtin.Facing);
        var at = facing.RowOf(entity);
        angle = at < 0 ? 0 : facing.Column(0)[at];
        if (_glides.TryGetValue(entity, out var glide) && Time < glide.Start + glide.Duration)
            (x, y, angle) = glide.At(Time, x, y, angle);
        return true;
    }

    /// <summary>Путь: откуда, когда начат и сколько длится.</summary>
    private readonly record struct Glide(double X, double Y, double Angle, double Start, double Duration)
    {
        public (double X, double Y, double Angle) At(double time, double x, double y, double angle)
        {
            var t = Math.Clamp((time - Start) / Duration, 0, 1);
            var turn = Euclid(angle - Angle + Math.PI, Math.Tau) - Math.PI;
            return (X + (x - X) * t, Y + (y - Y) * t, Angle + turn * t);
        }
    }

    private static double Euclid(double value, double modulus)
    {
        var rest = value % modulus;
        return rest < 0 ? rest + modulus : rest;
    }
}

/// <summary>
/// Инструменты движка для клиентских модов (kh.tool): общие <see cref="ModTools"/> над миром реплики (Position и
/// Facing — сглаженные) и свои — ClientAbi.Tool* (Each, Pose, Pick, Within, Sees, Explored, Measure, Column).
/// </summary>
public sealed class ClientTools : ModTools
{
    private readonly Replica _replica;
    private readonly Poses _poses;
    private readonly Fonts _fonts;
    private readonly List<uint> _list = [];
    private readonly List<int> _components = [];

    public ClientTools(Replica replica, GameContent content, Func<Land?> land, Poses poses, Fonts fonts)
        : base(new ReplicaWorld(replica, content, land))
    {
        _replica = replica;
        _poses = poses;
        _fonts = fonts;
    }

    /// <summary>Ошибки разбора входа — сюда.</summary>
    public Action<string>? Log { get; set; }

    protected override void Report(int id, string message) => Log?.Invoke($"инструмент {id}: {message}");

    protected override double Number(GameWorld world, uint entity, int component, int field, double stored)
    {
        if (component is not (Builtin.Position or Builtin.Facing) || !_poses.TryGet(world, entity, out var x, out var y, out var angle))
            return stored;
        return component == Builtin.Facing ? angle : field == 0 ? x : field == 1 ? y : stored;
    }

    protected override int Run(int id, ref ToolInput r)
    {
        if (_replica.World is not { } world)
            return ModAbi.ErrorNotLoaded;
        switch (id)
        {
            case ClientAbi.ToolEach:
                ReadComponents(ref r);
                _list.Clear();
                world.Query(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_components), _list);
                _list.Sort();
                WriteList();
                return 0;
            case ClientAbi.ToolPose:
            {
                var n = r.Count(4);
                for (var i = 0; i < n; i++)
                {
                    _poses.TryGet(world, r.U32(), out var x, out var y, out var angle);
                    F64(x);
                    F64(y);
                    F64(angle);
                }
                return 0;
            }
            case ClientAbi.ToolPick:
                U32(Pick(world, r.F64(), r.F64()) ?? 0);
                return 0;
            case ClientAbi.ToolWithin:
            {
                var (x1, y1, x2, y2) = (r.F64(), r.F64(), r.F64(), r.F64());
                ReadComponents(ref r);
                _components.Add(Builtin.Position);
                _list.Clear();
                world.Query(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_components), _list);
                var (left, right, top, bottom) = (Math.Min(x1, x2), Math.Max(x1, x2), Math.Min(y1, y2), Math.Max(y1, y2));
                _list.RemoveAll(entity => !_poses.TryGet(world, entity, out var x, out var y, out _) || x < left || x > right || y < top || y > bottom);
                _list.Sort();
                WriteList();
                return 0;
            }
            case ClientAbi.ToolSees or ClientAbi.ToolExplored:
            {
                var n = r.Count(16);
                var into = Room(n);
                for (var i = 0; i < n; i++)
                {
                    var (x, y) = ((int)Math.Floor(r.F64()), (int)Math.Floor(r.F64()));
                    into[i] = (id == ClientAbi.ToolSees ? _replica.Sees(x, y) : _replica.Explored(x, y)) ? (byte)1 : (byte)0;
                }
                return 0;
            }
            case ClientAbi.ToolMeasure:
            {
                var font = r.Byte();
                var text = Encoding.UTF8.GetString(r.Rest());
                F32(font <= (byte)FontName.Logo ? _fonts.Width((FontName)font, text) : 0);
                return 0;
            }
            case ClientAbi.ToolColumn:
            {
                var component = Component(r.U32());
                var field = (int)r.U32();
                var def = world.Schema[component];
                if ((uint)field >= (uint)def.Fields.Count || !def.Fields[field].Columnar)
                    throw new ArgumentException($"{def.Name}: нет поля-столбца №{field}");
                var n = r.Count(4);
                for (var i = 0; i < n; i++)
                {
                    var entity = r.U32();
                    F64(world.TryNumber(entity, component, field, out var value) ? Number(world, entity, component, field, value) : double.NaN);
                }
                return 0;
            }
            case ModAbi.ToolStorage:
                return ModAbi.ErrorUnknownId;
            default:
                return base.Run(id, ref r);
        }
    }

    /// <summary>
    /// Сущность под точкой мира (pick в script.rs): юниты — по радиусу Unit (+0,15), здания — по прямоугольнику
    /// Building; чужое вне обзора не берётся; из попавших — ближайшая.
    /// </summary>
    public uint? Pick(GameWorld world, double x, double y)
    {
        var schema = world.Schema;
        var unit = schema.Find("Unit");
        var building = schema.Find("Building");
        uint? best = null;
        var bestDistance = double.MaxValue;
        foreach (var entity in world.Store(Builtin.Position).Entities)
        {
            if (!_poses.TryGet(world, entity, out var ex, out var ey, out _))
                continue;
            var foreign = world.TryNumber(entity, Builtin.Owner, 0, out var owner) && (uint)owner != _replica.Player;
            if (foreign && !_replica.Sees((int)Math.Floor(ex), (int)Math.Floor(ey)))
                continue;
            var (dx, dy) = (x - ex, y - ey);
            bool hit;
            if (unit is { } u && world.TryNumber(entity, u, 0, out var radius))
                hit = dx * dx + dy * dy <= (radius + 0.15) * (radius + 0.15);
            else if (building is { } b && world.Has(entity, b))
            {
                var def = schema[b];
                var w = def.Field("width") is { } width && world.TryNumber(entity, b, width.Index, out var wv) ? wv : 1;
                var h = def.Field("height") is { } height && world.TryNumber(entity, b, height.Index, out var hv) ? hv : 1;
                hit = Math.Abs(dx) <= w / 2 && Math.Abs(dy) <= h / 2;
            }
            else
                hit = false;
            var distance = dx * dx + dy * dy;
            if (hit && distance < bestDistance)
                (best, bestDistance) = (entity, distance);
        }
        return best;
    }

    private void ReadComponents(ref ToolInput r)
    {
        _components.Clear();
        var k = r.Count(4);
        for (var i = 0; i < k; i++)
            _components.Add(Component(r.U32()));
    }

    private void WriteList()
    {
        U32((uint)_list.Count);
        foreach (var entity in _list)
            U32(entity);
    }

    /// <summary>Мир реплики для инструментов.</summary>
    private sealed class ReplicaWorld(Replica replica, GameContent content, Func<Land?> land) : IToolWorld
    {
        public GameWorld? World => replica.World;
        public Land? Land => land();
        public Kharos.Engine.Layers.LayerSet? Layers => replica.Layers;
        public ulong Tick => replica.Tick;
        public int ProtoId(string path) => content.FindProto(path)?.Id ?? -1;
        public string? ProtoPath(int id) => (uint)id < (uint)content.Protos.Count ? content.Protos[id].Path : null;
    }
}
