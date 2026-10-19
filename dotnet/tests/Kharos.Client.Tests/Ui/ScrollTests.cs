using Kharos.Client.Ui;
using Kharos.Gpu;

namespace Kharos.Client.Tests.Ui;

/// <summary>Прокручиваемые списки: колесо, полоса, обрезка детей, держащийся низа список.</summary>
public sealed class ScrollTests : IDisposable
{
    private readonly Gui _ui = new(Fonts.Load());

    public void Dispose() => _ui.Fonts.Dispose();

    private int _rows = 10;

    private static (Rect Track, Rect Thumb) Bar(Node node) => (node.BarTrack, node.BarThumb);

    /// <summary>Список list (0, 0, 100 × 60) из rows строк по 20 пикселей.</summary>
    private void Frame(UiInput input, bool end = false)
    {
        _ui.Begin(input);
        _ui.Box(new Style { Id = "list", Anchor = new Anchor(Left: 0, Top: 0), Width = 100, Height = 60, Scroll = true, ScrollEnd = end, Align = Align.Stretch }, () =>
        {
            for (var i = 0; i < _rows; i++)
                _ui.Box(new Style { Id = $"row:{i}", Height = 20, Background = Color.Hex(0x224466) });
        });
        _ui.Finish();
    }

    private Rect Row(int index) => _ui.RectOf($"row:{index}")!.Value;

    [Fact]
    public void ChildrenAreClippedToTheList()
    {
        Frame(Frames.Off(0));
        Frame(Frames.Off(0.1));
        // Видны три строки целиком; четвёртая за краем — места у неё нет.
        Assert.Equal(new Rect(0, 0, 100, 20), Row(2) with { Y = Row(2).Y - 40 });
        Assert.Equal(0, Row(3).H);
        Assert.Equal(0, Row(9).H);
        // Сдвиг на 15: верхняя строка обрезана до пяти пикселей, нижняя — доли строки.
        _ui.ScrollTo("list", 15);
        Frame(Frames.Off(0.2));
        Assert.Equal(new Rect(0, 0, 100, 5), Row(0));
        Assert.Equal(new Rect(0, 5, 100, 20), Row(1));
        Assert.Equal(new Rect(0, 45, 100, 15), Row(3));
        Assert.Equal(0, Row(4).H);
        // Рисование ведётся с обрезкой по списку.
        Assert.Contains(_ui.Batch.Segments, segment => segment.Clip == new Clip(0, 0, 100, 60));
    }

    [Fact]
    public void ClippedRowDoesNotTakeThePointer()
    {
        Frame(Frames.Off(0));
        Frame(Frames.At(0.1, 50, 90));
        Assert.Null(_ui.Hits.FirstOrDefault(hit => hit.Rect.Contains(50, 90) && hit.Id.StartsWith("row", StringComparison.Ordinal)).Id);
        Assert.False(_ui.Covered);
        Frame(Frames.At(0.2, 50, 30));
        Assert.True(_ui.Hovered("row:1"));
    }

    [Fact]
    public void WheelScrollsAndClampsToTheContent()
    {
        Frame(Frames.Off(0));
        Frame(Frames.At(0.1, 50, 30) with { Wheel = -1 });
        Assert.Equal(Gui.WheelLine, _ui.ScrollOf("list"));
        Assert.Equal(-Gui.WheelLine, _ui.Roots[0].Children[0].Rect.Y);
        Frame(Frames.At(0.2, 50, 30) with { Wheel = -10 });
        // 10 × 20 − 60 = 140 — дальше не едет.
        Assert.Equal(140, _ui.ScrollOf("list"));
        Frame(Frames.At(0.3, 50, 30) with { Wheel = 100 });
        Assert.Equal(0, _ui.ScrollOf("list"));
        // Мимо списка колесо ему не достаётся.
        Frame(Frames.At(0.4, 500, 500) with { Wheel = -1 });
        Assert.Equal(0, _ui.ScrollOf("list"));
    }

    [Fact]
    public void ScrollbarAppearsOnlyWhenContentIsTaller()
    {
        _rows = 3;
        Frame(Frames.Off(0));
        Frame(Frames.Off(0.1));
        Assert.Null(_ui.RectOf(Gui.BarId("list")));
        Assert.False(_ui.Roots[0].HasBar);
        _rows = 10;
        Frame(Frames.Off(0.2));
        Frame(Frames.Off(0.3));
        var track = _ui.RectOf(Gui.BarId("list"))!.Value;
        // Русло у правого края, на всю высоту списка без отступов; запас нажатия слева.
        Assert.Equal(60 - Gui.BarMargin * 2, track.H);
        Assert.Equal(100 - Gui.BarMargin, track.X + track.W);
        Assert.True(_ui.Roots[0].HasBar);
        var (trackNode, thumb) = Bar(_ui.Roots[0]);
        // Бегунок — доля видимого содержимого: 60 из 200.
        Assert.Equal(trackNode.H * 60 / 200, thumb.H, 0.01);
        Assert.Equal(trackNode.Y, thumb.Y);
        _ui.ScrollTo("list", 140);
        Frame(Frames.Off(0.4));
        Assert.Equal(trackNode.Y + trackNode.H, Bar(_ui.Roots[0]).Thumb.Y + thumb.H, 0.01);
    }

