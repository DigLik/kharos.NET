using System.Buffers.Binary;
using System.Diagnostics;
using Hello;
using Kharos.Plugins.Game;
using MemoryPack;

namespace Kharos.Plugins.Tests;

/// <summary>
/// Мод hello (mods/hello, NativeAOT-LLVM → host.wasm) через рантайм игровых модов. Без собранного host.wasm тесты
/// пропускаются: pwsh dotnet/tools/build-mods.ps1.
/// </summary>
public class HelloModTests
{
    private static GameModuleHost Host(Action<GameModuleOptions>? configure = null)
    {
        Assert.SkipUnless(HelloModule.Built, HelloModule.NotBuilt);
        var options = new GameModuleOptions();
        configure?.Invoke(options);
        return new GameModuleHost(options);
    }

    private static GameModule Load(GameModuleHost host, ulong seed = 42) =>
        host.LoadFile("hello", HelloModule.WasmPath, HelloModule.LoadInfo(seed));

    [Fact]
    public void LoadsAndDeclaresAbi()
    {
        using var host = Host();
        using var module = Load(host);
        using var instance = module.CreateInstance();

        Assert.Equal(1, GameModuleHost.AbiVersion);
        Assert.Equal(1, instance.Manifest.Abi);
        Assert.Equal(4, instance.Manifest.Systems.Length);
        var drive = instance.Manifest.Systems[0];
        Assert.Equal("hello.drive", drive.Name);
        Assert.Equal(["Position", "Facing", "Drive"], drive.Query);
        Assert.Equal(["Position", "Facing", "Drive"], drive.Write);
        Assert.Empty(drive.After);
        var crowd = instance.Manifest.Systems[1];
        Assert.Equal(("hello.crowd", "drive"), (crowd.Name, Assert.Single(crowd.After)));
        // Н5: кадр с таблицей и without, система из одной пишущей таблицы, запрос.
        var follow = instance.Manifest.Systems[2];
        Assert.Equal("hello.follow", follow.Name);
        Assert.Equal(["Anchor"], follow.Without);
        Assert.Equal(["Position", "Unit"], Assert.Single(follow.Tables).Query);
        Assert.False(follow.Tables[0].Write);
        var wrap = instance.Manifest.Systems[3];
        Assert.Equal(("hello.wrap", 0, true), (wrap.Name, wrap.Query.Length, Assert.Single(wrap.Tables).Write));
        Assert.Equal([new ModTableLayout(3, false)], instance.Systems[2].Tables);
        Assert.Equal(["hello.count"], instance.Manifest.Queries);
        Assert.Equal(["hello.arrived", "player_joined"], instance.Manifest.Events);
        Assert.Contains("hello.spawn", instance.Manifest.Orders);
        Assert.Equal(HelloModule.Width, instance.Systems[0].Width);
        Assert.All(module.Imports, i => Assert.Contains(i, GameModuleHost.AllowedImports));
        Assert.DoesNotContain(module.Imports, i => i.StartsWith("env.", StringComparison.Ordinal));
    }

    [Fact]
    public void DriveSystemMatchesPlainCSharp()
    {
        using var host = Host();
        using var module = Load(host);
        using var instance = module.CreateInstance();
        const int n = 2000;
        var (entities, wasm) = HelloModule.Units(n);
        var plain = (double[])wasm.Clone();
        var system = instance.SystemId("hello.drive");
        var wasmArrivals = 0;
        var plainArrivals = new CountArrivals();

        for (ulong tick = 0; tick < 300; tick++)
        {
            var commands = instance.RunSystem(system, entities, wasm, new ModCall(tick, 0.05, (uint)n + 1));
            foreach (var command in ModCommands.Decode(commands))
            {
                Assert.Equal(ModCommandOp.Emit, command.Op);
                Assert.Equal("hello.arrived", command.Name);
                Assert.Equal(20, command.Payload!.Length);
                wasmArrivals++;
            }
            Wheels.Drive(
                Col(plain, n, HelloModule.X), Col(plain, n, HelloModule.Y), Col(plain, n, HelloModule.Angle),
                Col(plain, n, HelloModule.Speed), Col(plain, n, HelloModule.Turn),
                Col(plain, n, HelloModule.Tx), Col(plain, n, HelloModule.Ty),
                entities, tick, 0.05, 0.5, ref plainArrivals);
        }

        Assert.True(wasmArrivals > 0, "за 300 тиков кто-то должен доехать");
        Assert.Equal(plainArrivals.Count, wasmArrivals);
        // Тригонометрия ходовой — DetMath (перенос musl, тот же исходник в wasm и на хосте): совпадение до бита.
        for (var i = 0; i < wasm.Length; i++)
            Assert.True(BitConverter.DoubleToInt64Bits(wasm[i]) == BitConverter.DoubleToInt64Bits(plain[i]), $"столбец {i / n}, строка {i % n}: {wasm[i]:R} ≠ {plain[i]:R}");
    }

