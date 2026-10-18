using Kharos.Client.Ui;
using Kharos.Gpu;

namespace Kharos.Client.Tests.Ui;

/// <summary>Что интерфейс кладёт в батч: отрезки по краскам и обрезке, четырёхугольники, цвета и uv.</summary>
public sealed class BatchTests : IDisposable
{
    private readonly Gui _ui = new(Fonts.Load());

    public void Dispose() => _ui.Fonts.Dispose();

    private Batch Frame(Action build)
    {
        _ui.Begin(Frames.Off(0));
        build();
        var batch = new Batch();
        _ui.Finish(batch);
        return batch;
    }

    private static PaintKind[] Kinds(Batch batch) => [.. batch.Segments.Select(segment => segment.Paint.Kind)];

    [Fact]
    public void PanelSkinBackgroundOutlineAndText()
    {
        var batch = Frame(() => _ui.Box(new Style { Anchor = new Anchor(Left: 10, Top: 10), Width = 100, Height = 40, Background = Color.Hex(0x102030), Skin = Skin.Panel, Outline = Color.Hex(0xff0000), Padding = 10 }, () =>
            _ui.Text("Hi", new TextStyle { Shadow = Skins.Ink })));
        // Заливка (белым углом шрифта), девять частей скина, четыре полосы обводки, тень и буквы — краской шрифта.
        Assert.Equal([PaintKind.Font, PaintKind.Skin, PaintKind.Font], Kinds(batch));
        Assert.Equal(0, batch.Segments[0].Start);
        Assert.Equal(6, batch.Segments[1].Start);
        Assert.Equal(6 + 9 * 6, batch.Segments[2].Start);
        Assert.Equal((1 + 9 + 4 + 2 * 2) * 6, batch.Indices.Count);
        Assert.All(batch.Segments, segment => Assert.Null(segment.Clip));
        var fill = batch.Vertices[0];
        Assert.Equal(new Vertex(10, 10, Batch.WhiteU, Batch.WhiteV, Color.Hex(0x102030).Rgba), fill);
        Assert.Equal(new Vertex(110, 50, Batch.WhiteU, Batch.WhiteV, Color.Hex(0x102030).Rgba), batch.Vertices[2]);
        // Угол скина: 8 × 8 пикселей экрана, 4 × 4 пикселя скина панели.
        var corner = batch.Vertices[4];
        Assert.Equal((10f, 10f, 0f, 0f), (corner.X, corner.Y, corner.U, corner.V));
        Assert.Equal((18f, 18f, 4f / Skins.Width, 4f / Skins.Height), (batch.Vertices[6].X, batch.Vertices[6].Y, batch.Vertices[6].U, batch.Vertices[6].V));
        // Буквы стоят на целых пикселях; тень — на пиксель правее и ниже, чернилами.
        var shadow = batch.Vertices[4 * (1 + 9 + 4)];
        var letter = batch.Vertices[4 * (1 + 9 + 4 + 2)];
        Assert.Equal(Skins.Ink.Rgba, shadow.Rgba);
        Assert.Equal(Gui.TextColor.Rgba, letter.Rgba);
        Assert.Equal((letter.X + 1, letter.Y + 1), (shadow.X, shadow.Y));
        Assert.Equal(MathF.Round(letter.X), letter.X);
        Assert.Equal(MathF.Round(letter.Y), letter.Y);
    }

    [Fact]
    public void ImagesPaintBySourceAndTeam()
    {
        var batch = Frame(() => _ui.Box(new Style { Direction = Direction.Row }, () =>
        {
            _ui.Image(new ImageSpec(ImageKind.Sprite, 16, 16, new Uv(0.25f, 0, 0.5f, 0.5f), 3));
            _ui.Coin();
            _ui.Minimap(64, 64);
        }));
        Assert.Equal([PaintKind.Sprite, PaintKind.Skin, PaintKind.Minimap, PaintKind.Fog], Kinds(batch));
        Assert.Equal(3, batch.Segments[0].Paint.Team);
        Assert.Equal((0.25f, 0f), (batch.Vertices[0].U, batch.Vertices[0].V));
        Assert.Equal((Skins.Coin / (float)Skins.Width, 8f / Skins.Height), (batch.Vertices[4].U, batch.Vertices[6].V));
        Assert.Equal(new Vertex(32, 64, 0, 1, Color.White.Rgba), batch.Vertices[11]);
    }

    [Fact]
    public void AlphaFadesNested()
    {
        var batch = Frame(() => _ui.Box(new Style { Alpha = 0.5f, Background = new Color(0, 0, 0, 255), Width = 10, Height = 10 }, () =>
            _ui.Box(new Style { Alpha = 0.5f, Background = new Color(0, 0, 0, 200), Width = 5, Height = 5 })));
        Assert.Equal(128u, batch.Vertices[0].Rgba >> 24);
        Assert.Equal(50u, batch.Vertices[4].Rgba >> 24);
    }

    [Fact]
    public void ClipNestsAndSplitsSegments()
    {
        var batch = Frame(() => _ui.Box(new Style { Anchor = new Anchor(Left: 10, Top: 10), Width = 100, Height = 100, Clip = true, Background = Color.White, Padding = 50 }, () =>
            _ui.Box(new Style { Width = 100, Height = 100, Clip = true, Background = Color.White }, () =>
                _ui.Box(new Style { Width = 10, Height = 10, Background = Color.White }))));
        // Корень сам не обрезан, ребёнок — корнем, внук — пересечением.
        Assert.Equal(new Clip?[] { null, new Clip(10, 10, 100, 100), new Clip(60, 60, 50, 50) }, batch.Segments.Select(segment => segment.Clip));
        // Узел нулевого размера не рисуется.
        var empty = Frame(() => _ui.Box(new Style { Width = 0, Height = 10, Background = Color.White }));
        Assert.Empty(empty.Indices);
    }

    [Fact]
    public void NodesAreReusedAcrossFrames()
    {
        Node? first = null;
        for (var frame = 0; frame < 3; frame++)
        {
            Frame(() => _ui.Box(new Style { Width = 10, Height = 10 }, () => _ui.Text("x")));
            if (frame == 0) first = _ui.Roots[0];
            Assert.Same(first, _ui.Roots[0]);
            Assert.Single(_ui.Roots[0].Children);
        }
    }

    [Fact]
    public void ScopeClosesNode()
    {
        _ui.Begin(Frames.Off(0));
        using (_ui.Open(new Style { Direction = Direction.Row, Gap = 2 }))
        {
            _ui.Space(10, 10);
            _ui.Space(20, 5);
        }
        _ui.Finish();
        Assert.Single(_ui.Roots);
        Assert.Equal((32f, 10f), _ui.Roots[0].Want);
    }

    [Fact]
    public void ParsesCssColors()
    {
        Assert.Equal(Color.Hex(0xf0c95a), Color.Parse("#f0c95a"));
        Assert.Equal(new Color(0x11, 0x22, 0x33, 0x44), Color.Parse("#11223344"));
        Assert.Equal(new Color(10, 20, 30, 128), Color.Parse("rgb(10 20 30 / 0.5)"));
        Assert.Equal(new Color(10, 20, 30, 255), Color.Parse(" rgba(10, 20, 30, 1) "));
        Assert.Null(Color.Parse("#12345"));
        Assert.Null(Color.Parse("red"));
        Assert.Equal(0x80302010u, new Color(0x10, 0x20, 0x30, 0x80).Rgba);
    }
}
