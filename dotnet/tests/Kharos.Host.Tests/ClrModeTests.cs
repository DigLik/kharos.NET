using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using Hello;
using Kharos.Content;
using Kharos.Engine.World;
using Kharos.Plugins.Game;
using MemoryPack;

namespace Kharos.Host.Tests;

/// <summary>
/// Режим CLR (docs/dotnet-n5.md, Н5-5): мод — сборка .NET в своей AssemblyLoadContext, те же вызовы, что у host.wasm.
/// Один сценарий в обоих режимах даёт один и тот же мир до байта: тригонометрия мода — DetMath.
/// </summary>
public sealed class ClrModeTests(ITestOutputHelper output)
{
    /// <summary>Итог сценария: сохранение мира и всего хоста, сигналы по порядку, ответы запроса.</summary>
    private sealed record Outcome(byte[] World, byte[] Save, List<string> Signals, byte[] Count, ulong Random, double Ms);

    /// <summary>
    /// Сценарий на всё, что даёт Н5: приказы с правами, Stage, кадр с таблицей и Without, система из пишущей таблицы,
    /// адресные сигналы, запрос, инструменты Columns и Revision — и 300 тиков хода.
    /// </summary>
    private static Outcome Play(ModMode mode)
    {
        var errors = new List<string>();
        using var host = HelloGame.Create(mode, seed: 7, log: errors.Add);
        Assert.Equal(mode, host.ModMode);
        Assert.All(host.Mods, mod => Assert.Equal(mode, mod.Mode));
        var signals = new List<string>();
        void Collect()
        {
            foreach (var signal in host.TakeDelta().Signals)
                signals.Add($"{host.Tick} {signal.Name} {Convert.ToHexString(signal.Payload)} {string.Join(',', signal.To ?? [])}");
        }
        var started = Stopwatch.GetTimestamp();
        host.Start();
        host.Join(1);
        host.Join(2);
        host.Send(1, "hello.spawn", HelloGame.Spawn(300, 10, 10, spread: 60));
        host.Send(2, "hello.spawn", HelloGame.Spawn(120, -40, 30, spread: 30));
        host.Step();
        Collect();
        var mine = HelloGame.Units(host.World, 1);
        var theirs = HelloGame.Units(host.World, 2);
        host.Send(1, "hello.move", HelloGame.Move(50, -40, [.. mine.Take(60)]));
        host.Send(1, "hello.follow", Follow(mine[0], [.. mine.Skip(1).Take(30), theirs[0]], 2, 1));
        host.Send(1, "hello.follow", Follow(0, [.. mine.Skip(40).Take(10)], -20, 15));
        host.Send(1, "hello.anchor", HelloGame.Move(0, 0, [.. mine.Skip(5).Take(4)]));
        host.Send(2, "hello.rights", [], OrderRights.Remote | OrderRights.Admin);
        host.Send(2, "hello.probe", MemoryPackSerializer.Serialize(new ProbeOrder { X = -40, Y = 30, Radius = 10, Entity = theirs[3] }));
        for (var tick = 0; tick < 300; tick++)
        {
            if (tick % 50 == 25)
                host.Send(2, "hello.move", HelloGame.Move(tick - 100, 60 - tick / 3.0, [.. theirs.Where((_, i) => i % 3 == tick % 3)]));
            if (tick == 120)
                host.Send(1, "hello.spawn", HelloGame.Spawn(50, 240, 240, spread: 40));
            host.Step();
            Collect();
        }
        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var count = host.Query("hello", "hello.count", BitConverter.GetBytes(1u)) ?? throw new InvalidOperationException("нет запроса");
        Assert.Empty(host.Errors);
        Assert.Empty(errors);
        return new Outcome(WorldSave.Save(host.World), host.Save(), signals, count, host.Scheduler.RandomState, ms);
    }

    private static byte[] Follow(uint leader, uint[] followers, double dx, double dy) =>
        MemoryPackSerializer.Serialize(new FollowOrder { Leader = leader, Followers = followers, Dx = dx, Dy = dy });

    [Fact]
    public void ClrAndWasmProduceIdenticalWorlds()
    {
        var clr = Play(ModMode.Clr);
        var wasm = Play(ModMode.Wasm);
        output.WriteLine($"CLR: {clr.Ms:F0} мс, wasm: {wasm.Ms:F0} мс; мир {clr.World.Length} байт, SHA-256 {Convert.ToHexStringLower(SHA256.HashData(clr.World))[..16]}…");
        Assert.Equal(wasm.Signals, clr.Signals);
        Assert.Equal(wasm.Random, clr.Random);
        Assert.Equal(wasm.Count, clr.Count);
        Assert.True(wasm.World.AsSpan().SequenceEqual(clr.World), "мир CLR и wasm разошёлся");
        Assert.True(wasm.Save.AsSpan().SequenceEqual(clr.Save), "сохранение CLR и wasm разошлось");
    }

    [Theory]
    [InlineData(ModMode.Clr)]
    [InlineData(ModMode.Wasm)]
    public void FollowersUseTablesStageAndWithout(ModMode mode)
    {
        using var host = HelloGame.Create(mode);
        host.Start();
        host.Join(1);
        host.Join(2);
        var mine = HelloGame.Units(host.World, 1);
        var theirs = HelloGame.Units(host.World, 2);
        host.TakeDelta();
        // Чужой юнит в ведомых — мимо; сцена видит своих ведомых с Follow сразу после записи.
        host.Send(1, "hello.follow", Follow(mine[0], [mine[1], mine[2], theirs[0]], 3, -2));
        host.Step();
        var staged = Assert.Single(host.TakeDelta().Signals, s => s.Name == "hello.staged");
        Assert.Equal([1u], staged.To!);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(staged.Payload));
        Assert.Equal(0, staged.Payload[4]);
        var follow = host.World.Schema.Id("Follow");
        var drive = host.World.Schema.Id("Drive");
        Assert.True(host.World.Has(mine[1], follow) && !host.World.Has(theirs[0], follow));

