using Kharos.Client.Game;
using Kharos.Client.Ui;
using Kharos.Protocol;
using S = Kharos.Mod.Sdk.Client;

namespace Kharos.Client.Tests.Ui;

/// <summary>Модальное окно: затемнение, перехват ввода, закрытие по Escape, молчание модов под ним.</summary>
public sealed class ModalTests : IDisposable
{
    private readonly Gui _ui = new(Fonts.Load());

    public void Dispose() => _ui.Fonts.Dispose();

    private bool _open = true;
    private bool _closeOnBackdrop;
    private bool _closed;
    private Click? _below, _inside;

    /// <summary>Кнопка below (10, 10, 80 × 30) под окном и окно m 200 × 100 по центру с кнопкой ok и полем.</summary>
    private void Frame(UiInput input, bool field = false)
    {
        _ui.Begin(input);
        _ui.Box(new Style { Id = "world", Anchor = new Anchor(Left: 0, Top: 0), Padding = 10 }, () =>
            _below = _ui.Button("below", Style.Button with { Width = 80, Height = 30 }, () => _ui.Text("A")));
        if (_open)
        {
            _closed = _ui.Modal("m", new Style { Width = 200, Height = 100, Padding = 10, Skin = Skin.Panel }, () =>
            {
                _inside = _ui.Button("ok", Style.Button with { Width = 60, Height = 24 }, () => _ui.Text("OK"));
                if (field) _ui.Input("name", "", new FieldOptions { Width = 120 });
            }, _closeOnBackdrop);
        }
        else _closed = false;
        _ui.Finish();
    }

    [Fact]
    public void PanelIsCenteredOverADimmedScreen()
    {
        Frame(Frames.Off(0));
        var dim = _ui.Roots[^1];
        Assert.Equal(new Rect(0, 0, 800, 600), dim.Rect);
        Assert.Equal(Gui.Dim, dim.Style.Background);
        Assert.Equal(new Rect(300, 250, 200, 100), dim.Children[0].Rect);
        // Окно после обычных панелей, даже если описано раньше.
        Assert.Equal("world", _ui.Roots[0].Style.Id);
        Assert.Contains(_ui.Batch.Vertices, vertex => vertex.Rgba == Gui.Dim.Rgba);
    }

    [Fact]
    public void ModalRootsAreStackedAboveOthersAndTipsAboveModals()
    {
        _ui.Begin(Frames.At(2, 20, 20));
        _ui.Modal("m", default, () => _ui.Text("x"));
        _ui.Box(new Style { Id = "late", Anchor = new Anchor(Left: 0, Top: 0) }, () => _ui.Text("late"));
        _ui.Tip("late", "tip");
        _ui.Finish();
        Assert.Equal(["late", "m"], _ui.Roots.Select(root => root.Style.Id));
    }

    [Fact]
    public void InputBelowTheModalIsCaptured()
    {
        Frame(Frames.Off(0));
        Frame(Frames.At(0.1, 20, 20));
        Assert.True(_ui.ModalOpen);
        Assert.Equal("m", _ui.ModalId);
        Assert.False(_ui.Hovered("below"));
        Assert.True(_ui.Covered);
        Assert.True(_ui.Covers(790, 590));
        // Нажатие и отпускание над кнопкой под окном щелчка не дают.
        Frame(Frames.At(0.2, 20, 20) with { Pressed = PointerButtons.Left });
        Assert.False(_ui.PressedButton.HasValue);
        Frame(Frames.At(0.3, 20, 20) with { Released = PointerButtons.Left });
        Assert.Null(_below);
        Assert.Null(_ui.Clicked("below"));
        // Колесо мира тоже не достаётся списку под окном — а кнопка в окне работает.
        Frame(Frames.At(0.4, 320, 262));
        Assert.True(_ui.Hovered("ok"));
        Frame(Frames.At(0.5, 320, 262) with { Pressed = PointerButtons.Left });
        Frame(Frames.At(0.6, 320, 262) with { Released = PointerButtons.Left });
        Assert.NotNull(_inside);
    }

    [Fact]
    public void ClosingModalReleasesTheInput()
    {
        Frame(Frames.Off(0));
        _open = false;
        Frame(Frames.Off(0.1));
        Frame(Frames.At(0.2, 20, 20));
        Assert.False(_ui.ModalOpen);
        Assert.Null(_ui.ModalId);
        Assert.True(_ui.Hovered("below"));
    }

