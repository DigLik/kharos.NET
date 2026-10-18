using Kharos.Client.Ui;
using Kharos.Gpu;

namespace Kharos.Client.Tests.Ui;

/// <summary>Ввод против мест прошлого кадра: наведение, нажатие, щелчок, долгое касание, подсказки, прокрутка.</summary>
public sealed class InputTests : IDisposable
{
    private readonly Gui _ui = new(Fonts.Load());

    public void Dispose() => _ui.Fonts.Dispose();

    private Click? _click;

    /// <summary>Кадр с кнопкой ok (10, 10, 80 × 30) в панели panel; disabled — недоступна.</summary>
    private void ButtonFrame(UiInput input, bool disabled = false)
    {
        _ui.Begin(input);
        _ui.Box(new Style { Id = "panel", Anchor = new Anchor(Left: 0, Top: 0), Padding = 10 }, () =>
            _click = _ui.Button("ok", Style.Button with { Width = 80, Height = 30 }, () => _ui.Text("OK"), disabled: disabled));
        _ui.Finish();
    }

    [Fact]
    public void ClickNeedsPressAndReleaseOnSameNode()
    {
        ButtonFrame(Frames.Off(0));
        Assert.Equal(new Rect(10, 10, 80, 30), _ui.RectOf("ok"));
        ButtonFrame(Frames.At(0.1, 20, 20) with { Pressed = PointerButtons.Left });
        Assert.True(_ui.Hovered("ok"));
        Assert.True(_ui.Pressing("ok"));
        Assert.False(_ui.PressedButton);
        Assert.True(_ui.HoveredButton);
        Assert.Null(_click);
        ButtonFrame(Frames.At(0.2, 25, 22) with { Released = PointerButtons.Left, Shift = true });
        Assert.Equal(new Click(0, Shift: true), _click);
        Assert.Equal(new Click(0, Shift: true), _ui.Clicked("ok"));
        ButtonFrame(Frames.At(0.3, 25, 22));
        Assert.Null(_click);
        // Отпустили мимо — щелчка нет.
        ButtonFrame(Frames.At(0.4, 20, 20) with { Pressed = PointerButtons.Left });
        ButtonFrame(Frames.At(0.5, 300, 300) with { Released = PointerButtons.Left });
        Assert.Null(_click);
    }

    [Fact]
    public void RightAndMiddleButtons()
    {
        ButtonFrame(Frames.Off(0));
        ButtonFrame(Frames.At(0.1, 20, 20) with { Pressed = PointerButtons.Right });
        Assert.False(_ui.Pressing("ok"));
        ButtonFrame(Frames.At(0.2, 20, 20) with { Released = PointerButtons.Right });
        Assert.Equal(new Click(2), _click);
        ButtonFrame(Frames.At(0.3, 20, 20) with { Pressed = PointerButtons.Middle });
        ButtonFrame(Frames.At(0.4, 20, 20) with { Released = PointerButtons.Middle, Ctrl = true });
        Assert.Equal(new Click(1, Ctrl: true), _click);
    }

    [Fact]
    public void DisabledButtonDeniesAndDoesNotClick()
    {
        ButtonFrame(Frames.Off(0), disabled: true);
        ButtonFrame(Frames.At(0.1, 20, 20) with { Pressed = PointerButtons.Left }, disabled: true);
        Assert.True(_ui.PressedButton);
        Assert.False(_ui.HoveredButton);
        ButtonFrame(Frames.At(0.2, 20, 20) with { Released = PointerButtons.Left }, disabled: true);
        Assert.Null(_click);
        Assert.NotNull(_ui.Clicked("ok"));
    }

    [Fact]
    public void ButtonSkinFollowsState()
    {
        Skin Skin() => _ui.Roots[0].Children[0].Style.Skin!.Value;
        ButtonFrame(Frames.Off(0));
        Assert.Equal(Kharos.Client.Ui.Skin.Button, Skin());
        ButtonFrame(Frames.At(0.1, 20, 20));
        Assert.Equal(Kharos.Client.Ui.Skin.ButtonHover, Skin());
        ButtonFrame(Frames.At(0.2, 20, 20) with { Pressed = PointerButtons.Left });
        Assert.Equal(Kharos.Client.Ui.Skin.ButtonActive, Skin());
        ButtonFrame(Frames.At(0.3, 20, 20), disabled: true);
        Assert.Equal(Kharos.Client.Ui.Skin.ButtonDisabled, Skin());
    }

