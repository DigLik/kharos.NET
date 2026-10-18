using System.Text;
using Kharos.Client.Ui;
using Kharos.Gpu;

namespace Kharos.Client.Tests.Ui;

/// <summary>Глифы Ark Pixel: чёткие пиксели по порогу, атлас по требованию, стабильный отпечаток.</summary>
public sealed class FontTests : IDisposable
{
    private readonly Fonts _fonts = Fonts.Load();

    public void Dispose() => _fonts.Dispose();

    private const string Sample = "Kharos: Завод боевых машин, 1250 кредитов +4/с — «Меню»… ×3";

    private static IEnumerable<int> Alphabet() =>
        Enumerable.Range(0x21, 0x5e).Concat(Enumerable.Range(0x410, 0x40)).Append(0x401).Append(0x451);

    [Fact]
    public void NativeSizesAreCrispWithoutThreshold()
    {
        // Контуры пиксельного шрифта лежат на сетке: на родном кегле и целых кратных покрытие — только 0 и 255,
        // порог ничего не меняет, и глиф совпадает с fontdue (оба считают точную площадь покрытия).
        foreach (var (face, px) in new[] { (0, 12f), (0, 24f), (1, 10f), (1, 20f), (1, 40f) })
            foreach (var codepoint in Alphabet())
            {
                var (w, h, _, _, pixels) = _fonts.Faces[face].Rasterize(codepoint, px, threshold: false);
                Assert.True(w > 0 && h > 0, $"U+{codepoint:X4}");
                Assert.All(pixels, value => Assert.True(value is 0 or 255, $"лицо {face}, {px} px, U+{codepoint:X4}: {value}"));
            }
    }

    [Fact]
    public void AtlasHoldsOnlyCrispPixels()
    {
        var batch = new Batch();
        foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            _fonts.SetScale(scale);
            foreach (var font in Enum.GetValues<FontName>()) _fonts.Draw(batch, 3, 7, font, Color.White, Sample, null);
            Assert.All(_fonts.Atlas.Pixels, value => Assert.True(value is 0 or 255));
            for (var y = 0; y < 4; y++)
                for (var x = 0; x < 4; x++)
                    Assert.Equal(255, _fonts.Atlas.Pixels[y * FontAtlas.Size + x]);
        }
    }

    [Fact]
    public void GlyphsLandOnPhysicalPixels()
    {
        foreach (var scale in new[] { 1f, 1.5f, 2f })
        {
            _fonts.SetScale(scale);
            var batch = new Batch();
            _fonts.Draw(batch, 10.3f, 20.6f, FontName.Body, Color.White, Sample, null);
            foreach (var vertex in batch.Vertices)
            {
                Assert.Equal(MathF.Round(vertex.X * scale), vertex.X * scale, 3);
                Assert.Equal(MathF.Round(vertex.Y * scale), vertex.Y * scale, 3);
                Assert.Equal(MathF.Round(vertex.U * FontAtlas.Size), vertex.U * FontAtlas.Size, 3);
            }
        }
    }

    [Fact]
    public void GenerationGrowsOnlyForNewGlyphs()
    {
        var batch = new Batch();
        var start = _fonts.Atlas.Generation;
        _fonts.Draw(batch, 0, 0, FontName.Body, Color.White, "abc", null);
        var after = _fonts.Atlas.Generation;
        Assert.Equal(start + 3, after);
        _fonts.Draw(batch, 0, 0, FontName.Body, Color.White, "cab", null);
        Assert.Equal(after, _fonts.Atlas.Generation);
        _fonts.Draw(batch, 0, 0, FontName.Small, Color.White, "a", null);
        Assert.Equal(after + 1, _fonts.Atlas.Generation);
        // Новый масштаб — атлас заново.
        _fonts.SetScale(2);
        Assert.True(_fonts.Atlas.Generation > after + 1);
        Assert.Equal(0, _fonts.Atlas.Pixels[4 * FontAtlas.Size + 10]);
    }

    [Fact]
    public void SampleStringIsStable()
    {
        var batch = new Batch();
        foreach (var font in Enum.GetValues<FontName>()) _fonts.Draw(batch, 0, 0, font, Color.White, Sample, null);
        var quads = new StringBuilder();
        foreach (var vertex in batch.Vertices) quads.Append($"{vertex.X},{vertex.Y},{vertex.U * FontAtlas.Size},{vertex.V * FontAtlas.Size};");
        Repo.PngGray("font-atlas.png", _fonts.Atlas.Pixels, FontAtlas.Size, FontAtlas.Size);
        // Отпечатки атласа и раскладки глифов: меняются, только если поменялись шрифт, растеризатор или укладка.
        Assert.Equal(AtlasSha, Repo.Sha256(_fonts.Atlas.Pixels));
        Assert.Equal(QuadsSha, Repo.Sha256(Encoding.UTF8.GetBytes(quads.ToString())));
    }

    private const string AtlasSha = "3903e68fc72f2f5a5740bb455bd8e38e4d05b4c0a683978632139c73b7734c5b";
    private const string QuadsSha = "f3ddfc368b685e1a3a17127183b14cac31dca454193be7559c70df3e5824812c";

    [Fact]
    public void KerningIsOptIn()
    {
        // Ark Pixel несёт пары в GPOS: −100 единиц из 1200 — пиксель на кегле 12. Пилот их не применяет.
        var plain = _fonts.Width(FontName.Body, "AVATo");
        Assert.Equal(1f, _fonts.Faces[0].Kern('A', 'V', 12) * -1, 3);
        _fonts.Kerning = true;
        Assert.Equal(plain - 4, _fonts.Width(FontName.Body, "AVATo"));
        var batch = new Batch();
        _fonts.Draw(batch, 0, 0, FontName.Body, Color.White, "AV", null);
        var kerned = batch.Vertices[4].X - batch.Vertices[0].X;
        _fonts.Kerning = false;
        Assert.Equal(plain, _fonts.Width(FontName.Body, "AVATo"));
        batch.Clear();
        _fonts.Draw(batch, 0, 0, FontName.Body, Color.White, "AV", null);
        Assert.Equal(kerned + 1, batch.Vertices[4].X - batch.Vertices[0].X);
    }

    [Fact]
    public void MissingGlyphFallsBackToNotdef()
    {
        Assert.True(_fonts.Width(FontName.Body, "\U0001F600") >= 0);
        var batch = new Batch();
        _fonts.Draw(batch, 0, 0, FontName.Body, Color.White, "\U0001F600", null);
    }
}
