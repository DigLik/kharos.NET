using System.Buffers.Binary;
using System.Text.Json;
using Kharos.Mod.Sdk;
using MemoryPack;

namespace Hello;

/// <summary>
/// Пробный мод (docs/dotnet.md, Н2–Н3): ходовая над Position, Facing, Drive и теснота инструментом движка Separate;
/// юниты — вида units/rover (data/units.json);
/// компоненты Drive, Unit, Follow и Anchor объявлены в mod.json. События: start, player_joined (три юнита у дома
/// игрока), hello.arrived (сигнал клиентам). Приказы: hello.spawn, hello.move, hello.probe (ответ инструментов
/// сигналом); hello.spin, hello.hog, hello.fail, hello.bad — для тестов пределов и сбоев. Проверки Н5 (кадр с
/// побочными таблицами, Without, RowOf, Stage, права, адресные сигналы, запросы): система hello.follow (за ведущим —
/// таблица Position+Unit), hello.wrap (только пишущая таблица), приказы hello.follow, hello.anchor, hello.rights,
/// запрос hello.count. Д3: hello.layer, hello.item, hello.land (команды Layer, Item, Land) и hello.read (инструменты Layer,
/// Items, Peak — ответ сигналом hello.read); слои стенда объявляет мод-стенд тестов (tests/Kharos.Host.Tests/LayerLab.cs).
/// </summary>
public sealed class HelloMod : Mod
{
    /// <summary>Расстояние «доехал», тайлы (data/hello.json).</summary>
    public double Arrival { get; private set; } = 0.5;

    /// <summary>Скорость, поворот, корпус и обзор новых юнитов.</summary>
    public double Speed { get; private set; } = 4;
    public double Turn { get; private set; } = 3;
    public double Radius { get; private set; } = 0.5;
    public double Sight { get; private set; } = 6;

    /// <summary>Доля перекрытия корпусов, снимаемая за тик.</summary>
    public double Crowd { get; private set; } = 0.5;

    /// <summary>Сколько юнитов у вошедшего игрока.</summary>
    public int Start { get; private set; } = 3;

    private ComponentRef _position = null!;
    private ComponentRef _facing = null!;
    private ComponentRef _owner = null!;
    private ComponentRef _drive = null!;
    private ComponentRef _unit = null!;
    private ComponentRef _sight = null!;
    private ComponentRef _proto = null!;
    private ComponentRef _follow = null!;
    private ComponentRef _anchor = null!;
    private int? _rover;

    /// <summary>Слои партии, которых ждут приказы Д3 (data/layers.json мода-стенда в тестах): по имени.</summary>
    private readonly Dictionary<string, LayerRef> _layers = [];

