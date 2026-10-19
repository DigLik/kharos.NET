using Kharos.Client.Shell;
using Kharos.Engine.Terrain;

namespace Kharos.Client.Tests.Shell;

/// <summary>Порт packages/tests/preview.test.ts и схема новой игры оболочки (TerrainPreview).</summary>
public sealed class PreviewTests
{
    [Fact(DisplayName = "предпросмотр местности совпадает с миром везде, кроме гор")]
    public void PreviewMatchesWorldExceptMountains()
    {
        var config = new GeneratorConfig { Seed = 4242, RockLevel = 0.5 };
        var land = new Land(config);
        for (var y = -300; y < 300; y += 7)
        {
            for (var x = -300; x < 300; x += 7)
            {
                var terrain = land.TerrainAt((double)x, y);
                var sample = TerrainGenerator.Sample(config, x, y);
                Assert.Equal(land.BiomeAt(x, y), sample.Biome);
                Assert.Equal(terrain == TerrainType.Mountain ? TerrainType.Rock : terrain, sample.Terrain);
            }
        }
    }

    [Fact]
    public void PreviewWaitsForSettleThenFillsAndCountsShares()
    {
        var preview = new TerrainPreview();
        preview.Show(new GeneratorConfig { Seed = 7 }, 512, 256, now: 0);
        preview.Step(0.05, TimeSpan.Zero);
        Assert.False(preview.Ready);
        Assert.Equal(0, preview.Revision);

        preview.Step(1, TimeSpan.Zero);
        Assert.True(preview.Ready);
        Assert.True(preview.Revision > 0);
        var (rock, swamp) = preview.Shares!.Value;
        Assert.InRange(rock + swamp, 0.01, 1);
        // Карта 512×256 вписана в квадрат по ширине: верхняя четверть — поле вне карты.
        Assert.Equal([8, 17, 28, 255], preview.Pixels[..4]);
        var middle = (TerrainPreview.Side / 2 * TerrainPreview.Side + TerrainPreview.Side / 2) * 4;
        Assert.Equal(255, preview.Pixels[middle + 3]);
        Assert.NotEqual([8, 17, 28], preview.Pixels[middle..(middle + 3)]);

        // Та же местность — не пересчитывается; другая — заново, после паузы.
        var revision = preview.Revision;
        preview.Show(new GeneratorConfig { Seed = 7 }, 512, 256, now: 2);
        Assert.True(preview.Ready);
        preview.Show(new GeneratorConfig { Seed = 8 }, 512, 256, now: 2);
        Assert.False(preview.Ready);
        Assert.Null(preview.Shares);
        preview.Step(2.01, TimeSpan.Zero);
        Assert.Equal(revision, preview.Revision);
    }
}
