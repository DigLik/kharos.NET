using Kharos.Client.Looks;

namespace Kharos.Client.Tests.Looks;

/// <summary>Облики: кадры по направлениям и фазам, упаковка в общий атлас, портреты, значки.</summary>
public sealed class AtlasTests
{
    private static (int X, int Y, int W, int H) Pixels(Kharos.Client.Ui.Uv uv, LookAtlas atlas) =>
        ((int)MathF.Round(uv.U0 * atlas.Width), (int)MathF.Round(uv.V0 * atlas.Height),
         (int)MathF.Round((uv.U1 - uv.U0) * atlas.Width), (int)MathF.Round((uv.V1 - uv.V0) * atlas.Height));

    [Fact]
    public void BuildsFramesByPhaseAndDirection()
    {
        var look = Look.Build(7, Demo.Buggy);
        Assert.Equal(64 * 2, look.Images.Count);
        Assert.Equal((-1f, -1f), look.Offset);
        Assert.Equal((2 / 16f, 0.4f), look.Shadow);
        Assert.Equal((2f, 2f), look.Tiles);
        // Огонь фары — в тайлах от места сущности.
        var light = look.FrameLights[0][0];
        Assert.Equal(new FrameLight(9 / 16f, -3 / 16f, 6, 1), light);
        Assert.Equal(0, look.Cell(0, 0));
        Assert.Equal(16, look.Cell(Math.PI / 2, 0));
        Assert.Equal(63, look.Cell(-0.05, 0));
        Assert.Equal(0, look.Cell(Look.Turn - 0.01, 0));
        Assert.Equal(64 + 32, look.Cell(Math.PI, 1));
        Assert.Equal(32, look.Cell(Math.PI, 2));
        // Кадр с углом совпадает с чертежом, нарисованным напрямую.
        var direct = new Canvas(32, 32) { OriginX = 16, OriginY = 16 };
        Demo.Buggy.Draw(direct, 16.0 / 64 * Look.Turn, 1);
        Assert.Equal(direct.Data, look.Images[64 + 16]);
    }

    [Fact]
    public void PacksWithoutOverlap()
    {
        var (looks, atlas) = Demo.Atlas();
        Assert.Equal(LookAtlas.MaxWidth, atlas.Width);
        var rects = new List<(int X, int Y, int W, int H)>();
        foreach (var look in looks)
        {
            Assert.Equal(look.Directions * look.Phases, look.Cells.Length);
            Assert.Empty(look.Images);
            foreach (var cell in look.Cells)
            {
                var rect = Pixels(cell, atlas);
                Assert.Equal((look.Width, look.Height), (rect.W, rect.H));
                rects.Add(rect);
            }
            if (look.PortraitUv is { } portrait) rects.Add(Pixels(portrait, atlas));
        }
        Assert.Equal(128 + 64 + 16 + 1, rects.Count);
        foreach (var rect in rects) Assert.True(rect.X >= 0 && rect.Y >= 0 && rect.X + rect.W <= atlas.Width && rect.Y + rect.H <= atlas.Height);
        for (var i = 0; i < rects.Count; i++)
            for (var j = i + 1; j < rects.Count; j++)
            {
                var (a, b) = (rects[i], rects[j]);
                Assert.False(a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H, $"{a} и {b} перекрываются");
            }
        Repo.Png("looks-atlas.png", atlas.Pixels, atlas.Width, atlas.Height);
    }

    [Fact]
    public void PackedPixelsMatchFramesAndAreDeterministic()
    {
        var frames = Look.Build(1, Demo.Buggy).Images.ToArray();
        var (looks, atlas) = Demo.Atlas();
        for (var i = 0; i < frames.Length; i++)
        {
            var (x, y, w, h) = Pixels(looks[0].Cells[i], atlas);
            for (var row = 0; row < h; row++)
                Assert.True(frames[i].AsSpan(row * w * 4, w * 4).SequenceEqual(atlas.Pixels.AsSpan(((y + row) * atlas.Width + x) * 4, w * 4)));
        }
        var (_, again) = Demo.Atlas();
        Assert.Equal(Repo.Sha256(atlas.Pixels), Repo.Sha256(again.Pixels));
        // Полки от высоких к низким: кадры завода с нуля, багги — следом на той же полке, пока она не кончится
        // (4096 − 16 × 80) / 32 = 88 кадров, дальше — новая полка под самой высокой.
        Assert.Equal((0, 0), (Pixels(looks[2].Cells[0], atlas).X, Pixels(looks[2].Cells[0], atlas).Y));
        Assert.Equal((1280, 0), (Pixels(looks[0].Cells[0], atlas).X, Pixels(looks[0].Cells[0], atlas).Y));
        Assert.Equal((0, 80), (Pixels(looks[0].Cells[88], atlas).X, Pixels(looks[0].Cells[88], atlas).Y));
    }

    [Fact]
    public void PortraitIsCroppedAndUsedAsIcon()
    {
        var (looks, atlas) = Demo.Atlas();
        var factory = looks[2];
        // Портрет: прямоугольник 72 × 72 от (−36, −36) при начале (40, 40) — обрезан до закрашенного.
        Assert.Equal((72f, 72f), factory.PortraitSize);
        var icon = factory.Icon;
        Assert.Equal((72f, 72f), (icon.Width, icon.Height));
        var (x, y, w, h) = Pixels(icon.Uv, atlas);
        Assert.Equal((72, 72), (w, h));
        Assert.Equal(0x2d, atlas.Pixels[(y * atlas.Width + x) * 4]);
        // Без портрета — кадр значка.
        Assert.Equal(looks[0].Cells[0], looks[0].Icon.Uv);
        Assert.Equal((32f, 32f), (looks[0].Icon.Width, looks[0].Icon.Height));
        var empty = Look.Build(9, new LookSpec((_, _, _) => { }) { Portrait = new PortraitSpec(8, 8, _ => { }) });
        Assert.Equal((1f, 1f), empty.PortraitSize);
    }
}
