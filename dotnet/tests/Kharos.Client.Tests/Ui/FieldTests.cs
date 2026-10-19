using Kharos.Client.Game;
using Kharos.Client.Ui;
using Kharos.Protocol;
using S = Kharos.Mod.Sdk.Client;

namespace Kharos.Client.Tests.Ui;

/// <summary>Поле ввода: фокус, каретка, выделение, правка клавишами и буфером обмена, правки во входе кадра, молчание клавиш игры.</summary>
public sealed class FieldTests : IDisposable
{
    private readonly Gui _ui = new(Fonts.Load());

    public void Dispose() => _ui.Fonts.Dispose();

    private Field _field;
    private string _value = "";
    private double _time;

    private double Next() => _time += 0.016;

    /// <summary>Кадр с полем f (0, 0, 200 × 28) в панели; значение панель держит сама, как мод.</summary>
    private Field Frame(UiInput input, FieldOptions options = default, bool echo = true)
    {
        _ui.Begin(input);
        // Как мод: правки кадра приходят до описания поля и сразу возвращаются в него.
        if (echo)
            foreach (var (id, value) in _ui.FrameEdits)
                if (id == "f") _value = value;
        _ui.Box(new Style { Id = "panel", Anchor = new Anchor(Left: 0, Top: 0) }, () =>
        {
            _field = _ui.Input("f", _value, options with { Width = 200 });
            _ui.Box(new Style { Id = "other", Width = 50, Height = 20 });
        });
        _ui.Finish();
        return _field;
    }

    private static UiInput Hit(double time, params string[] keys) => Frames.Off(time) with { Hits = keys };

    private static UiInput Type(double time, string text) => Frames.Off(time) with { Text = text };

    /// <summary>Нажатие и отпускание левой кнопкой в точке — двумя кадрами.</summary>
    private void Click(float x, float y)
    {
        Frame(Frames.At(Next(), x, y) with { Pressed = PointerButtons.Left });
        Frame(Frames.At(Next(), x, y) with { Released = PointerButtons.Left });
    }

    private void Focus()
    {
        Frame(Frames.Off(Next()));
        Click(20, 14);
    }

    [Fact]
    public void ClickFocusesAndOutsideClickBlurs()
    {
        Frame(Frames.Off(Next()));
        Assert.False(_ui.Typing);
        Click(20, 14);
        Assert.True(_ui.Typing);
        Assert.Equal("f", _ui.Focused);
        Assert.True(Frame(Frames.Off(Next())).Focused);
        Click(300, 300);
        Assert.False(_ui.Typing);
        Assert.False(Frame(Frames.Off(Next())).Focused);
    }

    [Fact]
    public void TypingFillsValueAndReportsEdits()
    {
        Focus();
        Frame(Type(Next(), "при"));
        Assert.Equal([("f", "при")], _ui.FrameEdits);
        Frame(Type(Next(), "вет"));
        Assert.Equal("привет", _value);
        Assert.Equal([("f", "привет")], _ui.FrameEdits);
        // Кадр без правок — без записи.
        Frame(Frames.Off(Next()));
        Assert.Empty(_ui.FrameEdits);
    }

    [Fact]
    public void UnfocusedFieldIgnoresTyping()
    {
        Frame(Frames.Off(Next()));
        Frame(Type(Next(), "abc"));
        Assert.Equal("", _value);
        Assert.Empty(_ui.FrameEdits);
    }

