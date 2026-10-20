using Kharos.Engine.Terrain;

namespace Kharos.Engine.Tests.Terrain;

/// <summary>Местность: переносы packages/tests/land.test.ts, которые не требуют эталонов TS (область, сохранение, правка).</summary>
public sealed class LandTests
{
    private static readonly LandArea Area = LandArea.Of(-64, -64, 64, 64);

    private static byte[] Chunks(Land land)
    {
        var all = new List<byte>();
        for (int y = Area.Top; y < Area.Top + Area.Height; y++)
            for (int x = Area.Left; x < Area.Left + Area.Width; x++)
                all.AddRange(land.GetChunk(x, y).ToArray());
        return all.ToArray();
    }

    [Fact(DisplayName = "область карты покрывает границы мира чанками")]
    public void AreaCoversWorldBounds()
    {
        Assert.Equal(new LandArea(-2, -2, 4, 4), Area);
        Assert.Equal(new LandArea(-2, -2, 4, 4), LandArea.Of(-50, -50, 50, 50));
    }

    [Fact(DisplayName = "карта из сохранения та же байт в байт, а лениво собранная — та же, что из генератора")]
    public void SavedLandIsSameByteForByte()
    {
        var land = new Land(GeneratorConfig.Default, Area);
        var loaded = Land.Load(GeneratorConfig.Default, land.Save());
        Assert.Equal(Chunks(land), Chunks(loaded));
        Assert.Equal(Chunks(land), Chunks(new Land(GeneratorConfig.Default, Area)));
    }

    // В .NET версии генератора нет: карту читает тот же Load, что и в TS, и сид из настроек влияет только на несобранное.
    [Fact(DisplayName = "смена генератора не трогает сохранённый мир, а край мира берёт новый генератор")]
    public void GeneratorChangeKeepsSavedWorldAndNewEdge()
    {
        var land = new Land(GeneratorConfig.Default, Area);
        var other = GeneratorConfig.Default with { Seed = GeneratorConfig.Default.Seed + 1 };
        var loaded = Land.Load(other, land.Save());
        Assert.Equal(Chunks(land), Chunks(loaded));
        Assert.Equal(new Land(other).GetChunk(10, 10).ToArray(), loaded.GetChunk(10, 10).ToArray());
    }

    [Fact(DisplayName = "правка тайла: пересчитывает соседей как полная загрузка, уходит в сохранение и переносится на другую сторону")]
    public void TileEditRecomputesNeighboursSavesAndReplicates()
    {
        var land = new Land(GeneratorConfig.Default, Area);
        var replica = new Land(GeneratorConfig.Default, Area);
        // Скала посреди песка: глубина песков вокруг падает.
        int x = 0, y = 0;
        bool found = false;
        for (y = Area.Top * 32 + 10; y < (Area.Top + Area.Height) * 32 - 10 && !found; y++)
            for (x = Area.Left * 32 + 10; x < (Area.Left + Area.Width) * 32 - 10 && !found; x++)
                if (land.TerrainAt(x, y) == TerrainType.Sand) found = true;
        x--; y--;
        Assert.True(found);
        // Тайл справа от правки.
        int Depth(Land target) => target.GetChunk((x + 1) >> 5, y >> 5)[(((y & 31) * 32) + ((x + 1) & 31)) * 4 + 2];

        land.SetTile(x, y, new(TerrainType.Rock, 0, 1, true));
        Assert.Equal(TerrainType.Rock, land.TerrainAt(x, y));
        Assert.Equal((int)Math.Round(0.5 / 8 * 255), Depth(land));
        Assert.Equal(1, land.Revision);

        Assert.Equal(Chunks(land), Chunks(Land.Load(GeneratorConfig.Default, land.Save())));

        var tile = land.Stored(x, y);
        replica.ApplyEdits([x, y, tile.Terrain, tile.Biome, tile.Peak, tile.Relief]);
        Assert.Equal(Chunks(land), Chunks(replica));
    }

    [Fact(DisplayName = "вне карты править нельзя")]
    public void EditOutsideAreaIsIgnored()
    {
        var land = new Land(GeneratorConfig.Default, Area);
        land.SetTile(500, 500, new(TerrainType.Rock, 0, 1, false));
        Assert.Equal(0, land.Revision);
    }
}
