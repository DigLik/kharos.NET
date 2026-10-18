using System.Buffers.Binary;
using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Engine.Tests.World;
using Kharos.Engine.Tools;
using Kharos.Engine.World;

namespace Kharos.Engine.Tests.Layers;

/// <summary>Инструменты движка 18 Layer, 19 Items и 20 Peak (Д3): слои мира и игрока, земля, предметы в круге, вершины гор.</summary>
public sealed class LayerToolsTests
{
    private const int ToolLayer = 18, ToolItems = 19, ToolPeak = 20;

    private sealed class Source(LayerSet? layers, Land? land) : IToolWorld
    {
        private readonly GameWorld _world = Fixtures.World();
        public GameWorld World => _world;
        public Land? Land => land;
        public LayerSet? Layers => layers;
        public int ProtoId(string path) => -1;
        public string? ProtoPath(int id) => null;
    }

    private static LayerSet Layers()
    {
        var layers = new LayerSet(
        [
            new LayerSpec { Name = "grid", Scope = LayerScope.World, Channels = [Channel.U8, Channel.F32, Channel.U16] },
            new LayerSpec { Name = "mine", Scope = LayerScope.Player, Channels = [Channel.U8] },
            new LayerSpec { Name = "marks", Scope = LayerScope.World, Cell = 8, Fields = [("kind", Channel.U8), ("power", Channel.F64)] },
            new LayerSpec { Name = "notes", Scope = LayerScope.Player, Cell = 4, Fields = [("kind", Channel.U8)] },
            new LayerSpec { Name = "land", Scope = LayerScope.World, Channels = [Channel.U8, Channel.U8, Channel.U8, Channel.U8], Remember = true },
        ]);
        layers.AddPlayer(2);
        return layers;
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

    private static byte[] Call(ModTools tools, int id, byte[] input)
    {
        var output = new byte[1 << 16];
        var length = tools.Call(id, input, output);
        Assert.True(length >= 0, $"инструмент {id}: код {length}");
        return output[..length];
    }

    private static double[] Doubles(byte[] bytes, int from = 0) =>
        [.. Enumerable.Range(0, (bytes.Length - from) / 8).Select(i => BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(from + i * 8)))];

    [Fact]
    public void LayerReadsRectangleRowByRowIncludingNegativeAndEmptyTiles()
    {
        var layers = Layers();
        layers.Set(0, LayerOwner.World, -2, -1, 0, 7);
        layers.Set(0, LayerOwner.World, -1, -1, 1, 2.5);
        layers.Set(0, LayerOwner.World, 0, 0, 2, 513);
        layers.Set(0, LayerOwner.World, 40, 33, 0, 9);
        var tools = new ModTools(new Source(layers, null));

        // Прямоугольник 3×2 с (-2, -1): значения построчно; чанк через границу (0 и -1) и пустые тайлы — нули.
        Assert.Equal([7.0, 0, 0, 0, 0, 0], Doubles(Call(tools, ToolLayer, Input(0u, 0u, 0u, -2, -1, 3u, 2u))));
        Assert.Equal([0.0, 2.5, 0, 0, 0, 0], Doubles(Call(tools, ToolLayer, Input(0u, 0u, 1u, -2, -1, 3u, 2u))));
        Assert.Equal([0.0, 0, 0, 0, 0, 513], Doubles(Call(tools, ToolLayer, Input(0u, 0u, 2u, -2, -1, 3u, 2u))));
        // Дальний чанк и пустой прямоугольник.
        Assert.Equal([9.0], Doubles(Call(tools, ToolLayer, Input(0u, 0u, 0u, 40, 33, 1u, 1u))));
        Assert.Empty(Call(tools, ToolLayer, Input(0u, 0u, 0u, 0, 0, 0u, 5u)));
    }

