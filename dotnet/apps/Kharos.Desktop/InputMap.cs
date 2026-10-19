using System.Numerics;
using System.Text;
using Kharos.Client.Game;
using Kharos.Client.Ui;
using Silk.NET.Input;
using SilkKey = Silk.NET.Input.Key;

namespace Kharos.Desktop;

/// <summary>
/// Ввод Silk.NET → <see cref="ClientInput"/> (как Input у пилота): события копятся между кадрами, <see cref="Take"/>
/// отдаёт кадр и сбрасывает разовое. Клавиши — именами для модов и полей ввода (<see cref="Name"/>: буквы, цифры,
/// стрелки, Home и End, знаки); Ctrl и Meta (Cmd) — одно и то же «Ctrl» (Ctrl+A, C, X, V в полях ввода); набранный
/// текст — из событий символов (<see cref="ClientInput.Text"/>), без управляющих знаков. Горячие клавиши оболочки
/// решает оболочка (ShellApp), а не ввод: поле в фокусе их держит. Касаний Silk.NET (GLFW) не отдаёт.
/// </summary>
public sealed class InputMap
{
    private readonly SortedSet<string> _held = new(StringComparer.Ordinal);
    private readonly List<string> _hits = [];
    private readonly StringBuilder _text = new();
    private Vector2? _pointer;
    private PointerButtons _down, _pressed, _released;
    private float _wheel;

    public bool Focused { get; set; } = true;

    /// <summary>Системный буфер обмена первой клавиатуры (после <see cref="Attach"/>); null — нет клавиатуры.</summary>
    public IClipboard? Clipboard { get; private set; }

    public void Attach(IInputContext context)
    {
        foreach (var keyboard in context.Keyboards)
        {
            keyboard.KeyDown += (_, key, _) => Key(key, true);
            keyboard.KeyUp += (_, key, _) => Key(key, false);
            keyboard.KeyChar += (_, ch) => Char(ch);
            Clipboard ??= new KeyboardClipboard(keyboard);
        }
        foreach (var mouse in context.Mice)
        {
            mouse.MouseMove += (_, position) => _pointer = position;
            mouse.MouseDown += (_, button) => OnButton(button, true);
            mouse.MouseUp += (_, button) => OnButton(button, false);
            mouse.Scroll += (_, wheel) => _wheel += wheel.Y;
        }
    }

    /// <summary>Указатель на месте в пикселях окна (снимки кадров).</summary>
    public void PlacePointer(float x, float y) => _pointer = new Vector2(x, y);

    /// <summary>Окно потеряло фокус: зажатое отпускается (иначе клавиша «залипнет», пока окно не вернут).</summary>
    public void ReleaseAll()
    {
        _held.Clear();
        _released |= _down;
        _down = PointerButtons.None;
    }

    /// <summary>
    /// Ввод кадра. <paramref name="toLogical"/> — логических пикселей на пиксель окна (указатель окна → логический),
    /// width и height — экран в логических пикселях.
    /// </summary>
    public ClientInput Take(double time, float width, float height, float scale, float toLogical)
    {
        var input = new ClientInput
        {
            Time = time, Width = width, Height = height, Scale = scale,
            Pointer = _pointer is { } p ? (p.X * toLogical, p.Y * toLogical) : null,
            Down = _down, Pressed = _pressed, Released = _released, Wheel = _wheel,
            Shift = _held.Contains("shift"), Ctrl = _held.Contains("ctrl") || _held.Contains("meta"), Alt = _held.Contains("alt"),
            Touch = false, Focused = Focused,
            Keys = [.. _held], Hits = [.. _hits], Text = _text.ToString(),
        };
        _pressed = _released = PointerButtons.None;
        _wheel = 0;
        _hits.Clear();
        _text.Clear();
        return input;
    }

    /// <summary>Клавиша нажата или отпущена: повтор зажатой — не новое нажатие (повтор правки ведёт поле ввода само).</summary>
    public void Key(SilkKey key, bool down)
    {
        if (Name(key) is not { } name)
            return;
        if (down)
        {
            if (_held.Add(name))
                _hits.Add(name);
        }
        else
        {
            _held.Remove(name);
        }
    }

