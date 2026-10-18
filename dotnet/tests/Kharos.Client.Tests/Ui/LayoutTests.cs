using Kharos.Client.Ui;

namespace Kharos.Client.Tests.Ui;

/// <summary>Раскладка: размеры и места вложенных коробок, привязки, перенос рядов, текст.</summary>
public sealed class LayoutTests : IDisposable
{
    private readonly Gui _ui = new(Fonts.Load());

    public void Dispose() => _ui.Fonts.Dispose();

    /// <summary>Один кадр: build строит панели, возвращает корни после раскладки.</summary>
    private IReadOnlyList<Node> Frame(Action build, float width = 800, float height = 600)
    {
        _ui.Begin(Frames.Off(0, width, height));
        build();
        _ui.Finish();
        return _ui.Roots;
    }

    private static Style Size(float width, float height) => new() { Width = width, Height = height };

    [Fact]
    public void ColumnWithPaddingAndGap()
    {
        var root = Frame(() => _ui.Box(new Style { Anchor = new Anchor(Left: 10, Top: 20), Padding = (4, 6), Gap = 2 }, () =>
        {
            _ui.Box(Size(30, 10));
            _ui.Box(Size(50, 12));
        }))[0];
        Assert.Equal(new Rect(10, 20, 62, 32), root.Rect);
        Assert.Equal(new Rect(16, 24, 30, 10), root.Children[0].Rect);
        Assert.Equal(new Rect(16, 36, 50, 12), root.Children[1].Rect);
    }

    [Fact]
    public void RowGrowSharesFreeSpace()
    {
        var root = Frame(() => _ui.Box(new Style { Width = 200, Height = 20, Direction = Direction.Row, Gap = 4 }, () =>
        {
            _ui.Box(Size(20, 10));
            _ui.Box(new Style { Grow = 1 });
            _ui.Box(new Style { Grow = 3 });
        }))[0];
        Assert.Equal(new Rect(0, 0, 20, 10), root.Children[0].Rect);
        Assert.Equal(new Rect(24, 0, 43, 0), root.Children[1].Rect);
        Assert.Equal(new Rect(71, 0, 129, 0), root.Children[2].Rect);
    }

    [Theory]
    [InlineData(Align.Start, 0, 14)]
    [InlineData(Align.Center, 33, 47)]
    [InlineData(Align.End, 66, 80)]
    [InlineData(Align.Between, 0, 80)]
    public void Justify(Align justify, float first, float second)
    {
        var root = Frame(() => _ui.Box(new Style { Width = 100, Direction = Direction.Row, Gap = 4, Justify = justify }, () =>
        {
            _ui.Box(Size(10, 10));
            _ui.Box(Size(20, 10));
        }))[0];
        Assert.Equal(first, root.Children[0].Rect.X);
        Assert.Equal(second, root.Children[1].Rect.X);
    }

    [Theory]
    [InlineData(Align.Start, 0, 40)]
    [InlineData(Align.Center, 30, 40)]
    [InlineData(Align.End, 60, 40)]
    [InlineData(Align.Stretch, 0, 100)]
    public void AlignAcross(Align align, float x, float width)
    {
        var root = Frame(() => _ui.Box(new Style { Width = 100, Align = align }, () => _ui.Box(Size(40, 10))))[0];
        Assert.Equal(new Rect(x, 0, width, 10), root.Children[0].Rect);
    }

    [Fact]
    public void StackPlacesBySelfAndGrow()
    {
        var root = Frame(() => _ui.Box(new Style { Direction = Direction.Stack, Width = 100, Height = 50, Padding = 5 }, () =>
        {
            _ui.Box(new Style { Width = 20, Height = 10, Self = (Align.Center, Align.End) });
            _ui.Box(new Style { Grow = 1 });
            _ui.Box(new Style { Width = 10, Height = 10 });
        }))[0];
        Assert.Equal(new Rect(40, 35, 20, 10), root.Children[0].Rect);
        Assert.Equal(new Rect(5, 5, 90, 40), root.Children[1].Rect);
        Assert.Equal(new Rect(5, 5, 10, 10), root.Children[2].Rect);
    }

    [Fact]
    public void Anchors()
    {
        var roots = Frame(() =>
        {
            _ui.Box(new Style { Width = 100, Height = 50, Anchor = new Anchor(Right: 10, Bottom: 20) });
            _ui.Box(new Style { Height = 38, Anchor = new Anchor(Left: 0, Right: 0, Top: 0) });
            _ui.Box(new Style { Width = 100, Height = 50, Anchor = new Anchor(CenterX: 5, CenterY: -10) });
            _ui.Box(new Style { Anchor = new Anchor(Left: 30, Right: 30, Top: 40, Bottom: 60) });
            _ui.Box(Size(7, 7));
        });
        Assert.Equal(new Rect(690, 530, 100, 50), roots[0].Rect);
        Assert.Equal(new Rect(0, 0, 800, 38), roots[1].Rect);
        Assert.Equal(new Rect(355, 265, 100, 50), roots[2].Rect);
        Assert.Equal(new Rect(30, 40, 740, 500), roots[3].Rect);
        Assert.Equal(new Rect(0, 0, 7, 7), roots[4].Rect);
    }