    [Fact]
    public void SpawnOrderWritesCommandsInEngineFormat()
    {
        using var host = Host();
        using var module = Load(host);
        using var instance = module.CreateInstance();
        var payload = MemoryPackSerializer.Serialize(new SpawnOrder { Count = 2, X = 10, Y = 20 });

        var bytes = instance.RunOrder(instance.OrderId("hello.spawn"), 3, payload, new ModCall(5, 0.05, 100)).ToArray();
        var commands = ModCommands.Decode(bytes);

        // На юнита: Spawn, Add Position, Facing, Owner, Drive, Unit, Sight. Памяти мода без хоста нет — её запись молчит.
        Assert.Equal(14, commands.Count);
        Assert.Equal((ModCommandOp.Spawn, 100u), (commands[0].Op, commands[0].Entity));
        Assert.Equal((ModCommandOp.Spawn, 101u), (commands[7].Op, commands[7].Entity));
        Assert.Equal((HelloModule.Unit, 0.5), (commands[5].Component, Assert.Single(commands[5].Fields).Number));
        Assert.Equal((HelloModule.Sight, 6.0), (commands[6].Component, Assert.Single(commands[6].Fields).Number));
        var position = commands[1];
        Assert.Equal((ModCommandOp.Add, 100u, HelloModule.Position), (position.Op, position.Entity, position.Component));
        Assert.Equal([0, 1], position.Fields.Select(f => (int)f.Field));
        Assert.InRange(position.Fields[0].Number, 9.5, 10.5);
        Assert.InRange(position.Fields[1].Number, 19.5, 20.5);
        Assert.Equal(3.0, Assert.Single(commands[3].Fields).Number);
        var drive = commands[4];
        Assert.Equal(HelloModule.Drive, drive.Component);
        Assert.Equal([4.0, 3.0], drive.Fields.Take(2).Select(f => f.Number)); // speed и turn из data/hello.json

        // kh.random — из зерна: тот же мир даёт те же байты, другое зерно — другие.
        using var twin = module.CreateInstance();
        Assert.Equal(bytes, twin.RunOrder(twin.OrderId("hello.spawn"), 3, payload, new ModCall(5, 0.05, 100)).ToArray());
        using var other = Load(host, seed: 7);
        using var otherInstance = other.CreateInstance();
        Assert.NotEqual(bytes, otherInstance.RunOrder(otherInstance.OrderId("hello.spawn"), 3, payload, new ModCall(5, 0.05, 100)).ToArray());
    }

    [Fact]
    public void GrowingPayloadAfterPauseIsNotATimeout()
    {
        // Эпохи идут и между вызовами: kh_free старого буфера при росте payload раньше шёл со сроком прошлого вызова,
        // и после паузы дольше предела ловился как перерасход времени.
        using var host = Host(o => o.CallTimeoutMs = 50);
        using var module = Load(host);
        using var instance = module.CreateInstance();
        var arrived = instance.EventId("hello.arrived");
        instance.RaiseEvent(arrived, new byte[20], new ModCall(1, 0.05, 1));
        Thread.Sleep(300);

        var payload = new byte[4000];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 77);
        var command = Assert.Single(ModCommands.Decode(instance.RaiseEvent(arrived, payload, new ModCall(2, 0.05, 1))));