    [Fact]
    public void CaretMovesAndEdits()
    {
        Focus();
        Frame(Type(Next(), "hello"));
        Frame(Hit(Next(), "left"));
        Frame(Hit(Next(), "left"));
        Assert.Equal(3, _ui.CaretOf("f"));
        Frame(Hit(Next(), "backspace"));
        Assert.Equal("helo", _value);
        Assert.Equal(2, _ui.CaretOf("f"));
        Frame(Hit(Next(), "delete"));
        Assert.Equal("heo", _value);
        Frame(Type(Next(), "LL"));
        Assert.Equal("heLLo", _value);
        Frame(Hit(Next(), "home"));
        Assert.Equal(0, _ui.CaretOf("f"));
        Frame(Hit(Next(), "backspace"));
        Assert.Equal("heLLo", _value);
        Frame(Hit(Next(), "end"));
        Assert.Equal(5, _ui.CaretOf("f"));
        Frame(Hit(Next(), "delete"));
        Assert.Equal("heLLo", _value);
    }

    [Fact]
    public void ShiftSelectsAndTypingReplacesSelection()
    {
        Focus();
        Frame(Type(Next(), "hello world"));
        Frame(Hit(Next(), "home") with { Shift = true });
        Assert.Equal((0, 11), _ui.Selection("f"));
        // Выделение рисуется синей подложкой.
        Assert.Contains(_ui.Roots[0].Children[0].Children[0].Children, node => node.Style.Background == Gui.SelectionColor);
        Frame(Type(Next(), "x"));
        Assert.Equal("x", _value);
        Frame(Type(Next(), "yz"));
        Frame(Hit(Next(), "left") with { Shift = true });
        Frame(Hit(Next(), "left") with { Shift = true });
        Assert.Equal((1, 3), _ui.Selection("f"));
        // Стрелка без Shift сжимает выделение к краю.
        Frame(Hit(Next(), "left"));
        Assert.Equal((1, 1), _ui.Selection("f"));
    }

    [Fact]
    public void ClipboardCopyCutPaste()
    {
        Focus();
        Frame(Type(Next(), "abcdef"));
        Frame(Hit(Next(), "a") with { Ctrl = true });
        Assert.Equal((0, 6), _ui.Selection("f"));
        Frame(Hit(Next(), "c") with { Ctrl = true });
        Assert.Equal("abcdef", _ui.Clipboard.Get());
        Frame(Hit(Next(), "x") with { Ctrl = true });
        Assert.Equal("", _value);
        _ui.Clipboard.Set("one\r\ntwo\u0007");
        Frame(Hit(Next(), "v") with { Ctrl = true });
        Assert.Equal("one two", _value);
        Frame(Hit(Next(), "v") with { Ctrl = true });
        Assert.Equal("one twoone two", _value);
        // Ctrl+буква не печатается, даже если оболочка прислала и символ.
        Frame(Hit(Next(), "z") with { Ctrl = true, Text = "z" });
        Assert.Equal("one twoone two", _value);
    }

    [Fact]
    public void DigitsAndMaxLengthFilterInput()
    {
        var options = new FieldOptions { Digits = true, MaxLength = 4 };
        Frame(Frames.Off(Next()), options);
        Frame(Frames.At(Next(), 20, 14) with { Pressed = PointerButtons.Left }, options);
        Frame(Type(Next(), "1a2b3c4d5"), options);
        Assert.Equal("1234", _value);
        Frame(Type(Next(), "9"), options);
        Assert.Equal("1234", _value);
    }

    [Fact]
    public void EnterEscapeTabAndArrowsGoToThePanel()
    {
        Focus();
        Frame(Type(Next(), "ab"));
        var field = Frame(Hit(Next(), "enter", "escape", "up", "down", "tab", "q"));
        Assert.Equal(["enter", "escape", "up", "down", "tab"], field.Keys);
        Assert.Equal("ab", field.Value);
        Assert.Empty(_ui.FrameEdits);
    }

