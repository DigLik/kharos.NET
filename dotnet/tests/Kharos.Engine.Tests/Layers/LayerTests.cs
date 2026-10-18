using Kharos.Engine.Layers;

namespace Kharos.Engine.Tests.Layers;

public sealed class LayerTests
{
    private const string Specs = """
        {
          "land": { "scope": "world", "shape": "grid", "channels": ["u8", "u8", "u8", "u8"], "save": true, "remember": true },
          "paving": { "scope": "world", "shape": "grid", "channels": ["u16"], "save": true },
          "sight": { "scope": "player", "shape": "grid", "channels": ["u8"], "allies": true },
          "explored": { "scope": "player", "shape": "grid", "channels": ["u8"], "save": true },
          "traces": { "scope": "world", "shape": "items", "cell": 16, "fields": { "kind": "u8", "tick": "u32" } }
        }
        """;

    private static readonly LayerOwner World = LayerOwner.World;

    private static LayerSet Make() => new(LayerSpec.ParseAll(Specs));

    [Fact]
    public void DeclarationsParseAndCheck()
    {
        var specs = LayerSpec.ParseAll(Specs);
        Assert.Equal(["land", "paving", "sight", "explored", "traces"], specs.Select(spec => spec.Name));
        Assert.Equal(4, specs[0].Stride);
        Assert.Equal(16 + 1 + 4, specs[4].Stride);
        Assert.Throws<FormatException>(() => LayerSpec.ParseAll("""{ "a": { "scope": "player", "shape": "grid", "channels": ["u8"], "remember": true } }"""));
        Assert.Throws<FormatException>(() => LayerSpec.ParseAll("""{ "a": { "scope": "world", "shape": "grid", "channels": ["u8"], "typo": 1 } }"""));
        Assert.Throws<FormatException>(() => LayerSpec.ParseAll("""{ "a": { "scope": "world", "shape": "grid", "channels": ["u7"] } }"""));
        Assert.Throws<FormatException>(() => LayerSpec.ParseAll("""{ "a": { "scope": "world", "shape": "items" } }"""));
        Assert.Throws<FormatException>(() => LayerSpec.ParseAll("""{ "a": { "scope": "world", "shape": "grid", "channels": ["u8"], "allies": true } }"""));
        Assert.Throws<FormatException>(() => LayerSpec.ParseAll("""{ "a": { "scope": "team", "shape": "grid", "channels": ["u8"] } }"""));
    }

    [Fact]
    public void BaseLayersParse()
    {
        var specs = LayerSpec.ParseAll(File.ReadAllText(Repo.File("packages/base/data/layers.json")));
        Assert.Equal(["land", "paving", "occupancy", "sight", "explored", "deposits", "traces"], specs.Select(spec => spec.Name));
        var byName = specs.ToDictionary(spec => spec.Name);
        Assert.Equal([Channel.U8, Channel.U8, Channel.U8, Channel.U8], byName["land"].Channels);
        Assert.True(byName["land"] is { Save: true, Remember: true, Send: true, Scope: LayerScope.World });
        Assert.True(byName["occupancy"] is { Save: false, Send: false });
        Assert.True(byName["sight"] is { Scope: LayerScope.Player, Allies: true, Save: false });
        Assert.True(byName["explored"] is { Scope: LayerScope.Player, Save: true });
        Assert.Equal([("kind", Channel.U8), ("reserve", Channel.F64)], byName["deposits"].Fields);
        Assert.Equal(40, byName["deposits"].Cell);
        Assert.Equal(["id", "tick", "kind", "entity", "type", "facing", "player", "size"], byName["traces"].Fields.Select(field => field.Name));
        Assert.Equal(16 + 4 + 4 + 1 + 4 + 2 + 8 + 2 + 8, byName["traces"].Stride);
        _ = new LayerSet(specs);
    }

    [Fact]
    public void GridKeepsTypedChannelsAndAllocatesLazily()
    {
        var layers = Make();
        var (land, paving) = (layers.Id("land"), layers.Id("paving"));
        layers.Set(paving, World, 5, 5, 0, 0);
        Assert.Empty(layers.TakeChanges());
        layers.Set(land, World, -1, -33, 2, 7);
        layers.Set(paving, World, 40, 3, 0, 1000);
        Assert.Equal(7.0, layers.Get(land, World, -1, -33, 2));
        Assert.Equal(0.0, layers.Get(land, World, -1, -33, 1));
        Assert.Equal(1000.0, layers.Get(paving, World, 40, 3, 0));
        var changes = layers.TakeChanges();
        Assert.Equal([("land", -1, -2), ("paving", 1, 0)], changes.Select(change => (change.Layer, change.X, change.Y)));
        layers.Set(land, World, -1, -33, 2, 7);
        Assert.Empty(layers.TakeChanges());
    }