    [Fact]
    public void LongTouchIsRightClick()
    {
        ButtonFrame(Frames.Off(0));
        ButtonFrame(Frames.At(1, 20, 20) with { Pressed = PointerButtons.Left, Touch = true });
        ButtonFrame(Frames.At(1.3, 21, 20) with { Touch = true });
        Assert.Null(_click);
        ButtonFrame(Frames.At(1.6, 21, 21) with { Touch = true });
        Assert.Equal(new Click(2, Long: true), _click);
        ButtonFrame(Frames.At(1.7, 21, 21) with { Released = PointerButtons.Left, Touch = true });
        Assert.Null(_click);
        // Палец уехал дальше допуска — долгого нажатия нет.
        ButtonFrame(Frames.At(2, 20, 20) with { Pressed = PointerButtons.Left, Touch = true });
        ButtonFrame(Frames.At(2.1, 40, 20) with { Touch = true });
        ButtonFrame(Frames.At(3, 40, 20) with { Touch = true });
        Assert.Null(_click);
    }

    [Fact]
    public void WithinHeldAndCovers()
    {
        void Frame(UiInput input)
        {
            _ui.Begin(input);
            _ui.Box(new Style { Id = "panel", Anchor = new Anchor(Left: 100, Top: 100), Padding = 10 }, () =>
                _ui.Box(new Style { Width = 50, Height = 20 }, () => _ui.Box(new Style { Id = "item", Width = 40, Height = 20 })));
            _ui.Box(new Style { Anchor = new Anchor(Left: 0, Top: 500), Width = 50, Height = 50 });
            _ui.Box(new Style { Anchor = new Anchor(Left: 600, Top: 500), Width = 50, Height = 50, Passive = true });
            _ui.Finish();
        }
        Frame(Frames.Off(0));
        Frame(Frames.At(0.1, 120, 120) with { Pressed = PointerButtons.Left });
        Assert.True(_ui.Hovered("item"));
        Assert.False(_ui.Hovered("panel"));
        Assert.True(_ui.Within("panel"));
        Assert.True(_ui.Within("item"));
        Assert.Equal((0.25f, 0.5f), _ui.Held("item"));
        Assert.Null(_ui.Held("panel"));
        Assert.True(_ui.Covers(105, 105));
        Assert.True(_ui.Covers(10, 510));
        Assert.False(_ui.Covers(610, 510));
        Assert.False(_ui.Covers(400, 300));
        Frame(Frames.At(0.2, 10, 510));
        Assert.True(_ui.Covered);
        Frame(Frames.At(0.3, 610, 510));
        Assert.False(_ui.Covered);
    }

    private void TipFrame(UiInput input, Anchor? anchor = null, bool card = false)
    {
        _ui.Begin(input);
        _ui.Box(new Style { Id = "b", Anchor = new Anchor(Left: 100, Top: 300), Width = 50, Height = 20 });
        _ui.Box(new Style { Id = "edge", Anchor = new Anchor(Left: 700, Top: 0), Width = 100, Height = 40 });
        foreach (var id in new[] { "b", "edge" })
        {
            if (card) _ui.Tip(id, () => _ui.Box(new Style { Width = 100, Height = 30 }), anchor);
            else _ui.Tip(id, "Подсказка");
        }
        _ui.Finish();
    }

    [Fact]
    public void TipAppearsAfterDelayNearPointer()
    {
        TipFrame(Frames.Off(0));
        TipFrame(Frames.At(1, 110, 310));
        Assert.Equal(2, _ui.Roots.Count);
        Assert.False(_ui.TipDue("b"));
        TipFrame(Frames.At(1.2, 110, 310));
        Assert.Equal(2, _ui.Roots.Count);
        TipFrame(Frames.At(1.4, 110, 310));
        Assert.Equal(3, _ui.Roots.Count);
        var tip = _ui.Roots[2];
        var width = _ui.Fonts.Width(FontName.Body, "Подсказка") + 20;
        Assert.Equal(new Rect(124, 310 - 28 - 12, width, 28), tip.Rect);
        Assert.True(tip.Style.Passive);
        Assert.Equal(Skin.Panel, tip.Style.Skin);
        // Нажатие прячет подсказку.
        TipFrame(Frames.At(1.5, 110, 310) with { Pressed = PointerButtons.Left });
        Assert.Equal(2, _ui.Roots.Count);
    }