    [Fact]
    public void LayerOfPlayerNeedsItsOwnerAndRejectsBadInput()
    {
        var layers = Layers();
        layers.Set(1, LayerOwner.Player(2), 5, 5, 0, 3);
        var tools = new ModTools(new Source(layers, null));
        Assert.Equal([3.0, 0], Doubles(Call(tools, ToolLayer, Input(1u, 2u, 0u, 5, 5, 2u, 1u))));
        // У игрока без слоя — нули.
        Assert.Equal([0.0], Doubles(Call(tools, ToolLayer, Input(1u, 9u, 0u, 5, 5, 1u, 1u))));
        var scratch = new byte[64];
        Assert.Equal(-4, tools.Call(ToolLayer, Input(1u, 0u, 0u, 0, 0, 1u, 1u), scratch)); // слой игрока, а владелец — мир
        Assert.Equal(-4, tools.Call(ToolLayer, Input(0u, 2u, 0u, 0, 0, 1u, 1u), scratch)); // слой мира, а владелец — игрок
        Assert.Equal(-4, tools.Call(ToolLayer, Input(0u, 0u, 3u, 0, 0, 1u, 1u), scratch)); // нет канала
        Assert.Equal(-4, tools.Call(ToolLayer, Input(7u, 0u, 0u, 0, 0, 1u, 1u), scratch)); // нет слоя
        Assert.Equal(-4, tools.Call(ToolLayer, Input(2u, 0u, 0u, 0, 0, 1u, 1u), scratch)); // предметы, а не сетка
        Assert.Equal(-4, tools.Call(ToolLayer, Input(0u, 0u, 0u, 0, 0, 2000u, 2000u), scratch)); // больше предела
        Assert.Equal(-4, tools.Call(ToolLayer, Input(0u, 0u, 0u, int.MaxValue, 0, 2u, 1u), scratch));
        Assert.Equal(-4, tools.Call(ToolLayer, Input(0u, 0u, 0u, 0, 0, 1u), scratch)); // вход обрывается
        // Партия без слоёв.
        Assert.Equal(-4, new ModTools(new Source(null, null)).Call(ToolLayer, Input(0u, 0u, 0u, 0, 0, 1u, 1u), scratch));
    }

    [Fact]
    public void LandLayerReadsStoredTileBytesFromLand()
    {
        var land = new Land(new GeneratorConfig { Seed = 3, Blank = true }, LandArea.Of(-64, -64, 64, 64));
        land.SetTile(4, 5, new TileEdit(TerrainType.Rock, 2, 1, true));
        land.SetPeakTile(8, 8, 1, 8.5, 9, 2);
        var tools = new ModTools(new Source(Layers(), land));
        var rock = land.Stored(4, 5);
        for (var channel = 0u; channel < 4; channel++)
        {
            var expected = new[] { (double)rock.Terrain, rock.Biome, rock.Peak, rock.Relief }[channel];
            Assert.Equal(expected, Doubles(Call(tools, ToolLayer, Input(4u, 0u, channel, 4, 5, 1u, 1u)))[0]);
        }
        var mountain = land.Stored(8, 8);
        Assert.Equal((double)TerrainType.Mountain, Doubles(Call(tools, ToolLayer, Input(4u, 0u, 0u, 8, 8, 1u, 1u)))[0]);
        Assert.Equal((double)mountain.Peak, Doubles(Call(tools, ToolLayer, Input(4u, 0u, 2u, 8, 8, 1u, 1u)))[0]);
    }

    [Fact]
    public void ItemsInCircleGoByCellsThenRecordOrder()
    {
        var layers = Layers();
        // Ячейки 8×8: (0,0) — первый и второй по порядку записи, (1,0), (0,1) и далёкая.
        layers.Push(2, LayerOwner.World, new LayerItem(3, 3, [1, 10]));
        layers.Push(2, LayerOwner.World, new LayerItem(2, 4, [2, 20]));
        layers.Push(2, LayerOwner.World, new LayerItem(9, 1, [3, 30]));
        layers.Push(2, LayerOwner.World, new LayerItem(1, 9, [4, 40]));
        layers.Push(2, LayerOwner.World, new LayerItem(-5, -5, [5, 50]));
        layers.Push(2, LayerOwner.World, new LayerItem(300, 300, [6, 60]));
        var tools = new ModTools(new Source(layers, null));

        var output = Call(tools, ToolItems, Input(2u, 0u, 4.0, 4.0, 6.0));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(output));
        // Порядок: ячейки по y, затем x; (0,0): (3,3), (2,4); (1,0): (9,1); (0,1): (1,9).
        Assert.Equal([3.0, 3, 1, 10, 2, 4, 2, 20, 9, 1, 3, 30, 1, 9, 4, 40], Doubles(output, 4));