    [Fact]
    public void ChannelsSaturateLikeRustCasts()
    {
        var layers = new LayerSet(LayerSpec.ParseAll("""{ "g": { "scope": "world", "shape": "grid", "channels": ["u8", "u16", "u32", "f32", "f64"] } }"""));
        var g = layers.Id("g");
        double[] values = [300.7, -5, 1e12, 0.1, 0.1];
        double[] expected = [255, 0, uint.MaxValue, (float)0.1, 0.1];
        for (var channel = 0; channel < values.Length; channel++)
        {
            layers.Set(g, World, 3, 4, channel, values[channel]);
            Assert.Equal(expected[channel], layers.Get(g, World, 3, 4, channel));
        }
        layers.Set(g, World, 3, 4, 0, double.NaN);
        Assert.Equal(0.0, layers.Get(g, World, 3, 4, 0));
        layers.Set(g, World, 3, 4, 0, 7.9);
        Assert.Equal(7.0, layers.Get(g, World, 3, 4, 0));
        Assert.Equal(LayerSet.ChunkTiles * (1 + 2 + 4 + 4 + 8), layers.ChunkBytes(g, World, 0, 0).Length);
        Assert.True(layers.ChunkBytes(g, World, 1, 0).IsEmpty);
    }

    [Fact]
    public void ChunkOfHandlesNegatives()
    {
        Assert.Equal(((0, 0), 0), LayerSet.ChunkOf(0, 0));
        Assert.Equal(((-1, -1), 31 * 32 + 31), LayerSet.ChunkOf(-1, -1));
        Assert.Equal(((-2, 1), 5 * 32 + 31), LayerSet.ChunkOf(-33, 37));
    }

    [Fact]
    public void PlayerLayersLiveAndDieWithThePlayer()
    {
        var layers = Make();
        var explored = layers.Id("explored");
        layers.Set(explored, LayerOwner.Player(1), 0, 0, 0, 1);
        Assert.Equal(0.0, layers.Get(explored, LayerOwner.Player(1), 0, 0, 0));
        layers.AddPlayer(1);
        layers.AddPlayer(2);
        layers.Set(explored, LayerOwner.Player(1), 0, 0, 0, 1);
        Assert.Equal(1.0, layers.Get(explored, LayerOwner.Player(1), 0, 0, 0));
        Assert.Equal(0.0, layers.Get(explored, LayerOwner.Player(2), 0, 0, 0));
        Assert.Single(layers.Save(LayerOwner.Player(1)));
        layers.TakeChanges();
        layers.ForgetPlayer(1);
        Assert.Equal([2u], layers.Players());
        var gone = Assert.Single(layers.TakeChanges());
        Assert.Empty(gone.Bytes);
    }

    [Fact]
    public void OwnersMustMatchTheScope()
    {
        var layers = Make();
        layers.AddPlayer(1);
        var (land, sight) = (layers.Id("land"), layers.Id("sight"));
        layers.Set(sight, World, 0, 0, 0, 1);
        layers.Set(land, LayerOwner.Player(1), 0, 0, 0, 1);
        Assert.Empty(layers.TakeChanges());
        var chunk = new byte[LayerSet.ChunkTiles];
        Assert.Throws<FormatException>(() => layers.Apply(new LayerChange("sight", World, 0, 0, chunk)));
        Assert.Throws<FormatException>(() => layers.Apply(new LayerChange("land", LayerOwner.Player(1), 0, 0, new byte[LayerSet.ChunkTiles * 4])));
        Assert.Throws<FormatException>(() => layers.Apply(new LayerChange("land", World, 0, 0, chunk)));
        Assert.Throws<FormatException>(() => layers.Apply(new LayerChange("nope", World, 0, 0, chunk)));
        layers.Apply(new LayerChange("sight", LayerOwner.Player(7), 0, 0, chunk));
        Assert.Contains(7u, layers.Players());
    }

