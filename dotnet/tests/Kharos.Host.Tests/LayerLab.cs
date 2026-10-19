using System.Text;
using System.Text.Json;
using Hello;
using Kharos.Content;
using Kharos.Plugins.Game;
using Kharos.Protocol;
using MemoryPack;

namespace Kharos.Host.Tests;

/// <summary>
/// Стенд слоёв (Д3): мод hello и мод без кода «lab», который объявляет слои для приказов hello.layer, hello.item,
/// hello.read: сетка мира scorch (u8, f32), предметы marks и deposits (как у base), слой игрока notes, запоминаемое покрытие paving и следы traces.
/// </summary>
public static class LayerLab
{
    private const string Layers = """
        {
          "scorch": { "scope": "world", "shape": "grid", "channels": ["u8", "f32"], "save": true },
          "marks": { "scope": "world", "shape": "items", "cell": 8, "fields": { "kind": "u8", "power": "f64" }, "save": true },
          "notes": { "scope": "player", "shape": "grid", "channels": ["u8"], "save": true },
          "deposits": { "scope": "world", "shape": "items", "cell": 40, "fields": { "kind": "u8", "reserve": "f64" }, "save": true, "remember": true },
          "paving": { "scope": "world", "shape": "grid", "channels": ["u16"], "save": true, "remember": true },
          "traces": { "scope": "world", "shape": "items", "cell": 16, "sight": true, "fields": { "id": "u32", "tick": "u32", "kind": "u8", "entity": "u32", "type": "u16", "facing": "f64", "player": "u16", "size": "f64" } }
        }
        """;

    /// <summary>Содержимое: hello и lab поверх.</summary>
    public static GameContent Content()
    {
        var lab = new ModPackage("lab", Encoding.UTF8.GetBytes("""{ "name": "lab", "requires": ["hello"] }"""),
            new Dictionary<string, byte[]> { ["layers"] = Encoding.UTF8.GetBytes(Layers) });
        return GameContent.Load([ModPackage.Read(HelloGame.ModDir), lab]);
    }

    /// <summary>Хост стенда в нужном режиме (пропуск, если мод не собран); settings — поверх games.json.</summary>
    public static Host Create(ModMode mode, string? settings = null, ulong seed = 1)
    {
        if (mode == ModMode.Wasm)
            Assert.SkipUnless(HelloGame.Built, HelloGame.NotBuilt);
        else
            Assert.SkipWhen(ClrModule.Find(HelloGame.ModDir) is null, "Hello.Host.dll не собран: dotnet build dotnet/Kharos.slnx");
        return new Host(Content(), Options(mode, settings, seed));
    }

    public static HostOptions Options(ModMode mode, string? settings = null, ulong seed = 1) => new()
    {
        Modules = new GameModuleOptions { CallTimeoutMs = 1000, MaxStrikes = 50 },
        Seed = seed,
        ModMode = mode,
        Settings = settings is null ? null : JsonDocument.Parse(settings).RootElement.Clone(),
    };

    public static byte[] Layer(string layer, int channel, int x, int y, int w, int h, double value, uint owner = 0) =>
        MemoryPackSerializer.Serialize(new LayerOrder { Layer = layer, Owner = owner, Channel = channel, X = x, Y = y, W = w, H = h, Value = value });

    public static byte[] Item(string layer, double x, double y, double[] fields, uint owner = 0, bool delete = false) =>
        MemoryPackSerializer.Serialize(new ItemOrder { Layer = layer, Owner = owner, X = x, Y = y, Fields = fields, Delete = delete });

    public static byte[] Tile(int x, int y, byte terrain, byte tier = 1, byte biome = 0, bool cliff = false) =>
        MemoryPackSerializer.Serialize(new LandOrder { X = x, Y = y, Terrain = terrain, Tier = tier, Biome = biome, Cliff = cliff });

    public static byte[] Peak(int x, int y, byte biome, double peakX, double peakY, double radius) =>
        MemoryPackSerializer.Serialize(new LandOrder { Peak = true, X = x, Y = y, Biome = biome, PeakX = peakX, PeakY = peakY, Radius = radius });

    /// <summary>Приказ hello.read; ответ приходит сигналом (<see cref="ReadAnswer"/>).</summary>
    public static byte[] Read(ReadOrder order) => MemoryPackSerializer.Serialize(order);

    /// <summary>Переносит юниты hello в точку (x, y) и там их оставляет: место и цель ходовой (Object.assign в тестах TS).</summary>
    public static void Teleport(Host host, IEnumerable<uint> units, double x, double y)
    {
        var world = host.World;
        var drive = world.Schema.Id("Drive");
        foreach (var unit in units)
        {
            world.SetNumber(unit, Kharos.Engine.World.Builtin.Position, 0, x);
            world.SetNumber(unit, Kharos.Engine.World.Builtin.Position, 1, y);
            world.Set(unit, drive, "tx", Kharos.Engine.World.FieldValue.Of(x));
            world.Set(unit, drive, "ty", Kharos.Engine.World.FieldValue.Of(y));
        }
    }

    /// <summary>Ответ hello.read из дельты: сигнал адресован игроку.</summary>
    public static ReadReport ReadAnswer(WorldDelta delta, uint player)
    {
        var signal = Assert.Single(delta.Signals, s => s.Name == "hello.read" && s.To is { } to && to.SequenceEqual([player]));
        return MemoryPackSerializer.Deserialize<ReadReport>(signal.Payload)!;
    }
}
