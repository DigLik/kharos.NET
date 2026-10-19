using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Kharos.Client.Ui;
using Kharos.Content;
using Kharos.Engine.Movement;
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
/// Facing — сглаженные; ход и занятость — <see cref="ModTools.Navigation"/> по реплике, как у хоста) и свои —
/// ClientAbi.Tool* (Each, Pose, Pick, Within, Sees, Explored, Measure, Column; ABI 2 — Text, Prefs, Roster, Settings).
/// </summary>
public sealed class ClientTools : ModTools
{
    private readonly Replica _replica;
    private readonly GameContent _content;
    private readonly Func<Land?> _land;
    private readonly Poses _poses;
    private readonly Fonts _fonts;
    private readonly List<uint> _list = [];
    private readonly List<int> _components = [];
    private ChassisTable? _chassis;

    public ClientTools(Replica replica, GameContent content, Func<Land?> land, Poses poses, Fonts fonts)
        : base(new ReplicaWorld(replica, content, land))
    {
        _replica = replica;
        _content = content;
        _land = land;
        _poses = poses;
        _fonts = fonts;
    }

    /// <summary>Ошибки разбора входа — сюда.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Хранилище настроек модов (Prefs, ABI 2): ключи <see cref="PrefsKey"/>.</summary>
    public IStorage Storage { get; set; } = new MemoryStorage();

    /// <summary>Чей вызов сейчас (имя мода): настройки Prefs у каждого мода свои. null — настроек нет.</summary>
    public string? Mod { get; set; }

    /// <summary>Игроки партии для Roster (ростер протокола).</summary>
    public Func<IReadOnlyList<PlayerInfo>> Players { get; set; } = () => [];

    /// <summary>Режим администратора у этого подключения (сообщение Commands хоста).</summary>
    public Func<bool> Admin { get; set; } = () => false;

    /// <summary>Право редактора у этого подключения. TODO(Р): протокол его не шлёт — до оболочки редактора false.</summary>
    public Func<bool> Editor { get; set; } = () => false;