    [Fact]
    public void TipFlipsAtScreenEdges()
    {
        TipFrame(Frames.Off(0));
        TipFrame(Frames.At(1, 790, 5));
        TipFrame(Frames.At(2, 790, 5));
        var tip = _ui.Roots[^1];
        var width = _ui.Fonts.Width(FontName.Body, "Подсказка") + 20;
        // Справа не влезает — левее указателя; сверху не влезает — ниже указателя.
        Assert.Equal(790 - 14 - width, tip.Rect.X);
        Assert.Equal(25, tip.Rect.Y);
    }

    [Fact]
    public void CardTipWithAnchor()
    {
        TipFrame(Frames.Off(0), new Anchor(Right: 8, Bottom: 100), card: true);
        TipFrame(Frames.At(1, 110, 310), new Anchor(Right: 8, Bottom: 100), card: true);
        TipFrame(Frames.At(2, 110, 310), new Anchor(Right: 8, Bottom: 100), card: true);
        var tip = _ui.Roots[^1];
        // Панель подсказки: отступы 6 × 10 вокруг карточки 100 × 30.
        Assert.Equal(new Rect(800 - 8 - 120, 600 - 100 - 42, 120, 42), tip.Rect);
        Assert.Equal(new Rect(800 - 8 - 110, 600 - 100 - 36, 100, 30), tip.Children[0].Rect);
    }

    [Fact]
    public void UnclosedNodesAndTipsCloseAtFinish()
    {
        TipFrame(Frames.Off(0));
        TipFrame(Frames.At(1, 110, 310));
        _ui.Begin(Frames.At(2, 110, 310));
        _ui.Open(new Style { Id = "b", Anchor = new Anchor(Left: 100, Top: 300), Width = 50, Height = 20 });
        Assert.True(_ui.TipDue("b"));
        _ui.BeginTip();
        _ui.Open(new Style());
        _ui.Text("незакрытая");
        _ui.Finish();
        Assert.Equal(2, _ui.Roots.Count);
        Assert.Equal("незакрытая", _ui.Roots[1].Children[0].Children[0].Text);
        Assert.True(_ui.Roots[1].Style.Passive);
    }

    private void ListFrame(UiInput input)
    {
        _ui.Begin(input);
        _ui.Box(new Style { Id = "list", Anchor = new Anchor(Left: 10, Top: 10), Width = 100, Height = 50, Scroll = true }, () =>
        {
            for (var i = 0; i < 10; i++) _ui.Box(new Style { Id = $"row:{i}", Width = 100, Height = 20, Background = Color.Hex(0x203040) });
        });
        _ui.Finish();
    }

    [Fact]
    public void WheelScrollsNearestListAndClips()
    {
        ListFrame(Frames.Off(0));
        var list = _ui.Roots[0];
        Assert.Equal(new Rect(10, 10, 100, 20), list.Children[0].Rect);
        Assert.Equal(new Rect(10, 50, 100, 20), list.Children[2].Rect);
        // Видимая часть строки — по обрезке списка.
        Assert.Equal(new Rect(10, 50, 100, 10), _ui.RectOf("row:2"));
        Assert.Equal(new Rect(10, 70, 100, 0), _ui.RectOf("row:3"));
        ListFrame(Frames.At(1, 50, 30) with { Wheel = -1 });
        Assert.Equal(new Rect(10, -30, 100, 20), _ui.Roots[0].Children[0].Rect);
        ListFrame(Frames.At(2, 50, 30) with { Wheel = -10 });
        Assert.Equal(10 - 150, _ui.Roots[0].Children[0].Rect.Y);
        ListFrame(Frames.At(3, 50, 30) with { Wheel = 2 });
        Assert.Equal(10 - 70, _ui.Roots[0].Children[0].Rect.Y);
        // Заливки строк обрезаны прямоугольником списка.
        var segments = _ui.Batch.Segments;
        Assert.Single(segments);
        Assert.Equal(new Clip(10, 10, 100, 50), segments[0].Clip);
        Assert.Equal(PaintKind.Font, segments[0].Paint.Kind);
    }
}