    [Fact]
    public void EscapeAsksToClose()
    {
        Frame(Frames.Off(0));
        Assert.False(_closed);
        Frame(Frames.Off(0.1) with { Hits = ["a"] });
        Assert.False(_closed);
        Frame(Frames.Off(0.2) with { Hits = ["escape"] });
        Assert.True(_closed);
        // Окно само не исчезает: решает тот, кто его описывает.
        Assert.True(_ui.ModalOpen);
    }

    [Fact]
    public void OnlyTopmostModalClosesOnEscape()
    {
        var closed = new List<string>();
        void Two(UiInput input)
        {
            _ui.Begin(input);
            if (_ui.Modal("first", default, () => _ui.Text("1"))) closed.Add("first");
            if (_ui.Modal("second", default, () => _ui.Text("2"))) closed.Add("second");
            _ui.Finish();
        }
        Two(Frames.Off(0));
        Assert.Equal("second", _ui.ModalId);
        Two(Frames.Off(0.1) with { Hits = ["escape"] });
        Assert.Equal(["second"], closed);
    }

    [Fact]
    public void BackdropClickClosesOnlyWhenAsked()
    {
        Frame(Frames.Off(0));
        Frame(Frames.At(0.1, 50, 400) with { Pressed = PointerButtons.Left });
        Frame(Frames.At(0.2, 50, 400) with { Released = PointerButtons.Left });
        Assert.False(_closed);
        _closeOnBackdrop = true;
        Frame(Frames.At(0.3, 50, 400) with { Pressed = PointerButtons.Left });
        Frame(Frames.At(0.4, 50, 400) with { Released = PointerButtons.Left });
        Assert.True(_closed);
        // Щелчок по самой панели окно не закрывает.
        Frame(Frames.At(0.5, 400, 300) with { Pressed = PointerButtons.Left });
        Frame(Frames.At(0.6, 400, 300) with { Released = PointerButtons.Left });
        Assert.False(_closed);
    }

    [Fact]
    public void FieldInsideTheModalTypesAndEscapeStillReachesIt()
    {
        Frame(Frames.Off(0), field: true);
        Frame(Frames.At(0.1, 330, 290) with { Pressed = PointerButtons.Left }, field: true);
        Frame(Frames.At(0.2, 330, 290) with { Released = PointerButtons.Left }, field: true);
        Assert.Equal("name", _ui.Focused);
        Frame(Frames.Off(0.3) with { Text = "hi" }, field: true);
        Assert.Equal([("name", "hi")], _ui.FrameEdits);
        Frame(Frames.Off(0.4) with { Hits = ["escape"] }, field: true);
        Assert.True(_closed);
    }

    // ------------------------------------------------------------------ моды под окном молчат

    private (bool OverUi, int Keys, string Hits, string Text, float Wheel, bool Pressed, bool Released) ModFrame(ClientInput input)
    {
        var scene = new ClientScene(_ui);
        var writer = new AbiWriter();
        _ui.Begin(input.ToUi());
        new FrameInput().Write(writer, new FrameClock(1, 1 / 60.0, 3, 0, 0.05, 1), input, scene.Camera, _ui, scene.Atmosphere, [], null, []);
        (bool OverUi, int Keys, string Hits, string Text, float Wheel, bool Pressed, bool Released) seen = default;
        new S.ClientContext().Frame(writer.Written, c =>
            seen = (c.Input.OverUi, c.Input.Keys.Count, string.Join(',', c.Input.Hits.Order()), c.Input.Text, c.Input.Wheel,
                c.Input.Pressed(S.PointerButton.Left), c.Input.Released(S.PointerButton.Left)));
        if (_open)
            _ui.Modal("m", default, () => _ui.Text("x"));
        _ui.Finish();
        return seen;
    }

    [Fact]
    public void ModsDoNotSeeKeysClicksOrWheelUnderAModal()
    {
        var world = new ClientInput
        {
            Width = 800, Height = 600, Time = 0.1, Pointer = (500, 500), Keys = ["w"], Hits = ["w", "escape"], Text = "w",
            Wheel = 1, Pressed = PointerButtons.Left, Released = PointerButtons.Left, Down = PointerButtons.Left,
        };
        var before = ModFrame(world);
        Assert.Equal((false, 1, "escape,w", "w", 1f, true, true), before);
        Assert.True(_ui.ModalOpen);
        var under = ModFrame(world with { Time = 0.2 });
        // Над интерфейсом везде, клавиши, текст, колесо и нажатие мира — пусто; отпускание мод видит (чтобы не залипнуть).
        Assert.Equal((true, 0, "", "", 0f, false, true), under);
    }
}
