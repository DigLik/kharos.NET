using System.Numerics;
using System.Text;
using Kharos.Client.Game;
using Kharos.Client.Ui;
using Silk.NET.Input;
using SilkKey = Silk.NET.Input.Key;

namespace Kharos.Desktop;

/// <summary>
/// Ввод Silk.NET → <see cref="ClientInput"/> (как Input у пилота): события копятся между кадрами, <see cref="Take"/>
/// отдаёт кадр и сбрасывает разовое. Касаний Silk.NET (GLFW) не отдаёт, поэтому <c>Touch</c> всегда ложь.
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

    /// <summary>Нажата F11 с прошлого кадра — оболочка переключает полный экран.</summary>
    public bool ToggleFullscreen { get; set; }

    public void Attach(IInputContext context)
    {
        foreach (var keyboard in context.Keyboards)
        {
            keyboard.KeyDown += (_, key, _) => OnKey(key, true);
            keyboard.KeyUp += (_, key, _) => OnKey(key, false);
            keyboard.KeyChar += (_, ch) =>
            {
                if (!char.IsControl(ch))
                    _text.Append(ch);
            };
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
            Shift = _held.Contains("shift"), Ctrl = _held.Contains("ctrl"), Alt = _held.Contains("alt"),
            Touch = false, Focused = Focused,
            Keys = [.. _held], Hits = [.. _hits], Text = _text.ToString(),
        };
        _pressed = _released = PointerButtons.None;
        _wheel = 0;
        _hits.Clear();
        _text.Clear();
        return input;
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

    private void OnKey(SilkKey key, bool down)
    {
        if (key == SilkKey.F11 && down && !_held.Contains("f11"))
            ToggleFullscreen = true;
        if (Name(key) is not { } name)
            return;
        if (down)
        {
            // Повтор зажатой клавиши — не новое нажатие.
            if (_held.Add(name))
                _hits.Add(name);
        }
        else
        {
            _held.Remove(name);
        }
    }

    /// <summary>Имя клавиши для модов: буквы и цифры — по месту на клавиатуре, остальное — по смыслу ("left", "escape", "f1").</summary>
    public static string? Name(SilkKey key)
    {
        if (key is >= SilkKey.A and <= SilkKey.Z)
            return ((char)('a' + (key - SilkKey.A))).ToString();
        if (key is >= SilkKey.Number0 and <= SilkKey.Number9)
            return ((char)('0' + (key - SilkKey.Number0))).ToString();
        if (key is >= SilkKey.F1 and <= SilkKey.F25)
            return "f" + (key - SilkKey.F1 + 1);
        return key switch
        {
            SilkKey.Left => "left",
            SilkKey.Right => "right",
            SilkKey.Up => "up",
            SilkKey.Down => "down",
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
            SilkKey.Minus => "-",
            SilkKey.Equal => "=",
            _ => null,
        };
    }
}