    [Fact]
    public void HeldKeyRepeats()
    {
        Focus();
        Frame(Type(Next(), "abcdefghij"));
        var time = Next();
        Frame(Hit(time, "backspace") with { Keys = ["backspace"] });
        Assert.Equal("abcdefghi", _value);
        // До срока повтора ничего не стирается.
        Frame(Frames.Off(time + 0.2) with { Keys = ["backspace"] });
        Assert.Equal("abcdefghi", _value);
        Frame(Frames.Off(time + 0.41) with { Keys = ["backspace"] });
        Assert.Equal("abcdefgh", _value);
        Frame(Frames.Off(time + 0.50) with { Keys = ["backspace"] });
        Assert.Equal("abcdefg", _value);
        Frame(Frames.Off(time + 0.51) with { Keys = [] });
        Frame(Frames.Off(time + 0.90) with { Keys = ["backspace"] });
        Assert.Equal("abcdefg", _value);
    }

    [Fact]
    public void ClickPlacesCaretAndDragSelects()
    {
        Focus();
        Frame(Type(Next(), "abcdef"));
        float At(string text) => 8 + _ui.Fonts.Width(FontName.Body, text);
        // Нажатие между c и d.
        Frame(Frames.At(Next(), At("abc") + 1, 14) with { Pressed = PointerButtons.Left });
        Assert.Equal(3, _ui.CaretOf("f"));
        Assert.Equal((3, 3), _ui.Selection("f"));
        Frame(Frames.At(Next(), At("abcde") + 1, 14));
        Assert.Equal((3, 5), _ui.Selection("f"));
    }

    [Fact]
    public void PanelCanReplaceValueWhileFocused()
    {
        Focus();
        Frame(Type(Next(), "send me"));
        // Панель сама очистила значение (отправила): поле берёт его, каретка в начале.
        _value = "";
        Frame(Frames.Off(Next()), echo: false);
        Assert.Equal("", _field.Value);
        Assert.Equal(0, _ui.CaretOf("f"));
        Assert.True(_field.Focused);
        // Панель, что держит прежнее значение, не мешает набирать.
        _value = "x";
        Frame(Frames.Off(Next()), echo: false);
        Frame(Type(Next(), "yz"), echo: false);
        Assert.Equal("xyz", _field.Value);
        Frame(Frames.Off(Next()), echo: false);
        Assert.Equal("xyz", _field.Value);
    }

    [Fact]
    public void FocusOptionAndDroppedFieldLoseFocus()
    {
        Frame(Frames.Off(Next()), new FieldOptions { Focus = true });
        Assert.True(_ui.Typing);
        // Поле перестали описывать — фокус и правка пропали.
        _ui.Begin(Frames.Off(Next()));
        _ui.Finish();
        Assert.False(_ui.Typing);
        Assert.Null(_ui.CaretOf("f"));
    }

    [Fact]
    public void KeepFocusNodeDoesNotBlur()
    {
        var first = true;
        void Build()
        {
            _ui.Box(new Style { Anchor = new Anchor(Left: 0, Top: 0) }, () =>
            {
                _ui.Input("f", "", new FieldOptions { Width = 200, Focus = first });
                _ui.Box(new Style { Id = "hint", Width = 50, Height = 20, KeepFocus = true });
                _ui.Box(new Style { Id = "plain", Width = 50, Height = 20 });
            });
            _ui.Finish();
            first = false;
        }
        _ui.Begin(Frames.Off(0));
        Build();
        Assert.True(_ui.Typing);
        // Нажатие на узел с KeepFocus (y 28..48) фокус не снимает, на обычный (48..68) — снимает.
        _ui.Begin(Frames.At(0.1, 10, 40) with { Pressed = PointerButtons.Left });
        Build();
        Assert.True(_ui.Typing);
        _ui.Begin(Frames.At(0.2, 10, 60) with { Pressed = PointerButtons.Left });
        Build();
        Assert.False(_ui.Typing);
    }

    [Fact]
    public void PasswordShowsBullets()
    {
        var options = new FieldOptions { Password = true };
        Frame(Frames.Off(Next()), options);
        Frame(Frames.At(Next(), 20, 14) with { Pressed = PointerButtons.Left }, options);
        Frame(Type(Next(), "secret"), options);
        Assert.Equal("secret", _value);
        var texts = new List<string>();
        void Collect(Node node)
        {
            if (node.Kind == LeafKind.Text) texts.Add(node.Text);
            foreach (var child in node.Children) Collect(child);
        }
        Collect(_ui.Roots[0]);
        Assert.Contains("••••••", texts);
        Assert.DoesNotContain("secret", texts);
    }

