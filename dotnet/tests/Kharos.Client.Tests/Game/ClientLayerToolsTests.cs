using System.Buffers.Binary;
using System.Text;
using Kharos.Client.Game;
using Kharos.Client.Ui;
using Kharos.Content;
using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Host;
using Kharos.Protocol;
using HostGame = Kharos.Host.Host;

namespace Kharos.Client.Tests.Game;

/// <summary>
/// Инструменты Layer, Items и Peak у клиентских модов (Д3): отвечают по реплике — слоям, что прислал хост, и карте с
/// правками хоста. Партия без модов: слои и землю правит тест напрямую.
/// </summary>
public sealed class ClientLayerToolsTests : IDisposable
{
    private const int ToolLayer = 18, ToolItems = 19, ToolPeak = 20;

    private const string Layers = """
        {
          "heat": { "scope": "world", "shape": "grid", "channels": ["u8", "f32"] },
          "marks": { "scope": "world", "shape": "items", "cell": 8, "fields": { "kind": "u8", "power": "f64" } },
          "notes": { "scope": "player", "shape": "grid", "channels": ["u8"] },
          "land": { "scope": "world", "shape": "grid", "channels": ["u8", "u8", "u8", "u8"], "remember": true }
        }
        """;

    private readonly Fonts _fonts = Fonts.Load();
    private readonly HostGame _host;
    private readonly HostSession _session;
    private readonly MemoryTransport _end;
    private readonly Replica _replica = new();
    private readonly ClientTools _tools;
    private readonly List<string> _log = [];

    public ClientLayerToolsTests()
    {
        var lab = new ModPackage("lab", Encoding.UTF8.GetBytes("""{ "name": "lab", "game": "lab" }"""), new Dictionary<string, byte[]>
        {
            ["games"] = Encoding.UTF8.GetBytes("""{ "lab": { "size": 64, "fog": false, "land": "flat" } }"""),
            ["lands"] = Encoding.UTF8.GetBytes("""{ "flat": { "seed": 3, "blank": true } }"""),
            ["layers"] = Encoding.UTF8.GetBytes(Layers),
        });
        var content = GameContent.Load([lab]);
        _host = new HostGame(content, new HostOptions { Seed = 1 });
        _host.Start();
        _session = new HostSession(_host, new HostSessionOptions { SendRate = 0 });
        (var hostEnd, _end) = MemoryTransport.Pair();
        _session.Connect(hostEnd, 1);
        Pump();
        _tools = new ClientTools(_replica, content, () => _replica.Terrain, new Poses(), _fonts) { Log = _log.Add };
    }

    public void Dispose()
    {
        _end.Dispose();
        _host.Dispose();
        _fonts.Dispose();
    }

    private void Pump() => _replica.Pump(_end);

    private void Tick()
    {
        _session.Step();
        Pump();
    }

    private static byte[] Input(params object[] values)
    {
        var bytes = new List<byte>();
        foreach (var value in values)
            bytes.AddRange(value switch
            {
                uint u => BitConverter.GetBytes(u),
                int i => BitConverter.GetBytes(i),
                double d => BitConverter.GetBytes(d),
                _ => throw new ArgumentException(value.GetType().Name),
            });
        return [.. bytes];
    }

    private byte[] Call(int id, byte[] input)
    {
        var output = new byte[4096];
        var length = _tools.Call(id, input, output);
        Assert.True(length >= 0, $"инструмент {id}: код {length}; {string.Join(" | ", _log)}");
        return output[..length];
    }