        Assert.Equal(payload, command.Payload);
        Assert.False(instance.Faulted);
        Assert.Equal(0, module.Strikes);
    }

    [Fact]
    public void EventHandlerAnswersWithSignal()
    {
        using var host = Host();
        using var module = Load(host);
        using var instance = module.CreateInstance();
        var payload = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 77);

        var command = Assert.Single(ModCommands.Decode(instance.RaiseEvent(instance.EventId("hello.arrived"), payload, new ModCall(1, 0.05, 1))));

        Assert.Equal((ModCommandOp.Signal, "hello.beep"), (command.Op, command.Name));
        Assert.Equal(payload, command.Payload);
    }

    [Fact]
    public void ErrorsComeBackAsCodes()
    {
        using var host = Host();
        using var module = Load(host);
        using var instance = module.CreateInstance();

        var failed = Assert.Throws<ModCallException>(() => instance.RunOrder(instance.OrderId("hello.fail"), 1, [], default));
        Assert.Equal(-5, failed.Code);
        Assert.Contains("приказ отказан", failed.Message);
        Assert.Equal(-1, Assert.Throws<ModCallException>(() => instance.RunOrder(99, 1, [], default)).Code);
        Assert.Equal(-5, Assert.Throws<ModCallException>(() => instance.RunOrder(instance.OrderId("hello.spawn"), 1, [1, 2, 3], default)).Code);

        // После ошибки мода (не ловушки) экземпляр годен.
        Assert.False(instance.Faulted);
        var (entities, data) = HelloModule.Units(10);
        instance.RunSystem(0, entities, data, new ModCall(0, 0.05, 11));
    }

    [Fact]
    public void MemoryLimitIsEnforced()
    {
        // Рантайму NativeAOT-LLVM нужно ~50 МБ сразу: с пределом 16 МБ экземпляр не создаётся.
        using (var tight = Host(o => o.MemoryLimitBytes = 16L << 20))
        using (var module = Load(tight))
            Assert.ThrowsAny<ModException>(() => module.CreateInstance());

        const long limit = 96L << 20;
        using var host = Host(o => o.MemoryLimitBytes = limit);
        using var hello = Load(host);
        using var instance = hello.CreateInstance();
        Assert.True(instance.MemoryBytes <= limit);
        Assert.Equal(0, instance.Allocate(200 << 20));

        // Мод берёт память, пока дают: вызов падает, памяти не больше предела.
        Assert.ThrowsAny<ModException>(() => instance.RunOrder(instance.OrderId("hello.hog"), 1, [], default));
        Assert.True(instance.MemoryBytes <= limit, $"{instance.MemoryBytes} > {limit}");
    }

    [Fact]
    public void EndlessOrderIsInterruptedAndModDisabledAfterThreeStrikes()
    {
        using var host = Host(o => o.CallTimeoutMs = 50);
        using var module = Load(host);
        var spin = 0;
        for (var strike = 1; strike <= 3; strike++)
        {
            using var instance = module.CreateInstance();
            spin = instance.OrderId("hello.spin");
            var sw = Stopwatch.StartNew();
            var trap = Assert.Throws<ModTrapException>(() => instance.RunOrder(spin, 1, [], default));
            Assert.True(trap.Timeout);
            Assert.True(sw.ElapsedMilliseconds < 2000, $"прервано через {sw.ElapsedMilliseconds} мс");
            Assert.True(instance.Faulted);
            Assert.Equal(strike, module.Strikes);
        }
        Assert.True(module.Disabled);
        Assert.Throws<ModDisabledException>(() => module.CreateInstance());
    }

    private static Span<double> Col(double[] data, int n, int column) => data.AsSpan(column * n, n);

    private struct CountArrivals : IArrivals
    {
        public int Count;

        public void Arrived(int row, uint entity, double x, double y) => Count++;
    }
}