    [Fact]
    public void ScrollbarIsDrawnOverTheContent()
    {
        Frame(Frames.Off(0));
        Frame(Frames.Off(0.1));
        var node = _ui.Roots[0];
        var (track, thumb) = Bar(node);
        var quads = _ui.Batch.Vertices.Count;
        Assert.True(quads > 0);
        Assert.Contains(NodeColors(), color => color == Gui.BarTrackColor.Rgba);
        Assert.Contains(NodeColors(), color => color == Gui.BarThumbColor.Rgba);
        Assert.True(thumb.H >= Gui.BarMinThumb && thumb.H <= track.H);
    }

    private IEnumerable<uint> NodeColors() => _ui.Batch.Vertices.Select(vertex => vertex.Rgba);

    [Fact]
    public void DraggingTheThumbScrolls()
    {
        Frame(Frames.Off(0));
        Frame(Frames.Off(0.1));
        var (track, thumb) = Bar(_ui.Roots[0]);
        var x = track.X + 2;
        var grab = thumb.Y + thumb.H / 2;
        Frame(Frames.At(0.2, x, grab) with { Pressed = PointerButtons.Left });
        Assert.True(_ui.Pressing(Gui.BarId("list")));
        // Потянули на половину хода — список на половину своего запаса.
        var travel = track.H - thumb.H;
        Frame(Frames.At(0.3, x, grab + travel / 2));
        Assert.Equal(70, _ui.ScrollOf("list"), 0.5);
        Frame(Frames.At(0.4, x, grab + travel * 3));
        Assert.Equal(140, _ui.ScrollOf("list"), 0.01);
        Frame(Frames.At(0.5, x, grab - 500));
        Assert.Equal(0, _ui.ScrollOf("list"), 0.01);
        // Отпустили — бегунок отпущен, список на месте.
        Frame(Frames.At(0.6, x, grab) with { Released = PointerButtons.Left });
        Frame(Frames.At(0.7, x, grab + 30));
        Assert.Equal(0, _ui.ScrollOf("list"), 0.01);
    }

    [Fact]
    public void PressingTheTrackCentersTheThumbUnderThePointer()
    {
        Frame(Frames.Off(0));
        Frame(Frames.Off(0.1));
        var (track, thumb) = Bar(_ui.Roots[0]);
        var middle = track.Y + track.H / 2;
        Frame(Frames.At(0.2, track.X + 2, middle) with { Pressed = PointerButtons.Left });
        Assert.Equal(70, _ui.ScrollOf("list"), 0.5);
        Assert.True(thumb.H < track.H);
    }

    [Fact]
    public void WheelOverTheBarAlsoScrolls()
    {
        Frame(Frames.Off(0));
        Frame(Frames.Off(0.1));
        var (track, _) = Bar(_ui.Roots[0]);
        Frame(Frames.At(0.2, track.X + 1, track.Y + 5) with { Wheel = -1 });
        Assert.Equal(Gui.WheelLine, _ui.ScrollOf("list"));
    }

    [Fact]
    public void EndListFollowsNewContentUntilScrolledAway()
    {
        _rows = 10;
        Frame(Frames.Off(0), end: true);
        Assert.Equal(140, _ui.ScrollOf("list"));
        _rows = 12;
        Frame(Frames.Off(0.1), end: true);
        Assert.Equal(180, _ui.ScrollOf("list"));
        // Отвели вверх — новые строки низ не утягивают.
        Frame(Frames.At(0.2, 50, 30) with { Wheel = 1 }, end: true);
        Assert.Equal(140, _ui.ScrollOf("list"));
        _rows = 14;
        Frame(Frames.Off(0.3), end: true);
        Assert.Equal(140, _ui.ScrollOf("list"));
        // Вернули вниз — снова держится.
        Frame(Frames.At(0.4, 50, 30) with { Wheel = -5 }, end: true);
        Assert.Equal(220, _ui.ScrollOf("list"));
        _rows = 16;
        Frame(Frames.Off(0.5), end: true);
        Assert.Equal(260, _ui.ScrollOf("list"));
    }

    [Fact]
    public void NestedListScrollsInsteadOfOuter()
    {
        void Nested(UiInput input)
        {
            _ui.Begin(input);
            _ui.Box(new Style { Id = "outer", Anchor = new Anchor(Left: 0, Top: 0), Width = 100, Height = 100, Scroll = true, Align = Align.Stretch }, () =>
            {
                _ui.Box(new Style { Id = "inner", Height = 40, Scroll = true, Align = Align.Stretch }, () =>
                {
                    for (var i = 0; i < 4; i++) _ui.Box(new Style { Height = 20 });
                });
                for (var i = 0; i < 6; i++) _ui.Box(new Style { Height = 20 });
            });
            _ui.Finish();
        }
        Nested(Frames.Off(0));
        Nested(Frames.At(0.1, 30, 10) with { Wheel = -1 });
        Assert.Equal(Gui.WheelLine, _ui.ScrollOf("inner"));
        Assert.Equal(0, _ui.ScrollOf("outer"));
        Nested(Frames.At(0.2, 30, 80) with { Wheel = -1 });
        Assert.Equal(Gui.WheelLine, _ui.ScrollOf("outer"));
    }
}