        // Край круга включён; (9,1) от (4,4) на √34 ≈ 5,83.
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(Call(tools, ToolItems, Input(2u, 0u, 3.0, 3.0, 0.0))));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(Call(tools, ToolItems, Input(2u, 0u, 4.0, 4.0, 1.0))));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(Call(tools, ToolItems, Input(2u, 0u, 0.0, 0.0, 1e6))));
        // Круг шире занятых ячеек — те же предметы, тот же порядок.
        var all = Doubles(Call(tools, ToolItems, Input(2u, 0u, 100.0, 100.0, 1e9)), 4);
        Assert.Equal([-5.0, -5, 5, 50, 3, 3, 1, 10, 2, 4, 2, 20, 9, 1, 3, 30, 1, 9, 4, 40, 300, 300, 6, 60], all);
    }

    [Fact]
    public void ItemsOfPlayerLayerAndBadInput()
    {
        var layers = Layers();
        layers.Push(3, LayerOwner.Player(2), new LayerItem(1, 1, [7]));
        var tools = new ModTools(new Source(layers, null));
        Assert.Equal([1.0, 1, 7], Doubles(Call(tools, ToolItems, Input(3u, 2u, 1.0, 1.0, 2.0)), 4));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(Call(tools, ToolItems, Input(3u, 5u, 1.0, 1.0, 2.0))));
        var scratch = new byte[64];
        Assert.Equal(-4, tools.Call(ToolItems, Input(3u, 0u, 1.0, 1.0, 2.0), scratch)); // слой игрока, а владелец — мир
        Assert.Equal(-4, tools.Call(ToolItems, Input(0u, 0u, 1.0, 1.0, 2.0), scratch)); // сетка, а не предметы
        Assert.Equal(-4, tools.Call(ToolItems, Input(2u, 0u, 1.0, 1.0, -1.0), scratch));
        Assert.Equal(-4, tools.Call(ToolItems, Input(2u, 0u, double.NaN, 1.0, 1.0), scratch));
        Assert.Equal(-4, tools.Call(ToolItems, Input(2u, 0u, 1.0, 1.0, double.PositiveInfinity), scratch));
        Assert.Equal(-4, tools.Call(ToolItems, Input(99u, 0u, 1.0, 1.0, 1.0), scratch));
    }

    [Fact]
    public void PeakTellsMountainOfTileAndNothingElsewhere()
    {
        var land = new Land(new GeneratorConfig { Seed = 3, Blank = true }, LandArea.Of(-64, -64, 64, 64));
        land.SetPeakTile(3, 3, 1, 3.5, 4, 2);
        land.SetPeakTile(4, 3, 1, 3.5, 4, 2);
        var tools = new ModTools(new Source(null, land));
        var output = Call(tools, ToolPeak, Input(3u, 3, 3, 4, 3, 10, 10));
        Assert.Equal(75, output.Length);
        Assert.Equal(1, output[0]);
        var peak = land.PeakOf(3, 3)!.Value;
        Assert.Equal([peak.X, peak.Y, peak.Radius], Doubles(output[1..25]));
        Assert.Equal(1, output[25]);
        Assert.Equal(0, output[50]);
        Assert.All(output[51..], value => Assert.Equal(0, value));
        // Без земли — ни одной вершины.
        Assert.Equal(new byte[25], Call(new ModTools(new Source(null, null)), ToolPeak, Input(1u, 3, 3)));
        Assert.Equal(-4, tools.Call(ToolPeak, Input(2u, 3, 3), new byte[64]));
    }
}
