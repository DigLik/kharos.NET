using System.Buffers.Binary;
using System.Text.Json;
using Kharos.Mod.Sdk;

namespace Hello;

/// <summary>
/// Пробный мод (docs/dotnet.md, Н2): система «ходовая» над Position, Facing, Drive; обработчик события
/// hello.arrived; приказ hello.spawn. Приказы hello.spin, hello.hog и hello.fail — для тестов пределов песочницы.
/// </summary>
public sealed class HelloMod : Mod
{
    /// <summary>Расстояние «доехал», тайлы (data/hello.json).</summary>
    public double Arrival { get; private set; } = 0.5;

    /// <summary>Скорость и поворот новых юнитов.</summary>
    public double Speed { get; private set; } = 4;
    public double Turn { get; private set; } = 3;

    private ComponentRef _position = null!;
    private ComponentRef _facing = null!;
    private ComponentRef _owner = null!;
    private ComponentRef _drive = null!;

    public override void Setup(ModBuilder mod)
    {
        mod.System("hello.drive", new DriveSystem(this))
            .Query("Position", "Facing", "Drive")
            .Write("Position", "Facing", "Drive")
            .After("orders");
        mod.On("hello.arrived", OnArrived);
        mod.Order("hello.spawn", OnSpawn);
        mod.Order("hello.spin", OnSpin);
        mod.Order("hello.hog", OnHog);
        mod.Order("hello.fail", _ => throw new InvalidOperationException("приказ отказан"));
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
                }
            }
        }
        _position = ctx.Component("Position");
        _facing = ctx.Component("Facing");
        _owner = ctx.Component("Owner");
        _drive = ctx.Component("Drive");
    }

    /// <summary>Доехал: сигнал клиентам с тем же payload (entity:u32, x:f64, y:f64).</summary>
    private static void OnArrived(EventContext ctx) => ctx.Commands.Signal("hello.beep"u8, ctx.Payload);

    /// <summary>Новые юниты вокруг точки: Position с разбросом из зерна мира, Facing, Owner, Drive.</summary>
    private void OnSpawn(OrderContext ctx)
    {
        var order = ctx.Read<SpawnOrder>() ?? throw new ArgumentException("hello.spawn без payload");
        if (order.Count is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(order), "hello.spawn: от 1 до 1000 юнитов");
        var commands = ctx.Commands;
        for (var k = 0; k < order.Count; k++)
        {
            var entity = commands.Spawn();
            var x = order.X + ctx.Random() - 0.5;
            var y = order.Y + ctx.Random() - 0.5;
            commands.Add(entity, _position).Number(_position["x"], x).Number(_position["y"], y);
            commands.Add(entity, _facing).Number(_facing["angle"], 0);
            commands.Add(entity, _owner).Number(_owner["player"], ctx.Player);
            commands.Add(entity, _drive)
                .Number(_drive["speed"], Speed).Number(_drive["turn"], Turn)
                .Number(_drive["tx"], x).Number(_drive["ty"], y);
        }
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