    [Fact]
    public void ItemsSitInCells()
    {
        var layers = Make();
        var traces = layers.Id("traces");
        layers.Push(traces, World, new LayerItem(3.5, -0.25, [2, 90]));
        layers.Push(traces, World, new LayerItem(4, -1, [1, 100]));
        var cell = layers.CellOf(traces, 3.5, -0.25);
        Assert.Equal((0, -1), cell);
        Assert.Equal(2, layers.Items(traces, World, cell).Count);
        layers.Retain(traces, World, cell, item => item.Fields[1] > 95);
        Assert.Equal([new LayerItem(4, -1, [1, 100])], layers.Items(traces, World, cell));
        Assert.Throws<InvalidOperationException>(() => layers.CellOf(layers.Id("land"), 0, 0));
    }

    [Fact]
    public void ChangesRebuildTheSameLayersThroughBytes()
    {
        var layers = Make();
        layers.AddPlayer(3);
        var (land, explored, traces) = (layers.Id("land"), layers.Id("explored"), layers.Id("traces"));
        for (var i = 0; i < 50; i++)
        {
            layers.Set(land, World, i * 7 - 100, i * 3, i % 4, i);
            layers.Set(explored, LayerOwner.Player(3), i, -i, 0, 1);
        }
        layers.Push(traces, World, new LayerItem(1, 2, [3, 4]));
        var copy = Make();
        foreach (var change in LayerCodec.Decode(LayerCodec.Encode(layers.TakeChanges())))
            copy.Apply(change);
        Assert.Empty(copy.TakeChanges());
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(i, copy.Get(land, World, i * 7 - 100, i * 3, i % 4));
            Assert.Equal(1.0, copy.Get(explored, LayerOwner.Player(3), i, -i, 0));
        }
        Assert.Equal(layers.Save(World), copy.Save(World));
        Assert.Equal(layers.Save(LayerOwner.Player(3)), copy.Save(LayerOwner.Player(3)));
        Assert.Equal(layers.Items(traces, World, (0, 0)), copy.Items(traces, World, (0, 0)));
    }

    [Fact]
    public void SaveSkipsUnsavedLayersAndLoadsBack()
    {
        var layers = Make();
        layers.AddPlayer(1);
        var (land, sight, explored, traces) = (layers.Id("land"), layers.Id("sight"), layers.Id("explored"), layers.Id("traces"));
        layers.Set(land, World, 100, 100, 0, 3);
        layers.Push(traces, World, new LayerItem(0, 0, [1, 1]));
        layers.Set(sight, LayerOwner.Player(1), 0, 0, 0, 1);
        layers.Set(explored, LayerOwner.Player(1), 0, 0, 0, 1);
        var world = layers.Save(World);
        Assert.Equal(["land"], world.Select(change => change.Layer));
        var player = layers.Save(LayerOwner.Player(1));
        Assert.Equal(["explored"], player.Select(change => change.Layer));
        var loaded = Make();
        foreach (var change in LayerCodec.Decode(LayerCodec.Encode([.. world, .. player])))
            loaded.Apply(change);
        Assert.Equal(3.0, loaded.Get(land, World, 100, 100, 0));
        Assert.Equal(1.0, loaded.Get(explored, LayerOwner.Player(1), 0, 0, 0));
        Assert.Equal(0.0, loaded.Get(sight, LayerOwner.Player(1), 0, 0, 0));
        Assert.Equal([(3, 3)], loaded.Cells(land, World));
    }

    [Fact]
    public void CodecLayoutIsLittleEndianAndRejectsGarbage()
    {
        var bytes = LayerCodec.Encode([new LayerChange("ab", LayerOwner.Player(2), -1, 5, [9])]);
        Assert.Equal([1, 0, 0, 0, 2, (byte)'a', (byte)'b', 2, 0, 0, 0, 255, 255, 255, 255, 5, 0, 0, 0, 1, 0, 0, 0, 9], bytes);
        Assert.Equal(World, LayerCodec.Decode(LayerCodec.Encode([new LayerChange("x", World, 0, 0, [])]))[0].Owner);
        for (var cut = 0; cut < bytes.Length; cut++)
            Assert.Throws<FormatException>(() => LayerCodec.Decode(bytes.AsSpan(0, cut)));
        Assert.Throws<FormatException>(() => LayerCodec.Decode([255, 255, 255, 255]));
        Assert.Empty(LayerCodec.Decode([0, 0, 0, 0]));
    }
}
