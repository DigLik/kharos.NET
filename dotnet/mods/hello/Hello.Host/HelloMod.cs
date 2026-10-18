using System.Buffers.Binary;
using System.Text.Json;
using Kharos.Mod.Sdk;
using MemoryPack;

namespace Hello;

/// <summary>
/// Пробный мод (docs/dotnet.md, Н2–Н3): ходовая над Position, Facing, Drive и теснота инструментом движка Separate;
/// юниты — вида units/rover (data/units.json);
/// компоненты Drive и Unit объявлены в mod.json. События: start, player_joined (три юнита у дома игрока),
/// hello.arrived (сигнал клиентам). Приказы: hello.spawn, hello.move, hello.probe (ответ инструментов сигналом);
/// hello.spin, hello.hog, hello.fail, hello.bad — для тестов пределов и сбоев.
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
    private int? _rover;

    public override void Setup(ModBuilder mod)
    {
        mod.System("hello.drive", new DriveSystem(this))
            .Query("Position", "Facing", "Drive")
            .Write("Position", "Facing", "Drive");
        mod.System("hello.crowd", new CrowdSystem(this))
            .Query("Position", "Unit")
            .Write("Position")
            .After("drive");
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
    }

    /// <summary>Доехал: сигнал клиентам с тем же payload (entity:u32, x:f64, y:f64).</summary>
    private static void OnArrived(EventContext ctx) => ctx.Commands.Signal("hello.beep"u8, ctx.Payload);

    /// <summary>Игрок вошёл (payload — u32): юниты у его дома — первый в центре, остальные по кругу в 24 тайла.</summary>
    private void OnJoined(EventContext ctx)
    {
        var player = BinaryPrimitives.ReadUInt32LittleEndian(ctx.Payload);
        var (x, y) = player <= 1 ? (0.0, 0.0) : (Math.Cos((player - 2) * Math.PI / 3) * 24, Math.Sin((player - 2) * Math.PI / 3) * 24);
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
