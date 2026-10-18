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
/// Место и направление сущностей тиком раньше — для сглаживания между тиками (ClientState.prev и pose в script.rs).
/// </summary>
public sealed class Poses
{
    private readonly Dictionary<uint, (double X, double Y, double Angle)> _previous = [];

    /// <summary>Доля тика с его прихода, 0..1.</summary>
    public double Alpha { get; set; } = 1;

    /// <summary>Запоминает места перед применением тика.</summary>
    public void Remember(GameWorld world)
    {
        _previous.Clear();
        var position = world.Store(Builtin.Position);
        var facing = world.Store(Builtin.Facing);
        var entities = position.Entities;
        ReadOnlySpan<double> xs = position.Column(0), ys = position.Column(1), angles = facing.Column(0);
        for (var row = 0; row < entities.Length; row++)
        {
            var entity = entities[row];
            var at = facing.RowOf(entity);
            _previous[entity] = (xs[row], ys[row], at < 0 ? 0 : angles[at]);
        }
    }

    /// <summary>Мир сменился снимком: прежних мест нет.</summary>
    public void Forget() => _previous.Clear();

    /// <summary>Место и направление сущности сейчас — между прошлым и этим тиком; нет Position — false.</summary>
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
        if (!_previous.TryGetValue(entity, out var previous))
            return true;
        var t = Alpha;
        var turn = Euclid(angle - previous.Angle + Math.PI, Math.Tau) - Math.PI;
        (x, y, angle) = (previous.X + (x - previous.X) * t, previous.Y + (y - previous.Y) * t, previous.Angle + turn * t);
        return true;
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
        public int ProtoId(string path) => content.FindProto(path)?.Id ?? -1;
        public string? ProtoPath(int id) => (uint)id < (uint)content.Protos.Count ? content.Protos[id].Path : null;
    }
}