    /// <summary>Ключ настройки key мода mod в хранилище.</summary>
    public static string PrefsKey(string mod, string key) => $"prefs/{mod}/{key}";

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
            case ClientAbi.ToolText:
            {
                var key = Str(ref r);
                var n = BinaryPrimitives.ReadUInt16LittleEndian(r.Bytes(2));
                for (var i = 0; i < n; i++)
                    Str(ref r);
                // TODO(Д3): словари всех модов (Kharos.I18n) на языке игрока с подстановкой аргументов; пока — сам ключ.
                WriteStr(key);
                return 0;
            }
            case ClientAbi.ToolPrefs:
            {
                var key = Str(ref r);
                if (Mod is { } mod && Storage.Get(PrefsKey(mod, key)) is { } bytes)
                    bytes.CopyTo(Room(bytes.Length));
                return 0;
            }
            case ClientAbi.ToolRoster:
            {
                var players = Players();
                var admin = Admin();
                U32((uint)players.Count);
                foreach (var player in players)
                {
                    U32(player.Player);
                    WriteStr(player.Name);
                    var into = Room(2);
                    into[0] = player.Online ? (byte)1 : (byte)0;
                    // Протокол знает только свой режим администратора: у чужих — 0.
                    into[1] = admin && player.Player == _replica.Player ? (byte)1 : (byte)0;
                }
                return 0;
            }
            case ClientAbi.ToolSettings:
                WriteSettings();
                return 0;
            case ModAbi.ToolStorage:
                return ModAbi.ErrorUnknownId;
            default:
                EnsureNavigation(world);
                return base.Run(id, ref r);
        }
    }

    /// <summary>
    /// Сущность под точкой мира (pick в script.rs): с телом — по радиусу Body (+0,15), с основанием — по прямоугольнику
    /// Footprint от левого верхнего тайла (место здания — он); чужое вне обзора не берётся; из попавших — ближайшая.
    /// Турели (Mount) не берутся: у них нет ни тела, ни основания. Моды без тела и основания движка (hello) могут назвать
    /// радиус полем radius компонента Unit, а размер — width и height компонента Building (место — середина).
    /// </summary>
    public uint? Pick(GameWorld world, double x, double y)
    {
        var schema = world.Schema;
        (int Component, int Field)? unit = schema.Find("Unit") is { } u && schema[u].Field("radius") is { } radiusField ? (u, radiusField.Index) : null;
        (int Component, int Width, int Height)? building = schema.Find("Building") is { } b && schema[b].Field("width") is { } widthField && schema[b].Field("height") is { } heightField ? (b, widthField.Index, heightField.Index) : null;
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
            if (world.TryNumber(entity, Builtin.Footprint, 0, out var w))
            {
                var h = world.TryNumber(entity, Builtin.Footprint, 1, out var hv) ? hv : 1;
                (dx, dy) = (x - (ex + w / 2), y - (ey + h / 2));
                hit = Math.Abs(dx) <= w / 2 && Math.Abs(dy) <= h / 2;
            }
            else if (world.TryNumber(entity, Builtin.Body, 0, out var radius))
                hit = dx * dx + dy * dy <= (radius + 0.15) * (radius + 0.15);
            else if (unit is { } legacy && world.TryNumber(entity, legacy.Component, legacy.Field, out var legacyRadius))
                hit = dx * dx + dy * dy <= (legacyRadius + 0.15) * (legacyRadius + 0.15);
            else if (building is { } shape && world.Has(entity, shape.Component))
            {
                var width = world.TryNumber(entity, shape.Component, shape.Width, out var wv) ? wv : 1;
                var height = world.TryNumber(entity, shape.Component, shape.Height, out var hv) ? hv : 1;
                hit = Math.Abs(dx) <= width / 2 && Math.Abs(dy) <= height / 2;
            }
            else
                hit = false;
            var distance = dx * dx + dy * dy;
            if (hit && distance < bestDistance)
                (best, bestDistance) = (entity, distance);
        }
        return best;
    }

    /// <summary>
    /// Ход и занятость над миром реплики (16 Tiles, 17 Occupant, 21 Walkable и др.; на них стоят Placing и canPlace из
    /// Base.Shared/Rules) — те же, что у хоста: ходовые из data/moves.json, земля, слой paving, границы карты. Мир,
    /// земля или слои реплики сменились — строятся заново.
    /// </summary>
    private void EnsureNavigation(GameWorld world)
    {
        var land = _land();
        if (Navigation is { } navigation && navigation.World == world && navigation.Land == land && navigation.Layers == _replica.Layers)
            return;
        _chassis ??= _content.Data("moves") is { } moves ? ChassisTable.Parse(moves) : ChassisTable.Empty;
        var bounds = _replica.Welcome?.Map is { } map ? new TileBounds(map.Left, map.Top, map.Right, map.Bottom) : (TileBounds?)null;
        Navigation = new Navigation(world, land, _replica.Layers, bounds, _chassis);
    }

    /// <summary>
    /// Settings: запись партии из data/games.json, карта, туман, права. TODO(Д4): настройки хоста поверх записи
    /// (HostOptions.Settings), час и сутки карты — протокол их клиенту пока не шлёт.
    /// </summary>
    private void WriteSettings()
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(buffer))
        {
            var welcome = _replica.Welcome;
            json.WriteStartObject();
            json.WriteString("game", welcome?.Game ?? "");
            json.WritePropertyName("settings");
            if (welcome is not null && _content.Data("games") is { ValueKind: JsonValueKind.Object } games && games.TryGetProperty(welcome.Game, out var entry))
                entry.WriteTo(json);
            else
                json.WriteNullValue();
            json.WritePropertyName("map");
            if (welcome?.Map is { } map)
            {
                json.WriteStartObject();
                json.WriteNumber("left", map.Left);
                json.WriteNumber("top", map.Top);
                json.WriteNumber("right", map.Right);
                json.WriteNumber("bottom", map.Bottom);
                json.WriteEndObject();
            }
            else
                json.WriteNullValue();
            json.WriteBoolean("fog", welcome?.Fog ?? false);
            json.WriteStartObject("rights");
            json.WriteBoolean("admin", Admin());
            json.WriteBoolean("editor", Editor());
            json.WriteEndObject();
            json.WriteEndObject();
        }
        buffer.WrittenSpan.CopyTo(Room(buffer.WrittenCount));
    }

    /// <summary>Строка str входа: u16 длина и UTF-8.</summary>
    private static string Str(ref ToolInput r) => Encoding.UTF8.GetString(r.Bytes(BinaryPrimitives.ReadUInt16LittleEndian(r.Bytes(2))));

    /// <summary>Строка str ответа: u16 длина и UTF-8 (длиннее 65535 байт — обрезается по символам).</summary>
    private void WriteStr(string text)
    {
        var count = Encoding.UTF8.GetByteCount(text);
        while (count > ushort.MaxValue)
        {
            text = text[..(text.Length * 3 / 4)];
            count = Encoding.UTF8.GetByteCount(text);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(Room(2), (ushort)count);
        Encoding.UTF8.GetBytes(text, Room(count));
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