    [Fact]
    public void LongTextShiftsToKeepCaretVisible()
    {
        Focus();
        Frame(Type(Next(), new string('w', 60)));
        var row = _ui.Roots[0].Children[0].Children[0];
        Assert.True(row.Style.Padding.Left < -100, $"сдвиг {row.Style.Padding.Left}");
        Frame(Hit(Next(), "home"));
        Assert.Equal(0, _ui.Roots[0].Children[0].Children[0].Style.Padding.Left);
    }

    [Fact]
    public void CaretBlinksTwiceASecondWhileFocused()
    {
        Focus();
        bool Caret()
        {
            var found = false;
            void Walk(Node node)
            {
                found |= node.Kind == LeafKind.Caret;
                foreach (var child in node.Children) Walk(child);
            }
            Walk(_ui.Roots[0]);
            return found;
        }
        Frame(Frames.Off(10.1));
        var on = Caret();
        Frame(Frames.Off(10.6));
        var off = Caret();
        Assert.NotEqual(on, off);
    }

    // ------------------------------------------------------------------ поле мода: поток, вход кадра, молчание клавиш

    private (string Edited, int Keys, string Hits, string Text) ModFrame(ClientInput input)
    {
        var scene = new ClientScene(_ui);
        var writer = new AbiWriter();
        _ui.Begin(input.ToUi());
        new FrameInput().Write(writer, new FrameClock(1, 1 / 60.0, 3, 0, 0.05, 1), input, scene.Camera, _ui, scene.Atmosphere, [], null, []);
        string edited = "", hits = "", text = "";
        var keys = 0;
        var output = new S.ClientContext().Frame(writer.Written, c =>
        {
            edited = c.Ui.Edited("name") ?? "-";
            keys = c.Input.Keys.Count;
            hits = string.Join(',', c.Input.Hits.Order());
            text = c.Input.Text;
            c.Ui.Box(new S.Style { Id = "panel", Anchor = new S.Anchor(Left: 0, Top: 0) }, () => c.Ui.Field("name", "Ann", new S.Style { Width = 120 }));
        });
        ClientStream.Apply(output, scene);
        _ui.Finish();
        return (edited, keys, hits, text);
    }

    [Fact]
    public void ModFieldEditsReachTheFrameInputAndGameKeysGoQuiet()
    {
        var plain = ModFrame(new ClientInput { Width = 800, Height = 600, Keys = ["b", "shift"], Hits = ["b", "enter"], Text = "b" });
        Assert.Equal(("-", 2, "b,enter", "b"), plain);
        Assert.NotNull(_ui.RectOf("name"));
        // Нажали на поле: фокус есть, ввод ещё не пришёл.
        ModFrame(new ClientInput { Width = 800, Height = 600, Time = 0.1, Pointer = (10, 10), Pressed = PointerButtons.Left, Down = PointerButtons.Left });
        Assert.True(_ui.Typing);
        // Печатают: правка идёт в этот же вход, клавиши игры молчат, остаются Enter и подобные.
        var typed = ModFrame(new ClientInput { Width = 800, Height = 600, Time = 0.2, Pointer = (10, 10), Keys = ["b", "shift"], Hits = ["b", "enter", "e"], Text = "b" });
        Assert.Equal(("bAnn", 0, "enter", ""), typed);
        Assert.Equal([("name", "bAnn")], _ui.FrameEdits);
    }

    [Fact]
    public void EditsGivenByTheShellWinOverTheUi()
    {
        var typed = ModFrame(new ClientInput { Width = 800, Height = 600, Edits = [("name", "Shell")] });
        Assert.Equal("Shell", typed.Edited);
    }
}