    [Fact]
    public void WrappingRow()
    {
        var root = Frame(() => _ui.Box(new Style { Width = 100, Direction = Direction.Row, Wrap = true, Gap = 5 }, () =>
        {
            for (var i = 0; i < 5; i++) _ui.Box(Size(40, 10));
        }))[0];
        Assert.Equal((100f, 40f), root.Want);
        float[][] expected = [[0, 0], [45, 0], [0, 15], [45, 15], [0, 30]];
        for (var i = 0; i < 5; i++) Assert.Equal(new Rect(expected[i][0], expected[i][1], 40, 10), root.Children[i].Rect);
    }

    [Fact]
    public void MinAndMax()
    {
        var roots = Frame(() =>
        {
            _ui.Box(new Style { MaxWidth = 120, MinHeight = 50 }, () => _ui.Box(Size(300, 10)));
            _ui.Box(new Style { MinWidth = 64, MaxHeight = 5 }, () => _ui.Box(Size(10, 10)));
        });
        Assert.Equal((120f, 50f), roots[0].Want);
        Assert.Equal((64f, 5f), roots[1].Want);
    }

    [Fact]
    public void PlacesOnWholePixels()
    {
        var root = Frame(() => _ui.Box(new Style { Width = 101, Direction = Direction.Row, Justify = Align.Center }, () => _ui.Box(Size(10, 10))))[0];
        // Свободно 91: сдвиг 45,5 округляется от нуля, как f32::round у пилота.
        Assert.Equal(46, root.Children[0].Rect.X);
    }

    [Fact]
    public void TextMeasuresByFontAdvance()
    {
        var root = Frame(() => _ui.Box(new Style(), () =>
        {
            _ui.Text("Меню");
            _ui.Text("1250", new TextStyle { Font = FontName.Title, Shadow = Skins.Ink, Lift = 2 });
            _ui.Text("две\nстроки", new TextStyle { Font = FontName.Small });
        }))[0];
        var fonts = _ui.Fonts;
        Assert.Equal((fonts.Width(FontName.Body, "Меню"), 16f), root.Children[0].Want);
        Assert.Equal((fonts.Width(FontName.Title, "1250") + 2, 28f), root.Children[1].Want);
        Assert.Equal(2 * 14f, root.Children[2].Want.H);
        Assert.Equal(["две", "строки"], root.Children[2].Lines);
        // Ark Pixel пропорциональный: цифры Ark Pixel 12 — по 6 пикселей, A — 8, B — 7; Ark Pixel 10 на кегле 20 —
        // вдвое шире своих 5.
        Assert.Equal(24, fonts.Width(FontName.Body, "1250"));
        Assert.Equal(27, fonts.Width(FontName.Body, "AB12"));
        Assert.Equal(40, fonts.Width(FontName.Title, "1250"));
        Assert.Equal(20, fonts.Width(FontName.Small, "1250"));
        Assert.Equal(_ui.Measure("AB12"), fonts.Width(FontName.Body, "AB12"));
    }

    [Fact]
    public void TextWrapsByWordsAndLetters()
    {
        var root = Frame(() => _ui.Box(new Style { Width = 60 }, () =>
            _ui.Text("aaa bbb ccc ddddddddddddddd", new TextStyle { Wrap = float.PositiveInfinity })))[0];
        var lines = root.Children[0].Lines;
        Assert.Equal(["aaa bbb", "ccc", "dddddddddd", "ddddd"], lines);
        Assert.All(lines, line => Assert.True(_ui.Fonts.Width(FontName.Body, line) <= 60));
    }

    [Fact]
    public void EllipsisInSqueezedRow()
    {
        const string title = "Завод боевых машин и всего на свете";
        var root = Frame(() => _ui.Box(new Style { Width = 100, Direction = Direction.Row, Gap = 4 }, () =>
        {
            _ui.Box(Size(60, 10));
            _ui.Box(new Style { Grow = 1 }, () => _ui.Text(title, new TextStyle { Ellipsis = true }));
        }))[0];
        var text = root.Children[1].Children[0];
        Assert.EndsWith("…", text.Lines[0]);
        Assert.True(text.Want.W <= 36, $"ширина {text.Want.W}");
        Assert.Equal(new Rect(64, 0, 36, 16), root.Children[1].Rect);
    }

    [Fact]
    public void ImageFitsIconByWholeScale()
    {
        var root = Frame(() => _ui.Image(new Icon(new Uv(0, 0, 0.5f, 0.5f), 10, 14), 32, 32, team: 2))[0];
        // 32 / 14 = 2,28: целым увеличением ×2 — 20 × 28, по центру.
        Assert.Equal(new Rect(0, 0, 32, 32), root.Rect);
        var image = root.Children[0];
        Assert.Equal(new Rect(6, 2, 20, 28), image.Rect);
        Assert.Equal(new ImageSpec(ImageKind.Sprite, 20, 28, new Uv(0, 0, 0.5f, 0.5f), 2), image.Image);
    }
}
