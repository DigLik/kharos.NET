using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Kharos.Engine.Terrain;

namespace Kharos.Engine.Tests.Terrain;

/// <summary>Земля C# против TS до байта: 1024² тайлов, дальние чанки, saveLand, правки (эталоны — Golden).</summary>
public sealed class LandGoldenTests(ITestOutputHelper output)
{
    public static TheoryData<string> Configs => new("default", "seed3", "rocky", "islands", "peaks", "blank");

    [Theory]
    [MemberData(nameof(Configs))]
    public void LandMatchesTypeScript(string name)
    {
        var golden = Golden.ReadJson("Terrain", $"land-{name}.json");
        var config = Golden.Config(golden.GetProperty("config"));
        var area = Golden.Area(golden.GetProperty("area"));
        Assert.Equal(LandArea.Of(-512, -512, 512, 512), area);

        // Один чанк байтами: при расхождении видно, какой тайл и какой байт.
        var land = new Land(config, area);
        var chunk = golden.GetProperty("chunk");
        var expectedChunk = Convert.FromBase64String(chunk.GetProperty("base64").GetString()!);
        AssertBytes(expectedChunk, land.GetChunk(chunk.GetProperty("x").GetInt32(), chunk.GetProperty("y").GetInt32()));

        var watch = Stopwatch.StartNew();
        land = new Land(config, area);
        var rows = Rows(land, area);
        watch.Stop();
        output.WriteLine($"{name}: {area.Width}×{area.Height} чанков за {watch.Elapsed.TotalMilliseconds:F0} мс");
        Assert.Equal(golden.GetProperty("rows").EnumerateArray().Select(row => row.GetString()), rows);

        foreach (var far in golden.GetProperty("far").EnumerateArray())
            Assert.Equal(far.GetProperty("sha256").GetString(), Golden.Sha256(land.GetChunk(far.GetProperty("x").GetInt32(), far.GetProperty("y").GetInt32())));

        var saved = land.Save();
        Assert.Equal(golden.GetProperty("save").GetProperty("length").GetInt32(), saved.Length);
        Assert.Equal(golden.GetProperty("save").GetProperty("sha256").GetString(), Golden.Sha256(saved));

        var sample = golden.GetProperty("sample");
        int from = sample.GetProperty("from").GetInt32();
        int step = sample.GetProperty("step").GetInt32();
        int size = sample.GetProperty("size").GetInt32();
        var schema = new byte[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var (terrain, biome) = TerrainGenerator.Sample(config, from + x * step, from + y * step);
                schema[y * size + x] = (byte)((int)terrain | (int)biome << 2);
            }
        }
        Assert.Equal(sample.GetProperty("sha256").GetString(), Golden.Sha256(schema));
    }

    [Fact]
    public void DerivedBytesDoNotDependOnBuildOrderOrReload()
    {
        // Сид 3: у гор на стыке чанков выводимое зависело от того, собран ли сосед (packages/tests/land.test.ts).
        var golden = Golden.ReadJson("Terrain", "land-seed3.json");
        var config = Golden.Config(golden.GetProperty("config"));
        var area = Golden.Area(golden.GetProperty("area"));
        var backward = new Land(config, area);
        for (int cy = area.Top + area.Height - 1; cy >= area.Top; cy--)
            for (int cx = area.Left + area.Width - 1; cx >= area.Left; cx--)
                backward.GetChunk(cx, cy);
        var expected = golden.GetProperty("rows").EnumerateArray().Select(row => row.GetString()).ToArray();
        Assert.Equal(expected, Rows(backward, area));
        Assert.Equal(expected, Rows(Land.Load(config, backward.Save()), area));
        Assert.Equal(expected, Rows(Land.Unpack(config, backward.Pack()), area));
    }

    [Fact]
    public void EditsMatchTypeScript()
    {
        var golden = Golden.ReadJson("Terrain", "edits.json");
        var config = Golden.Config(golden.GetProperty("config"));
        var area = Golden.Area(golden.GetProperty("area"));
        var bounds = golden.GetProperty("bounds");
        Assert.Equal(LandArea.Of(bounds.GetProperty("left").GetInt32(), bounds.GetProperty("top").GetInt32(), bounds.GetProperty("right").GetInt32(), bounds.GetProperty("bottom").GetInt32()), area);

        var land = new Land(config, area);
        foreach (var op in golden.GetProperty("ops").EnumerateArray()) Run(land, op);
        Assert.Equal(golden.GetProperty("revision").GetInt32(), land.Revision);
        Assert.Equal(golden.GetProperty("chunks").GetString(), ChunksSha(land, area));

        var stored = golden.GetProperty("stored").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        for (int i = 0; i < stored.Length; i += 6)
        {
            var tile = land.Stored(stored[i], stored[i + 1]);
            Assert.Equal(stored.AsSpan(i + 2, 4).ToArray(), new[] { tile.Terrain, tile.Biome, tile.Peak, tile.Relief }.Select(value => (int)value).ToArray());
        }

        // saveLand C# — те же байты, что у TS; сжатую TS читает C#.
        var save = golden.GetProperty("save");
        var saved = land.Save();
        Assert.Equal(save.GetProperty("sha256").GetString(), Golden.Sha256(saved));
        var tsPacked = Convert.FromBase64String(save.GetProperty("deflated").GetString()!);
        Assert.Equal(saved, Inflate(tsPacked));
        var unpacked = Land.Unpack(config, tsPacked);
        Assert.Equal(golden.GetProperty("chunks").GetString(), ChunksSha(unpacked, area));
        Assert.Equal(saved, Inflate(land.Pack()));

        // Нетронутая карта из TS — та же, что из генератора C#.
        var pristine = golden.GetProperty("pristine");
        var fresh = new Land(config, area);
        Assert.Equal(pristine.GetProperty("sha256").GetString(), Golden.Sha256(fresh.Save()));
        Assert.Equal(ChunksSha(fresh, area), ChunksSha(Land.Unpack(config, Convert.FromBase64String(pristine.GetProperty("deflated").GetString()!)), area));

        foreach (var patch in golden.GetProperty("patches").EnumerateArray())
        {
            var p = patch.EnumerateArray().Select(value => value.GetInt32()).ToArray();
            Land.PatchSavedTile(saved, p[0], p[1], new((byte)p[2], (byte)p[3], (byte)p[4], (byte)p[5]));
        }
        Assert.Equal(golden.GetProperty("patched").GetString(), Golden.Sha256(saved));
    }

    [Fact]
    public void EditsReplicateThroughStoredBytes()
    {
        var area = LandArea.Of(-64, -64, 64, 64);
        var land = new Land(GeneratorConfig.Default, area);
        var replica = new Land(GeneratorConfig.Default, area);
        land.SetTile(5, 5, new(TerrainType.Rock, 0, 1, Cliff: true));
        land.SetPeakTile(-10, 7, 0x80, -9.5, 8, 1.5);
        var edits = new List<int>();
        foreach (var (x, y) in new[] { (5, 5), (-10, 7) })
        {
            var tile = land.Stored(x, y);
            edits.AddRange([x, y, tile.Terrain, tile.Biome, tile.Peak, tile.Relief]);
        }
        replica.ApplyEdits(edits.ToArray());
        Assert.Equal(ChunksSha(land, area), ChunksSha(replica, area));
        // Радиус хранится байтом: round(1.5 / 4 · 255) = 96.
        Assert.Equal(new Peak(-9.5, 8, 96 / 255.0 * 4), land.PeakOf(-10, 7));
        Assert.Null(land.PeakOf(5, 5));
        Assert.True(land.ChunkRevision(0, 0) > 0);
        Assert.Equal(0, land.ChunkRevision(1, 1));
    }

    [Fact]
    public void AreaAndBoundaries()
    {
        Assert.Equal(new LandArea(-2, -2, 4, 4), LandArea.Of(-64, -64, 64, 64));
        Assert.Equal(new LandArea(-2, -2, 4, 4), LandArea.Of(-50, -50, 50, 50));
        var land = new Land(GeneratorConfig.Default, LandArea.Of(-64, -64, 64, 64));
        land.SetTile(500, 500, new(TerrainType.Rock, 0, 1, false));
        land.SetTile(0, 0, new(TerrainType.Mountain, 0, 0, false));
        Assert.Equal(0, land.Revision);
        Assert.Throws<InvalidOperationException>(() => new Land(GeneratorConfig.Default).Save());
        Assert.Throws<FormatException>(() => Land.Load(GeneratorConfig.Default, new byte[] { 1, 2, 3 }));
        var saved = land.Save();
        Assert.Throws<FormatException>(() => Land.Load(GeneratorConfig.Default, saved.AsSpan(0, saved.Length - 1)));
    }

    private static void Run(Land land, JsonElement op)
    {
        switch (op.GetProperty("op").GetString())
        {
            case "tile":
                land.SetTile(op.GetProperty("x").GetInt32(), op.GetProperty("y").GetInt32(), new(
                    (TerrainType)op.GetProperty("terrain").GetInt32(),
                    (byte)op.GetProperty("biome").GetInt32(),
                    op.GetProperty("tier").GetInt32(),
                    op.GetProperty("cliff").GetBoolean(),
                    op.TryGetProperty("dunes", out var dunes) ? (Dunes)dunes.GetInt32() : Dunes.Natural));
                break;
            case "peak":
                land.SetPeakTile(op.GetProperty("x").GetInt32(), op.GetProperty("y").GetInt32(), (byte)op.GetProperty("biome").GetInt32(),
                    op.GetProperty("peakX").GetDouble(), op.GetProperty("peakY").GetDouble(), op.GetProperty("radius").GetDouble());
                break;
            case "batch":
                land.Batch(() =>
                {
                    foreach (var inner in op.GetProperty("ops").EnumerateArray()) Run(land, inner);
                });
                break;
            case "edits":
                land.ApplyEdits(op.GetProperty("edits").EnumerateArray().Select(value => value.GetInt32()).ToArray());
                break;
            default:
                throw new InvalidDataException(op.ToString());
        }
    }

    /// <summary>SHA-256 каждого ряда чанков области: все чанки ряда подряд, с выводимым.</summary>
    private static string[] Rows(Land land, LandArea area)
    {
        var rows = new string[area.Height];
        for (int cy = 0; cy < area.Height; cy++)
        {
            var parts = new byte[area.Width][];
            for (int cx = 0; cx < area.Width; cx++) parts[cx] = land.GetChunk(area.Left + cx, area.Top + cy).ToArray();
            rows[cy] = Golden.Sha256(parts);
        }
        return rows;
    }

    private static string ChunksSha(Land land, LandArea area)
    {
        var parts = new List<byte[]>();
        for (int cy = area.Top; cy < area.Top + area.Height; cy++)
            for (int cx = area.Left; cx < area.Left + area.Width; cx++)
                parts.Add(land.GetChunk(cx, cy).ToArray());
        return Golden.Sha256(parts);
    }

    private static byte[] Inflate(byte[] packed)
    {
        using var input = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
        var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static void AssertBytes(byte[] expected, ReadOnlySpan<byte> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            if (expected[i] != actual[i])
                Assert.Fail($"тайл {i / TileFormat.TileBytes} (x {i / TileFormat.TileBytes % TileFormat.ChunkSize}, y {i / TileFormat.TileBytes / TileFormat.ChunkSize}), байт {i % TileFormat.TileBytes}: ждали {expected[i]}, получили {actual[i]}");
    }
}
