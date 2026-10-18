using System.Buffers.Binary;
using Hello;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;
using Kharos.Mod.Abi;
using Kharos.Plugins.Game;
using MemoryPack;

namespace Kharos.Host.Tests;

/// <summary>Хост с модом hello в wasm: схема из mod.json, системы, события, приказы, инструменты, память, сбои.</summary>
public class HostTests
{
    [Fact]
    public void JoinSpawnsUnitsAndCountsThemInStorage()
    {
        using var host = HelloGame.Create();
        var mod = Assert.Single(host.Mods);
        // Системы мода по After/Before, затем системы движка в порядке sim.ts (на них ссылаются полными именами).
        Assert.Equal(
            ["hello.follow", "hello.drive", "hello.crowd", "hello.wrap", "engine.paths", "engine.move", "engine.mounts", "engine.traces", Host.VisionSystem,
                "engine.recallLand", "engine.recallPaving", "engine.memory"],
            host.Scheduler.Names);
        Assert.True(host.World.Schema.Find("Drive") is not null && host.World.Schema.Find("Unit") is not null);

        host.Start();
        Assert.True(host.Join(1));
        Assert.False(host.Join(1));

        var units = HelloGame.Units(host.World, 1);
        Assert.Equal(3, units.Count);
        foreach (var unit in units)
        {
            Assert.Equal(6, host.World.Number(unit, Builtin.Sight, 0));
            Assert.Equal(0.5, host.World.Number(unit, host.World.Schema.Id("Unit"), 0));
        }
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(host.Storage(mod)));
        Assert.Equal("hello", host.World.Get(mod.StorageEntity, host.StorageComponent, "mod").Text);
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void OrderedUnitDrivesToTargetAndBeeps()
    {
        using var host = HelloGame.Create();
        host.Start();
        host.Join(1);
        host.Join(2);
        var unit = HelloGame.Units(host.World, 1)[0];
        var foreign = HelloGame.Units(host.World, 2)[0];
        var (tx, ty) = (30.0, -12.0);
        // Чужой юнит в приказе — мимо: приказ проверяет владельца инструментом Get.
        host.Send(1, "hello.move", HelloGame.Move(tx, ty, unit, foreign));
        host.Step();
        var drive = host.World.Schema.Id("Drive");
        Assert.Equal((tx, ty), (host.World.Number(unit, drive, 2), host.World.Number(unit, drive, 3)));
        Assert.NotEqual((tx, ty), (host.World.Number(foreign, drive, 2), host.World.Number(foreign, drive, 3)));

        double Distance()
        {
            var (x, y) = HelloGame.Position(host.World, unit);
            return Math.Sqrt((x - tx) * (x - tx) + (y - ty) * (y - ty));
        }
        var start = Distance();
        var beeped = false;
        var previous = start;
        for (var tick = 0; tick < 600 && !beeped; tick++)
        {
            host.Step();
            foreach (var signal in host.TakeDelta().Signals)
                if (signal.Name == "hello.beep" && BinaryPrimitives.ReadUInt32LittleEndian(signal.Payload) == unit)
                {
                    beeped = true;
                    var x = BinaryPrimitives.ReadDoubleLittleEndian(signal.Payload.AsSpan(4));
                    var y = BinaryPrimitives.ReadDoubleLittleEndian(signal.Payload.AsSpan(12));
                    Assert.True(Math.Abs(x - tx) < 0.6 && Math.Abs(y - ty) < 0.6, $"доехал в ({x}, {y})");
                }
            if (!beeped && tick > 20)
                Assert.True(Distance() <= previous + 0.05, $"тик {tick}: удаляется от цели ({Distance()} > {previous})");
            previous = Distance();
        }
        Assert.True(beeped, $"не доехал: осталось {Distance()} из {start}");
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void ToolsAnswerModLikeEngine()
    {
        using var host = HelloGame.Create();
        host.Start();
        host.Join(1);
        host.Send(1, "hello.spawn", HelloGame.Spawn(200, 10, 10, spread: 30));
        host.Step();
        host.TakeDelta();
        var world = host.World;
        var probe = HelloGame.Units(world, 1)[5];
        var (px, py) = (12.3, 7.9);
        // Приказы идут до систем: инструменты видят мир прошлого тика — его и берём эталоном.
        var near = HelloGame.Units(world).Where(e => HelloGame.Position(world, e) is var (x, y) && (x - px) * (x - px) + (y - py) * (y - py) <= 36).ToArray();
        var position = HelloGame.Position(world, probe);
        host.Send(1, "hello.probe", MemoryPackSerializer.Serialize(new ProbeOrder { X = px, Y = py, Radius = 6, Entity = probe }));
        host.Step();
        var signal = Assert.Single(host.TakeDelta().Signals, s => s.Name == "hello.probe");
        var report = MemoryPackSerializer.Deserialize<ProbeReport>(signal.Payload)!;

        Assert.Empty(host.Errors);
        Assert.Equal(near, report.Near);
        Assert.Equal([position.X, position.Y], report.Position);
        Assert.Equal(203, report.Units);
        Assert.True(report.Alive && report.HasDrive);
        Assert.Equal(host.Content.FindProto("games/hello")!.Id, report.GameProto);
        Assert.Equal(host.Content.Protos[0].Path, report.FirstProto);
        Assert.Equal("hello", report.Text);
        Assert.Equal(203u, BinaryPrimitives.ReadUInt32LittleEndian(report.Storage));
        Assert.Equal(host.Mods[0].StorageEntity, report.StorageEntity);
        var (terrain, biome, foot) = EngineTools.Terrain(new Land(host.Game.Land!), px, py);
        Assert.True(report.HasLand);
        Assert.Equal((terrain, biome, foot), (report.Terrain, report.Biome, report.Foot));
        Assert.NotEmpty(report.Near);
    }

    [Fact]
    public void NearToolMatchesBruteForce()
    {
        using var host = HelloGame.Create();
        host.Start();
        host.Join(1);
        host.Send(1, "hello.spawn", HelloGame.Spawn(500, 0, 0, spread: 40));
        host.Step();
        var world = host.World;
        var drive = world.Schema.Id("Drive");
        var points = new[] { (0.0, 0.0, 5.0), (10.5, -3.2, 8.0), (-15.0, 12.0, 3.0), (100.0, 100.0, 4.0) };
        var input = new List<byte>();
        void U32(uint v) => input.AddRange(BitConverter.GetBytes(v));
        void F64(double v) => input.AddRange(BitConverter.GetBytes(v));
        U32((uint)points.Length);
        U32(1);
        U32((uint)drive);
        foreach (var (x, y, r) in points)
        {
            F64(x);
            F64(y);
            F64(r);
        }
        var output = new byte[1 << 16];
        var length = host.Tools.Call(null, ModAbi.ToolNear, input.ToArray(), output);
        Assert.True(length > 0);
        var at = 0;
        foreach (var (x, y, r) in points)
        {
            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(at));
            var found = Enumerable.Range(0, count).Select(k => BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(at + 4 + k * 4))).ToList();
            at += 4 + count * 4;
            var expected = HelloGame.Units(world).Where(e =>
            {
                var (ex, ey) = HelloGame.Position(world, e);
                return (ex - x) * (ex - x) + (ey - y) * (ey - y) <= r * r;
            }).ToList();
            Assert.Equal(expected, found);
        }
        Assert.Equal(length, at);
        // Мало места — ничего не записано, ответ — сколько нужно.
        Assert.Equal(length, host.Tools.Call(null, ModAbi.ToolNear, input.ToArray(), new byte[3]));
        Assert.Equal(ModAbi.ErrorBadInput, host.Tools.Call(null, ModAbi.ToolNear, [1, 0, 0], output));
        Assert.Equal(ModAbi.ErrorUnknownId, host.Tools.Call(null, 99, [], output));
    }

    [Fact]
    public void SeparateMatchesPairwiseReference()
    {
        var random = new Random(5);
        const int n = 600;
        var x = new double[n];
        var y = new double[n];
        var radius = new double[n];
        var moving = new double[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = random.NextDouble() * 20;
            y[i] = random.NextDouble() * 20;
            radius[i] = 0.3 + random.NextDouble() * 0.5;
            moving[i] = random.Next(2);
        }
        x[7] = x[3];
        y[7] = y[3];
        var (rx, ry) = ((double[])x.Clone(), (double[])y.Clone());
        EngineTools.Separate(x, y, radius, moving, 0.5);

        // Эталон: все пары по порядку номеров.
        var dx = new double[n];
        var dy = new double[n];
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                var (ex, ey) = (rx[j] - rx[i], ry[j] - ry[i]);
                var reach = radius[i] + radius[j];
                var d2 = ex * ex + ey * ey;
                if (d2 >= reach * reach)
                    continue;
                var d = Math.Sqrt(d2);
                var (ux, uy) = d > 1e-9 ? (ex / d, ey / d) : (Math.Cos(i * 7 + j * 13), Math.Sin(i * 7 + j * 13));
                var overlap = (reach - d) * 0.5;
                var (wi, wj) = (moving[i] != 0 ? 1.0 : 3.0, moving[j] != 0 ? 1.0 : 3.0);
                dx[i] -= ux * overlap * wi / (wi + wj);
                dy[i] -= uy * overlap * wi / (wi + wj);
                dx[j] += ux * overlap * wj / (wi + wj);
                dy[j] += uy * overlap * wj / (wi + wj);
            }
        for (var i = 0; i < n; i++)
        {
            Assert.Equal(rx[i] + dx[i], x[i], 1e-9);
            Assert.Equal(ry[i] + dy[i], y[i], 1e-9);
        }
        Assert.True(Math.Abs(x[7] - x[3]) + Math.Abs(y[7] - y[3]) > 0.1, "совпавшие разведены");
    }

    [Fact]
    public void CrowdSystemPushesUnitsApart()
    {
        using var host = HelloGame.Create();
        host.Start();
        host.Join(1);
        // 100 юнитов в квадрате 2×2: корпуса по 0,5 сильно перекрыты.
        host.Send(1, "hello.spawn", HelloGame.Spawn(100, 50, 50, spread: 2));
        host.Step();
        double Overlap()
        {
            var units = HelloGame.Units(host.World, 1).Where(e => HelloGame.Position(host.World, e) is var (x, y) && Math.Abs(x - 50) < 20 && Math.Abs(y - 50) < 20).ToList();
            var total = 0.0;
            for (var i = 0; i < units.Count; i++)
                for (var j = i + 1; j < units.Count; j++)
                {
                    var (ax, ay) = HelloGame.Position(host.World, units[i]);
                    var (bx, by) = HelloGame.Position(host.World, units[j]);
                    total += Math.Max(0, 1 - Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by)));
                }
            return total;
        }
        var before = Overlap();
        host.Step();
        host.Step();
        Assert.True(Overlap() < before * 0.8, $"перекрытие {before} → {Overlap()}");
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void BadCommandBuffersDisableModAfterThreeStrikes()
    {
        var log = new List<string>();
        using var host = HelloGame.Create(log: log.Add);
        host.Start();
        host.Join(1);
        var mod = host.Mods[0];
        var unit = HelloGame.Units(host.World, 1)[0];

        // Ошибка в коде мода (исключение на плохой payload) — не сбой: игрок не должен выключать мод мусором.
        host.Send(1, "hello.fail", []);
        host.Send(1, "hello.spawn", [1, 2, 3]);
        host.Step();
        Assert.Equal(0, mod.Module.Strikes);
        Assert.Equal(2, host.Errors.Count);

        for (var k = 0; k < 3; k++)
        {
            host.Send(1, "hello.bad", []);
            host.Step();
        }
        Assert.Equal(3, mod.Module.Strikes);
        Assert.True(mod.Disabled);
        Assert.Contains(host.Errors, error => error.Exception is CommandException && error.Mod == "hello");
        Assert.Contains(log, line => line.Contains("выключен"));

        // Выключенный мод молчит: юниты стоят, приказы не идут.
        var position = HelloGame.Position(host.World, unit);
        host.Send(1, "hello.spawn", HelloGame.Spawn(5, 0, 0));
        for (var k = 0; k < 10; k++)
            host.Step();
        Assert.Equal(position, HelloGame.Position(host.World, unit));
        Assert.Equal(3, HelloGame.Units(host.World, 1).Count);
    }

    [Fact]
    public void TrapRestartsInstanceAndGameGoesOn()
    {
        Assert.SkipUnless(HelloGame.Built, HelloGame.NotBuilt);
        using var host = new Host(HelloGame.Content(), new HostOptions { Modules = new GameModuleOptions { CallTimeoutMs = 50 } });
        host.Start();
        host.Join(1);
        var mod = host.Mods[0];
        host.Send(1, "hello.spin", []);
        host.Step();
        Assert.Equal(1, mod.Module.Strikes);
        Assert.Contains(host.Errors, error => error.Exception is ModTrapException { Timeout: true });

        // Новый экземпляр поднимается сам: приказы и системы идут дальше.
        host.Send(1, "hello.spawn", HelloGame.Spawn(4, 5, 5));
        host.Step();
        Assert.Equal(1, mod.Restarts);
        Assert.Equal(7, HelloGame.Units(host.World, 1).Count);
        Assert.False(mod.Disabled);
    }

    [Fact]
    public void SameSeedSameWorld()
    {
        static byte[] Run(ulong seed)
        {
            using var host = HelloGame.Create(seed);
            host.Start();
            host.Join(1);
            host.Send(1, "hello.spawn", HelloGame.Spawn(50, 0, 0, spread: 10));
            for (var k = 0; k < 40; k++)
                host.Step();
            return WorldSave.Save(host.World);
        }
        Assert.Equal(Run(3), Run(3));
        Assert.NotEqual(Run(3), Run(4));
    }
}