    private static double[] Doubles(byte[] bytes, int from = 0) =>
        [.. Enumerable.Range(0, (bytes.Length - from) / 8).Select(i => BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(from + i * 8)))];

    [Fact]
    public void LayerToolReadsGridsThatHostSent()
    {
        var heat = _host.Layers.Id("heat");
        _host.Layers.Set(heat, LayerOwner.World, 3, -2, 0, 40);
        _host.Layers.Set(heat, LayerOwner.World, 4, -2, 1, 0.5);
        _host.Layers.Set(_host.Layers.Id("notes"), LayerOwner.Player(1), 3, -2, 0, 9);
        Tick();
        Assert.Equal([40.0, 0], Doubles(Call(ToolLayer, Input((uint)_replica.Layers!.Id("heat"), 0u, 0u, 3, -2, 2u, 1u))));
        Assert.Equal([0.0, 0.5], Doubles(Call(ToolLayer, Input((uint)_replica.Layers.Id("heat"), 0u, 1u, 3, -2, 2u, 1u))));
        // Слой игрока — свой (владелец — номер игрока реплики); чужой у клиента не заведён — нули.
        var notes = (uint)_replica.Layers.Id("notes");
        Assert.Equal([9.0], Doubles(Call(ToolLayer, Input(notes, _replica.Player, 0u, 3, -2, 1u, 1u))));
        Assert.Equal([0.0], Doubles(Call(ToolLayer, Input(notes, 7u, 0u, 3, -2, 1u, 1u))));
        Assert.Equal(-4, _tools.Call(ToolLayer, Input(notes, 0u, 0u, 3, -2, 1u, 1u), new byte[16]));
    }

    [Fact]
    public void ItemsToolSeesItemsThatArrivedAfterwards()
    {
        var marks = _host.Layers.Id("marks");
        _host.Layers.Push(marks, LayerOwner.World, new LayerItem(10, 10, [1, 5]));
        Tick();
        var id = (uint)_replica.Layers!.Id("marks");
        Assert.Equal([10.0, 10, 1, 5], Doubles(Call(ToolItems, Input(id, 0u, 9.0, 9.0, 3.0)), 4));
        _host.Layers.Push(marks, LayerOwner.World, new LayerItem(11, 9, [2, 6]));
        Tick();
        var output = Call(ToolItems, Input(id, 0u, 9.0, 9.0, 3.0));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(output));
        Assert.Equal([10.0, 10, 1, 5, 11, 9, 2, 6], Doubles(output, 4));
    }

    [Fact]
    public void LandLayerFollowsEditsThatHostSent()
    {
        // Слой «land» читается из карты клиента: каналы — хранимые байты тайла; правки хоста ложатся на неё.
        var land = (uint)_replica.Layers!.Id("land");
        Assert.Equal((double)TerrainType.Sand, Doubles(Call(ToolLayer, Input(land, 0u, 0u, 20, 20, 1u, 1u)))[0]);
        _host.Land!.SetTile(20, 20, new TileEdit(TerrainType.Rock, 0, 1, false));
        Tick();
        var stored = _host.Land.Stored(20, 20);
        Assert.Equal([(double)TerrainType.Rock, stored.Biome, stored.Peak, stored.Relief], Enumerable.Range(0, 4).Select(channel => Doubles(Call(ToolLayer, Input(land, 0u, (uint)channel, 20, 20, 1u, 1u)))[0]));
        Assert.Equal(stored, _replica.Terrain!.Stored(20, 20));
        Assert.Equal(-4, _tools.Call(ToolLayer, Input(77u, 0u, 0u, 0, 0, 1u, 1u), new byte[16]));
    }

    [Fact]
    public void PeakToolFollowsLandEditsThatHostSent()
    {
        Assert.Equal(new byte[25], Call(ToolPeak, Input(1u, 20, 20)));
        _host.Land!.SetPeakTile(20, 20, 1, 20.5, 21, 2);
        Tick();
        var output = Call(ToolPeak, Input(1u, 20, 20));
        var peak = _host.Land.PeakOf(20, 20)!.Value;
        Assert.Equal(1, output[0]);
        Assert.Equal([peak.X, peak.Y, peak.Radius], Doubles(output[1..]));
    }

    [Fact]
    public void ClientOwnToolsLiveFrom40AndHostToolsAreNotShadowed()
    {
        // 40 Each (свой) отвечает списком; 41–51 — свои (48–51 — ABI 2, ClientAbiTests); 52 — нет такого; хостовые 18–20
        // отвечают из реплики (тесты выше).
        Assert.Equal(4, Call(40, Input(0u)).Length);
        Assert.Equal(-1, _tools.Call(52, [], new byte[16]));
        Assert.Equal(25, Call(ToolPeak, Input(1u, 0, 0)).Length);
    }
}