        // Кадр hello.follow: цель ведомого — место ведущего из побочной таблицы со сдвигом.
        host.Step();
        var (lx, ly) = HelloGame.Position(host.World, mine[0]);
        host.Step();
        Assert.Equal(lx + 3, host.World.Number(mine[1], drive, 2), 6);
        Assert.Equal(ly - 2, host.World.Number(mine[1], drive, 3), 6);

        // Without: закреплённого система не ведёт — его цель больше не тянется за ведущим.
        host.Send(1, "hello.anchor", HelloGame.Move(0, 0, mine[2]));
        host.Send(1, "hello.move", HelloGame.Move(-100, -100, mine[2]));
        for (var tick = 0; tick < 3; tick++)
            host.Step();
        Assert.Equal((-100.0, -100.0), (host.World.Number(mine[2], drive, 2), host.World.Number(mine[2], drive, 3)));

        // Новый ведущий в том же приказе: сцена видит его живым и на месте.
        host.TakeDelta();
        host.Send(1, "hello.follow", Follow(0, [mine[1]], 0, 0));
        host.Step();
        var spawned = Assert.Single(host.TakeDelta().Signals, s => s.Name == "hello.staged");
        Assert.Equal((1u, (byte)1), (BinaryPrimitives.ReadUInt32LittleEndian(spawned.Payload), spawned.Payload[4]));

        // Ведущего не стало — Follow снимается.
        var leader = (uint)host.World.Number(mine[1], follow, 0);
        host.World.Destroy(leader);
        host.Step();
        Assert.False(host.World.Has(mine[1], follow));
    }

    [Theory]
    [InlineData(ModMode.Clr)]
    [InlineData(ModMode.Wasm)]
    public void QueryReadsWorldWithoutCommands(ModMode mode)
    {
        using var host = HelloGame.Create(mode);
        host.Start();
        host.Join(1);
        host.Join(2);
        host.Send(1, "hello.spawn", HelloGame.Spawn(20, 5, 5));
        host.Step();
        var before = WorldSave.Save(host.World);
        var reply = host.Query("hello", "hello.count", BitConverter.GetBytes(1u))!;
        Assert.Equal(23u, BinaryPrimitives.ReadUInt32LittleEndian(reply));
        Assert.Equal(host.World.Revision, BinaryPrimitives.ReadUInt64LittleEndian(reply.AsSpan(4)));
        Assert.Equal(26u, BinaryPrimitives.ReadUInt32LittleEndian(host.Query("hello", "hello.count", [])!));
        Assert.Null(host.Query("hello", "hello.nope", []));
        Assert.Null(host.Query("nope", "hello.count", []));
        Assert.True(before.AsSpan().SequenceEqual(WorldSave.Save(host.World)));
    }

    [Theory]
    [InlineData(ModMode.Clr)]
    [InlineData(ModMode.Wasm)]
    public void OrderRightsReachMod(ModMode mode)
    {
        using var host = HelloGame.Create(mode);
        host.Start();
        host.Join(3);
        host.Send(3, "hello.rights", [], OrderRights.Remote | OrderRights.Editor);
        host.Send(3, "hello.rights", []);
        host.Step();
        var answers = host.TakeDelta().Signals.Where(s => s.Name == "hello.rights").ToList();
        Assert.Equal(2, answers.Count);
        Assert.All(answers, answer => Assert.Equal([3u], answer.To!));
        Assert.Equal((uint)(OrderRights.Remote | OrderRights.Editor), BinaryPrimitives.ReadUInt32LittleEndian(answers[0].Payload));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(answers[1].Payload));
    }

    [Fact]
    public void ClrInstancesOfTwoHostsAreIsolated()
    {
        // У каждого хоста — своя ALC и своя статика SDK: два хоста в одном процессе не мешают друг другу.
        using var a = HelloGame.Create(ModMode.Clr, seed: 1);
        using var b = HelloGame.Create(ModMode.Clr, seed: 2);
        a.Start();
        b.Start();
        a.Join(1);
        b.Join(1);
        b.Join(2);
        a.Send(1, "hello.spawn", HelloGame.Spawn(10, 0, 0));
        a.Step();
        b.Step();
        Assert.Equal(13, HelloGame.Units(a.World).Count);
        Assert.Equal(6, HelloGame.Units(b.World).Count);
        Assert.Equal(13u, BinaryPrimitives.ReadUInt32LittleEndian(a.Storage(a.Mods[0])));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(b.Storage(b.Mods[0])));
        Assert.Empty(a.Errors);
        Assert.Empty(b.Errors);
    }

    [Fact]
    public void ClrModeNeedsAssemblyOfModWithHostCode()
    {
        var package = ModPackage.Read(HelloGame.ModDir);
        var content = GameContent.Load([package]);
        var error = Assert.Throws<HostException>(() => new Host(content, new HostOptions
        {
            ModMode = ModMode.Clr,
            ClrAssemblies = new Dictionary<string, string> { ["hello"] = Path.Combine(HelloGame.ModDir, "nope.dll") },
        }));
        Assert.Contains("нет сборки мода", error.Message);
    }
}