    /// <summary>Символ набранного текста: управляющие знаки (Ctrl+буква даёт их на Windows) не текст.</summary>
    public void Char(char ch)
    {
        if (!char.IsControl(ch))
            _text.Append(ch);
    }

    private void OnButton(MouseButton button, bool down)
    {
        var flag = button switch
        {
            MouseButton.Left => PointerButtons.Left,
            MouseButton.Right => PointerButtons.Right,
            MouseButton.Middle => PointerButtons.Middle,
            _ => PointerButtons.None,
        };
        if (flag == PointerButtons.None)
            return;
        if (down && (_down & flag) == 0)
            _pressed |= flag;
        if (!down && (_down & flag) != 0)
            _released |= flag;
        _down = down ? _down | flag : _down & ~flag;
    }

    /// <summary>
    /// Имя клавиши для модов и полей: буквы и цифры — по месту на клавиатуре (Ctrl+V — «v» при любой раскладке),
    /// цифры цифровой клавиатуры — как цифры, остальное — по смыслу ("left", "home", "escape", "f1", "/").
    /// </summary>
    public static string? Name(SilkKey key)
    {
        if (key is >= SilkKey.A and <= SilkKey.Z)
            return ((char)('a' + (key - SilkKey.A))).ToString();
        if (key is >= SilkKey.Number0 and <= SilkKey.Number9)
            return ((char)('0' + (key - SilkKey.Number0))).ToString();
        if (key is >= SilkKey.Keypad0 and <= SilkKey.Keypad9)
            return ((char)('0' + (key - SilkKey.Keypad0))).ToString();
        if (key is >= SilkKey.F1 and <= SilkKey.F25)
            return "f" + (key - SilkKey.F1 + 1);
        return key switch
        {
            SilkKey.Left => "left",
            SilkKey.Right => "right",
            SilkKey.Up => "up",
            SilkKey.Down => "down",
            SilkKey.Home => "home",
            SilkKey.End => "end",
            SilkKey.PageUp => "pageup",
            SilkKey.PageDown => "pagedown",
            SilkKey.Insert => "insert",
            SilkKey.Space => "space",
            SilkKey.Escape => "escape",
            SilkKey.Enter or SilkKey.KeypadEnter => "enter",
            SilkKey.Tab => "tab",
            SilkKey.Backspace => "backspace",
            SilkKey.Delete => "delete",
            SilkKey.ShiftLeft or SilkKey.ShiftRight => "shift",
            SilkKey.ControlLeft or SilkKey.ControlRight => "ctrl",
            SilkKey.AltLeft or SilkKey.AltRight => "alt",
            SilkKey.SuperLeft or SilkKey.SuperRight => "meta",
            SilkKey.Minus or SilkKey.KeypadSubtract => "-",
            SilkKey.Equal => "=",
            SilkKey.KeypadAdd => "+",
            SilkKey.KeypadMultiply => "*",
            SilkKey.Slash or SilkKey.KeypadDivide => "/",
            SilkKey.BackSlash => "\\",
            SilkKey.Period => ".",
            SilkKey.Comma => ",",
            SilkKey.Semicolon => ";",
            SilkKey.Apostrophe => "'",
            SilkKey.LeftBracket => "[",
            SilkKey.RightBracket => "]",
            SilkKey.GraveAccent => "`",
            _ => null,
        };
    }
}

/// <summary>Системный буфер обмена через клавиатуру GLFW (Ctrl+C, X, V в полях ввода).</summary>
public sealed class KeyboardClipboard(IKeyboard keyboard) : IClipboard
{
    public string Get()
    {
        try
        {
            return keyboard.ClipboardText ?? "";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // В буфере не текст (картинка) — GLFW отказывает: вставлять нечего.
            return "";
        }
    }

    public void Set(string text)
    {
        try
        {
            keyboard.ClipboardText = text;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
        }
    }
}