    public override void Setup(ModBuilder mod)
    {
        mod.System("hello.drive", new DriveSystem(this))
            .Query("Position", "Facing", "Drive")
            .Write("Position", "Facing", "Drive");
        mod.System("hello.crowd", new CrowdSystem(this))
            .Query("Position", "Unit")
            .Write("Position")
            .After("drive");
        // Н5: за ведущим — его место из побочной таблицы; закреплённые (Anchor) не идут.
        mod.System("hello.follow", new FollowSystem(this))
            .Query("Drive", "Follow")
            .Without("Anchor")
            .Write("Drive")
            .Table("Position", "Unit")
            .Before("drive");
        // Н5: система без кадра — одна пишущая таблица: юниты не уходят за край карты.
        mod.System("hello.wrap", new WrapSystem())
            .WriteTable("Position", "Unit")
            .After("crowd");
        mod.On("hello.arrived", OnArrived);
        mod.On("player_joined", OnJoined);
        mod.Order("hello.spawn", OnSpawn);
        mod.Order("hello.move", OnMove);
        mod.Order("hello.probe", OnProbe);
        mod.Order("hello.spin", OnSpin);
        mod.Order("hello.hog", OnHog);
        mod.Order("hello.fail", _ => throw new InvalidOperationException("приказ отказан"));
        // Неверный буфер команд: сущности 0 не бывает — движок отвергнет буфер и засчитает сбой.
        mod.Order("hello.bad", ctx => ctx.Commands.Destroy(0));
        mod.Order("hello.follow", OnFollow);
        mod.Order("hello.anchor", OnAnchor);
        // Права приказа — сигналом только отдавшему.
        mod.Order("hello.rights", ctx =>
        {
            Span<byte> payload = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)ctx.Rights);
            ctx.Commands.SignalTo("hello.rights", payload, [ctx.Player]);
        });
        mod.Query("hello.count", OnCount);
        // Д3: слои, предметы и земля командами и чтение их инструментами.
        mod.Order("hello.layer", OnLayer);
        mod.Order("hello.item", OnItem);
        mod.Order("hello.land", OnLand);
        mod.Order("hello.read", OnRead);
    }

    public override void Load(LoadContext ctx)
    {
        if (ctx.TryData("hello", out var json))
        {
            var reader = new Utf8JsonReader(json);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "arrival": Arrival = reader.GetDouble(); break;
                    case "speed": Speed = reader.GetDouble(); break;
                    case "turn": Turn = reader.GetDouble(); break;
                    case "radius": Radius = reader.GetDouble(); break;
                    case "sight": Sight = reader.GetDouble(); break;
                    case "crowd": Crowd = reader.GetDouble(); break;
                    case "start": Start = reader.GetInt32(); break;
                }
            }
        }
        _position = ctx.Component("Position");
        _facing = ctx.Component("Facing");
        _owner = ctx.Component("Owner");
        _drive = ctx.Component("Drive");
        _unit = ctx.Component("Unit");
        _sight = ctx.Component("Sight");
        _proto = ctx.Component("Proto");
        _follow = ctx.Component("Follow");
        _anchor = ctx.Component("Anchor");
        foreach (var name in (string[])["scorch", "marks", "notes", "deposits", "paving", "traces", "land"])
            if (ctx.Layer(name) is { } layer)
                _layers[name] = layer;
    }

    private LayerRef LayerNamed(string name) => _layers.TryGetValue(name, out var layer) ? layer : throw new ArgumentException($"нет слоя {name}");

    /// <summary>Канал слоя-сетки в прямоугольнике (команда Layer).</summary>
    private void OnLayer(OrderContext ctx)
    {
        var order = ctx.Read<LayerOrder>() ?? throw new ArgumentException("hello.layer без payload");
        ctx.Commands.Layer(LayerNamed(order.Layer), order.Owner, order.Channel, order.X, order.Y, order.W, order.H, order.Value);
    }

    /// <summary>Предмет слоя: положить или убрать (команда Item).</summary>
    private void OnItem(OrderContext ctx)
    {
        var order = ctx.Read<ItemOrder>() ?? throw new ArgumentException("hello.item без payload");
        var layer = LayerNamed(order.Layer);
        if (order.Delete)
            ctx.Commands.DeleteItems(layer, order.Owner, order.X, order.Y);
        else
            ctx.Commands.PushItem(layer, order.Owner, order.X, order.Y, order.Fields);
    }

    /// <summary>Правка земли: тайл или вершина горы (команда Land).</summary>
    private static void OnLand(OrderContext ctx)
    {
        var order = ctx.Read<LandOrder>() ?? throw new ArgumentException("hello.land без payload");
        if (order.Peak)
            ctx.Commands.LandPeak(order.X, order.Y, order.Biome, order.PeakX, order.PeakY, order.Radius);
        else
            ctx.Commands.LandTile(order.X, order.Y, new LandEdit(order.Terrain, order.Biome, order.Tier, order.Cliff, order.Dunes));
    }

    /// <summary>Инструменты Layer, Items и Peak — ответ сигналом hello.read хозяину.</summary>
    private void OnRead(OrderContext ctx)
    {
        var order = ctx.Read<ReadOrder>() ?? throw new ArgumentException("hello.read без payload");
        var report = new ReadReport();
        if (order.Layer.Length > 0)
        {
            report.Values = new double[order.W * order.H];
            Tools.Layer(LayerNamed(order.Layer), order.Owner, order.Channel, order.X, order.Y, order.W, order.H, report.Values);
        }
        if (order.ItemLayer.Length > 0)
        {
            var items = Tools.Items(LayerNamed(order.ItemLayer), order.ItemOwner, order.ItemX, order.ItemY, order.ItemRadius);
            report.ItemCount = items.Length;
            report.Items = [.. items.SelectMany(item => (double[])[item.X, item.Y, .. item.Fields])];
        }
        if (order.Peak)
        {
            report.HasPeak = Tools.Peak(order.PeakX, order.PeakY, out var x, out var y, out var radius);
            (report.PeakX, report.PeakY, report.PeakRadius) = (x, y, radius);
        }
        ctx.Commands.SignalTo("hello.read", MemoryPackSerializer.Serialize(report), [ctx.Player]);
    }

    /// <summary>Доехал: сигнал клиентам с тем же payload (entity:u32, x:f64, y:f64).</summary>
    private static void OnArrived(EventContext ctx) => ctx.Commands.Signal("hello.beep"u8, ctx.Payload);

    /// <summary>Игрок вошёл (payload — u32): юниты у его дома — первый в центре, остальные по кругу в 24 тайла.</summary>
    private void OnJoined(EventContext ctx)
    {
        var player = BinaryPrimitives.ReadUInt32LittleEndian(ctx.Payload);
        var (x, y) = player <= 1 ? (0.0, 0.0) : (DetMath.Cos((player - 2) * Math.PI / 3) * 24, DetMath.Sin((player - 2) * Math.PI / 3) * 24);
        for (var k = 0; k < Start; k++)
            SpawnUnit(ctx.Commands, player, x - 2 + (k + 1) * 1.4, y + 4);
        Count(ctx.Commands, Start);
    }

    /// <summary>Новые юниты вокруг точки: разброс из зерна мира.</summary>
    private void OnSpawn(OrderContext ctx)
    {
        var order = ctx.Read<SpawnOrder>() ?? throw new ArgumentException("hello.spawn без payload");
        if (order.Count is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(order), "hello.spawn: от 1 до 1000 юнитов");
        for (var k = 0; k < order.Count; k++)
            SpawnUnit(ctx.Commands, ctx.Player, order.X + (ctx.Random() - 0.5) * order.Spread, order.Y + (ctx.Random() - 0.5) * order.Spread);
        Count(ctx.Commands, order.Count);
    }

    /// <summary>Своим живым юнитам — новая цель (чужие и мёртвые — мимо: проверка инструментами Has и Get).</summary>
    private void OnMove(OrderContext ctx)
    {
        var order = ctx.Read<MoveOrder>() ?? throw new ArgumentException("hello.move без payload");
        foreach (var entity in order.Entities)
        {
            if (!Tools.Has(entity, _drive) || Tools.Get(entity, _owner) is not { } owner || owner.Number(_owner["player"]) != ctx.Player)
                continue;
            ctx.Commands.Set(entity, _drive).Number(_drive["tx"], order.X).Number(_drive["ty"], order.Y);
        }
    }

    /// <summary>Ответ инструментов о точке и сущности — сигналом hello.probe (ProbeReport).</summary>
    private void OnProbe(OrderContext ctx)
    {
        var order = ctx.Read<ProbeOrder>() ?? throw new ArgumentException("hello.probe без payload");
        var report = new ProbeReport
        {
            Near = Tools.Near(order.X, order.Y, order.Radius, _drive),
            Units = Tools.Count(_drive),
            GameProto = Tools.ProtoId("games/hello"),
            FirstProto = Tools.ProtoName(0),
            Alive = Tools.Alive(order.Entity),
            HasDrive = Tools.Has(order.Entity, _drive),
            Text = Tools.StringId("hello") is { } id ? Tools.Text(id) : null,
        };
        report.HasLand = Tools.Terrain(order.X, order.Y, out var terrain);
        (report.Terrain, report.Biome, report.Foot) = (terrain.Terrain, terrain.Biome, terrain.Foot);
        if (Tools.Get(order.Entity, _position) is { } position)
            report.Position = [position.Number(_position["x"]), position.Number(_position["y"])];
        report.Storage = Tools.ReadStorage(out var storage);
        report.StorageEntity = storage;
        ctx.Commands.Signal("hello.probe"u8, MemoryPackSerializer.Serialize(report));
    }

    private void SpawnUnit(CommandWriter commands, uint player, double x, double y)
    {
        var entity = commands.Spawn();
        commands.Add(entity, _position).Number(_position["x"], x).Number(_position["y"], y);
        commands.Add(entity, _facing).Number(_facing["angle"], 0);
        commands.Add(entity, _owner).Number(_owner["player"], player);
        commands.Add(entity, _drive)
            .Number(_drive["speed"], Speed).Number(_drive["turn"], Turn)
            .Number(_drive["tx"], x).Number(_drive["ty"], y);
        commands.Add(entity, _unit).Number(_unit["radius"], Radius);
        commands.Add(entity, _sight).Number(_sight["radius"], Sight);
        if (Rover() is { } rover)
            commands.Add(entity, _proto).Number(_proto["id"], rover);
    }

    /// <summary>Вид юнита — units/rover (облик рисует Hello.Client). Без инструментов движка (тесты песочницы) — без вида.</summary>
    private int? Rover()
    {
        if (_rover is null)
        {
            try
            {
                _rover = Tools.ProtoId("units/rover") is >= 0 and var id ? id : null;
            }
            catch (ToolException)
            {
                return null;
            }
        }
        return _rover;
    }

    /// <summary>Память мода: сколько юнитов создано всего (u32). Хост без памяти — молча.</summary>
    private static void Count(CommandWriter commands, int added)
    {
        if (Tools.ReadStorage(out _) is not { } bytes)
            return;
        var total = bytes.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : 0;
        Span<byte> next = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(next, total + (uint)added);
        Tools.WriteStorage(commands, next);
    }

    /// <summary>
    /// Своим юнитам — держаться за ведущим (Leader 0 — новый ведущий в (Dx, Dy)). Сигнал hello.staged хозяину: сколько
    /// ведомых сцена видит с Follow сразу после записи (u32) и видит ли она нового ведущего с местом (u8).
    /// </summary>
    private void OnFollow(OrderContext ctx)
    {
        var order = ctx.Read<FollowOrder>() ?? throw new ArgumentException("hello.follow без payload");
        var stage = ctx.Stage();
        var leader = order.Leader;
        var (dx, dy) = (order.Dx, order.Dy);
        if (leader == 0)
        {
            SpawnUnit(ctx.Commands, ctx.Player, order.Dx, order.Dy);
            leader = stage.Spawned[^1];
            (dx, dy) = (0, 0);
        }
        var seen = 0u;
        foreach (var entity in order.Followers)
        {
            if (entity == leader || !Tools.Has(entity, _drive) || Tools.Get(entity, _owner) is not { } owner || owner.Number(_owner["player"]) != ctx.Player)
                continue;
            ctx.Commands.Add(entity, _follow).Number(_follow["leader"], leader).Number(_follow["dx"], dx).Number(_follow["dy"], dy);
            if (stage.Has(entity, _follow) && stage.Number(entity, _follow, _follow["leader"]) == leader && stage.Number(entity, _drive, _drive["speed"]) > 0)
                seen++;
        }
        Span<byte> payload = stackalloc byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, seen);
        payload[4] = order.Leader == 0 && stage.Alive(leader) && stage.Number(leader, _position, _position["x"]) == order.Dx ? (byte)1 : (byte)0;
        ctx.Commands.SignalTo("hello.staged", payload, [ctx.Player]);
    }

    /// <summary>Своим юнитам из списка — Anchor (тик): hello.follow их больше не ведёт.</summary>
    private void OnAnchor(OrderContext ctx)
    {
        var order = ctx.Read<MoveOrder>() ?? throw new ArgumentException("hello.anchor без payload");
        foreach (var entity in order.Entities)
            if (Tools.Get(entity, _owner) is { } owner && owner.Number(_owner["player"]) == ctx.Player)
                ctx.Commands.Add(entity, _anchor).Number(_anchor["since"], ctx.Tick);
    }

    /// <summary>Запрос hello.count: payload — игрок u32 (нет — все); ответ — юнитов u32 и ревизия мира u64.</summary>
    private void OnCount(QueryContext ctx)
    {
        var player = ctx.Payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(ctx.Payload) : 0;
        var units = Tools.Near(0, 0, 400, _drive);
        var owners = Tools.Column(units, _owner, _owner["player"]);
        var count = player == 0 ? units.Length : owners.Count(owner => owner == player);
        Span<byte> reply = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(reply, (uint)count);
        BinaryPrimitives.WriteUInt64LittleEndian(reply[4..], Tools.Revision().World);
        ctx.Reply(reply);
    }

    /// <summary>Бесконечный цикл: сторож эпох должен прервать.</summary>
    private static void OnSpin(OrderContext ctx)
    {
        var k = ctx.Tick;
        while (true)
        {
            k = k * 6364136223846793005UL + 1442695040888963407UL;
            if (k == 42)
                Host.Log("не случится");
        }
    }

    /// <summary>Берёт память, пока дают: предел памяти экземпляра должен остановить.</summary>
    private static void OnHog(OrderContext ctx)
    {
        var hoard = new List<byte[]>();
        while (true)
        {
            var block = new byte[8 << 20];
            block[^1] = 1;
            hoard.Add(block);
        }
    }

    /// <summary>Ходовая: столбцы кадра → Wheels.Drive; доехавшим — событие hello.arrived.</summary>
    private sealed class DriveSystem(HelloMod mod) : ISystem
    {
        private Column _x, _y, _facing, _speed, _turn, _tx, _ty;

        public void Bind(SystemBinding binding)
        {
            _x = binding.Column("Position", "x");
            _y = binding.Column("Position", "y");
            _facing = binding.Column("Facing", "angle");
            _speed = binding.Column("Drive", "speed");
            _turn = binding.Column("Drive", "turn");
            _tx = binding.Column("Drive", "tx");
            _ty = binding.Column("Drive", "ty");
        }

        public void Run(SystemContext ctx)
        {
            var arrivals = new EmitArrivals(ctx.Commands);
            Wheels.Drive(
                ctx.Column(_x), ctx.Column(_y), ctx.Column(_facing),
                ctx.Column(_speed), ctx.Column(_turn), ctx.Column(_tx), ctx.Column(_ty),
                ctx.Entities, ctx.Tick, ctx.Dt, mod.Arrival, ref arrivals);
        }
    }

    /// <summary>Теснота: корпуса Unit.radius расталкивает пакетный инструмент движка (Tools.Separate) прямо в столбцах.</summary>
    private sealed class CrowdSystem(HelloMod mod) : ISystem
    {
        private Column _x, _y, _radius;

        public void Bind(SystemBinding binding)
        {
            _x = binding.Column("Position", "x");
            _y = binding.Column("Position", "y");
            _radius = binding.Column("Unit", "radius");
        }

        public void Run(SystemContext ctx)
        {
            if (ctx.Count > 1)
                Tools.Separate(ctx.Column(_x), ctx.Column(_y), ctx.Column(_radius), default, mod.Crowd);
        }
    }

    /// <summary>
    /// За ведущим: цель — его место (побочная таблица Position+Unit) со сдвигом; ведущий сам ведомый (есть в кадре) —
    /// его цель. Ведущего нет — Follow снимается.
    /// </summary>
    private sealed class FollowSystem(HelloMod mod) : ISystem
    {
        private Column _tx, _ty, _leader, _dx, _dy, _x, _y;

        public void Bind(SystemBinding binding)
        {
            _tx = binding.Column("Drive", "tx");
            _ty = binding.Column("Drive", "ty");
            _leader = binding.Column("Follow", "leader");
            _dx = binding.Column("Follow", "dx");
            _dy = binding.Column("Follow", "dy");
            _x = binding.Table(0).Column("Position", "x");
            _y = binding.Table(0).Column("Position", "y");
        }

        public void Run(SystemContext ctx)
        {
            var leaders = ctx.Table(0);
            var x = leaders.Column(_x);
            var y = leaders.Column(_y);
            var tx = ctx.Column(_tx);
            var ty = ctx.Column(_ty);
            var lead = ctx.Column(_leader);
            var dx = ctx.Column(_dx);
            var dy = ctx.Column(_dy);
            for (var i = 0; i < ctx.Count; i++)
            {
                var leader = (uint)lead[i];
                if (ctx.RowOf(leader) is >= 0 and var chain)
                {
                    (tx[i], ty[i]) = (tx[chain] + dx[i], ty[chain] + dy[i]);
                    continue;
                }
                if (leaders.RowOf(leader) is >= 0 and var row)
                    (tx[i], ty[i]) = (x[row] + dx[i], y[row] + dy[i]);
                else
                    ctx.Commands.Remove(ctx.Entities[i], mod._follow);
            }
        }
    }

    /// <summary>Край карты (±255,5): пишущая таблица Position+Unit — что не двигалось, в мир не отмечается.</summary>
    private sealed class WrapSystem : ISystem
    {
        private const double Edge = 255.5;
        private Column _x, _y;

        public void Bind(SystemBinding binding)
        {
            _x = binding.Table(0).Column("Position", "x");
            _y = binding.Table(0).Column("Position", "y");
        }

        public void Run(SystemContext ctx)
        {
            var units = ctx.Table(0);
            foreach (var column in new[] { _x, _y })
            {
                var values = units.Column(column);
                for (var i = 0; i < values.Length; i++)
                    values[i] = Math.Clamp(values[i], -Edge, Edge);
            }
        }
    }

    private readonly struct EmitArrivals(CommandWriter commands) : IArrivals
    {
        public void Arrived(int row, uint entity, double x, double y)
        {
            Span<byte> payload = stackalloc byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, entity);
            BinaryPrimitives.WriteDoubleLittleEndian(payload[4..], x);
            BinaryPrimitives.WriteDoubleLittleEndian(payload[12..], y);
            commands.Emit("hello.arrived"u8, payload);
        }
    }
}
