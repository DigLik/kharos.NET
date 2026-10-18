using Kharos.Mod.Sdk;
using Kharos.Plugins.Game;

namespace Kharos.Plugins.Tests;

/// <summary>Буфер команд SDK — байт в байт формат движка; разбор в Kharos.Plugins. Без wasm.</summary>
public class CommandBufferTests
{
    private static readonly ComponentRef Drive = new("Drive", 5,
    [
        new FieldRef("speed", FieldKind.Number, 0, 0),
        new FieldRef("moving", FieldKind.Flag, 1, 1),
        new FieldRef("move", FieldKind.String, 2, 2),
        new FieldRef("queue", FieldKind.Bytes, 3, 0),
    ]);

    [Fact]
    public void WriterProducesEngineFormatBytes()
    {
        var writer = new CommandWriter(nextEntity: 300);
        var entity = writer.Spawn();
        writer.Add(entity, Drive).Number(Drive["speed"], 1.5).Flag(Drive["moving"], true);
        writer.Set(7, Drive).TextId(Drive["move"], 3).Text(Drive["move"], "wheels").Bytes(Drive["queue"], [9, 8]);
        writer.Remove(7, Drive);
        writer.Destroy(7);
        writer.Emit("e"u8, [1, 2]);
        writer.Signal("s", []);

        byte[] expected =
        [
            0x01, 0xAC, 0x02,                                     // Spawn 300 (LEB128)
            0x03, 0xAC, 0x02, 0x05, 0x02,                         // Add 300 Drive, 2 поля
            0x00, 0x00, 0, 0, 0, 0, 0, 0, 0xF8, 0x3F,             //   speed = 1.5
            0x01, 0x02,                                           //   moving = true
            0x04, 0x07, 0x05, 0x03,                               // Set 7 Drive, 3 поля
            0x02, 0x03, 0x03,                                     //   move = строка №3
            0x02, 0x04, 0x06, (byte)'w', (byte)'h', (byte)'e', (byte)'e', (byte)'l', (byte)'s',
            0x03, 0x05, 0x02, 9, 8,                               //   queue = байты
            0x05, 0x07, 0x05,                                     // Remove 7 Drive
            0x02, 0x07,                                           // Destroy 7
            0x06, 0x01, (byte)'e', 0x02, 1, 2,                    // Emit "e" [1, 2]
            0x07, 0x01, (byte)'s', 0x00,                          // Signal "s" []
        ];
        Assert.Equal(expected, writer.Written.ToArray());
        Assert.Equal(301u, writer.Spawn());
    }

    [Fact]
    public void DecoderReadsWhatWriterWrote()
    {
        var writer = new CommandWriter(nextEntity: 10);
        var a = writer.Spawn();
        writer.Add(a, Drive).Number(Drive["speed"], -2.25);
        writer.Emit("hello.arrived", [1, 2, 3]);

        var commands = ModCommands.Decode(writer.Written);

        Assert.Equal(3, commands.Count);
        Assert.Equal((ModCommandOp.Spawn, 10u), (commands[0].Op, commands[0].Entity));
        Assert.Equal(ModCommandOp.Add, commands[1].Op);
        Assert.Equal(5, commands[1].Component);
        Assert.Equal(-2.25, Assert.Single(commands[1].Fields).Number);
        Assert.Equal("hello.arrived", commands[2].Name);
        Assert.Equal([1, 2, 3], commands[2].Payload);
        Assert.Equal(3, ModCommands.Count(writer.Written));
    }

    [Fact]
    public void DecoderRejectsBrokenBuffers()
    {
        Assert.Throws<ModCommandFormatException>(() => ModCommands.Decode([0x0D]));
        Assert.Throws<ModCommandFormatException>(() => ModCommands.Decode([0x09, 0x00]));
        Assert.Throws<ModCommandFormatException>(() => ModCommands.Decode([0x03, 0x01, 0x05, 0x01, 0x00, 0x00, 0x00]));
        Assert.Throws<ModCommandFormatException>(() => ModCommands.Decode([0x06, 0x05, (byte)'a']));
        Assert.Throws<ModCommandFormatException>(() => ModCommands.Decode([0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F]));
    }

    [Fact]
    public void N5RecordsRoundTrip()
    {
        var grid = new LayerRef("paving", 3, false, 0, 2, []);
        var items = new LayerRef("deposits", 4, false, 8, 0, ["kind", "amount"]);
        var writer = new CommandWriter();
        writer.Layer(grid, 0, 1, -5, 7, 2, 3, 0.5);
        writer.PushItem(items, 2, 1.5, -2.5, [3, 40]);
        writer.DeleteItems(items, 2, 1.5, -2.5);
        writer.LandTile(-1, 2, new LandEdit(1, 2, 3, Cliff: true, Dunes: 2));
        writer.LandPeak(4, -4, 1, 4.5, -3.5, 2.25);
        writer.SignalTo("note", [9], [1, 300]);
        writer.SignalAt("boom", [], 10.25, -0.5);

        var commands = ModCommands.Decode(writer.Written);
        Assert.Equal([ModCommandOp.Layer, ModCommandOp.Item, ModCommandOp.Item, ModCommandOp.Land, ModCommandOp.Land, ModCommandOp.SignalTo, ModCommandOp.SignalAt], commands.Select(c => c.Op));
        var layer = commands[0];
        Assert.Equal((3, 0u, (byte)1, -5.0, 7.0, 2u, 3u, 0.5), (layer.Layer, layer.Owner, layer.Mode, layer.X, layer.Y, layer.W, layer.H, Assert.Single(layer.Values)));
        Assert.Equal((4, 2u, (byte)0, 1.5, -2.5), (commands[1].Layer, commands[1].Owner, commands[1].Mode, commands[1].X, commands[1].Y));
        Assert.Equal([3.0, 40], commands[1].Values);
        Assert.Equal(((byte)1, 1.5), (commands[2].Mode, commands[2].X));
        Assert.Equal([1, 2, 3, 1, 2], commands[3].Tile);
        Assert.Equal((-1.0, 2.0), (commands[3].X, commands[3].Y));
        Assert.Equal([4.5, -3.5, 2.25], commands[4].Values);
        Assert.Equal("note", commands[5].Name);
        Assert.Equal([1u, 300u], commands[5].Players!);
        Assert.Equal(("boom", 10.25, -0.5), (commands[6].Name, commands[6].X, commands[6].Y));
    }

    [Fact]
    public void ValueRequiresAddOrSet()
    {
        var writer = new CommandWriter();
        writer.Destroy(1);
        Assert.Throws<InvalidOperationException>(() => writer.Number(Drive["speed"], 1));
    }
}
