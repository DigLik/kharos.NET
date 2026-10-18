using System.Buffers.Binary;
using Hello;
using Kharos.Plugins.Game;
using MemoryPack;

namespace Kharos.Plugins.Tests;

/// <summary>
/// Режим CLR без хоста (docs/dotnet-n5.md, Н5-5): сборка Hello.Host.dll в своей ALC отвечает так же, как host.wasm, —
/// тот же манифест, те же кадры и те же байты команд.
/// </summary>
public class ClrModuleTests
{
    private static string Assembly
    {
        get
        {
            var path = ClrModule.Find(Path.Combine(HelloModule.DotnetRoot, "mods", "hello"));
            Assert.SkipWhen(path is null, "Hello.Host.dll не собран: dotnet build dotnet/Kharos.slnx");
            return path!;
        }
    }

    [Fact]
    public void LoadsWithSameManifestAsWasm()
    {
        var module = new ClrModule("hello", Assembly, HelloModule.LoadInfo());
        using var clr = module.CreateInstance();
        Assert.Equal(4, clr.Manifest.Systems.Length);
        Assert.Equal(["hello.count"], clr.Manifest.Queries);
        Assert.Equal(HelloModule.Width, clr.Systems[0].Width);
        Assert.Equal([new ModTableLayout(3, true)], clr.Systems[3].Tables);
        Assert.False(clr.Faulted);

        Assert.SkipUnless(HelloModule.Built, HelloModule.NotBuilt);
        using var host = new GameModuleHost();
        using var wasm = host.LoadFile("hello", HelloModule.WasmPath, HelloModule.LoadInfo()).CreateInstance();
        Assert.Equal(MemoryPackSerializer.Serialize(wasm.Manifest), MemoryPackSerializer.Serialize(clr.Manifest));
    }

    [Fact]
    public void DriveSystemAndOrdersMatchWasmByteForByte()
    {
        Assert.SkipUnless(HelloModule.Built, HelloModule.NotBuilt);
        using var host = new GameModuleHost(new GameModuleOptions { CallTimeoutMs = 1000 });
        using var wasm = host.LoadFile("hello", HelloModule.WasmPath, HelloModule.LoadInfo(seed: 5)).CreateInstance();
        using var clr = new ClrModule("hello", Assembly, HelloModule.LoadInfo(seed: 5)).CreateInstance();
        // Один источник случайного у обоих — как у мира.
        var (a, b) = (new ModRandom(9), new ModRandom(9));
        wasm.RandomSource = a.Next;
        clr.RandomSource = b.Next;

        const int n = 1500;
        var (entities, wasmData) = HelloModule.Units(n, seed: 3);
        var clrData = (double[])wasmData.Clone();
        for (ulong tick = 0; tick < 200; tick++)
        {
            var call = new ModCall(tick, 0.05, n + 1);
            var fromWasm = wasm.RunSystem(0, new ArrayFrame((entities, wasmData)), call).ToArray();
            var fromClr = clr.RunSystem(0, new ArrayFrame((entities, clrData)), call).ToArray();
            Assert.Equal(fromWasm, fromClr);
        }
        Assert.True(wasmData.AsSpan().SequenceEqual(clrData), "столбцы CLR и wasm разошлись");

        // Приказ: случайный разброс, права — в общем блоке и в мосте одинаково.
        var spawn = MemoryPackSerializer.Serialize(new SpawnOrder { Count = 5, X = 3, Y = 4, Spread = 10 });
        var order = new ModCall(7, 0.05, 900, 1u /* Remote */);
        var spawnId = Array.IndexOf(clr.Manifest.Orders, "hello.spawn");
        Assert.Equal(wasm.RunOrder(spawnId, 4, spawn, order).ToArray(), clr.RunOrder(spawnId, 4, spawn, order).ToArray());
        var rights = Array.IndexOf(clr.Manifest.Orders, "hello.rights");
        var answer = ModCommands.Decode(clr.RunOrder(rights, 4, [], order)).Single();
        Assert.Equal((ModCommandOp.SignalTo, "hello.rights", 1u /* Remote */), (answer.Op, answer.Name, BinaryPrimitives.ReadUInt32LittleEndian(answer.Payload)));
        Assert.Equal([4u], answer.Players!);
    }

    [Fact]
    public void ModErrorsComeBackAsCodes()
    {
        using var clr = new ClrModule("hello", Assembly, HelloModule.LoadInfo()).CreateInstance();
        var fail = Array.IndexOf(clr.Manifest.Orders, "hello.fail");
        var error = Assert.Throws<ModCallException>(() => clr.RunOrder(fail, 1, [], new ModCall(1, 0.05, 1)));
        Assert.Equal(-5, error.Code);
        Assert.Contains("приказ отказан", error.Message);
        Assert.Throws<ModCallException>(() => clr.RunOrder(999, 1, [], new ModCall(1, 0.05, 1)));
        // Без инструментов движка запрос не ответит — ошибка кодом, экземпляр жив.
        Assert.Throws<ModCallException>(() => clr.RunQuery(0, [], new ModCall(1, 0.05, 1)));
        Assert.False(clr.Faulted);
    }
}
