using System.Globalization;

namespace Kharos.Mod.Sdk.Client;

/// <summary>Цвет RGBA8 (как Color интерфейса клиента).</summary>
public readonly record struct Color(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>Из 0xRRGGBB, непрозрачный.</summary>
    public static Color Hex(uint value) => new((byte)(value >> 16), (byte)(value >> 8), (byte)value);

    /// <summary>Упаковка r | g ≪ 8 | b ≪ 16 | a ≪ 24.</summary>
    public uint Rgba => (uint)(R | G << 8 | B << 16 | A << 24);

    /// <summary>Та же краска с альфой, умноженной на alpha.</summary>
    public Color Faded(float alpha) => this with { A = Ui.Byte(A * alpha) };

    /// <summary>Строка CSS: "#rrggbb", "#rrggbbaa", "rgb(r g b / a)", "rgba(r, g, b, a)"; не разобрана — null.</summary>
    public static Color? Parse(string? text)
    {
        if (text is null) return null;
        text = text.Trim();
        if (text.StartsWith('#'))
        {
            var hex = text[1..];
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return null;
            return hex.Length switch
            {
                6 => Hex(value),
                8 => new Color((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value),
                _ => null,
            };
        }
        string inside;
        if (text.StartsWith("rgba(", StringComparison.Ordinal)) inside = text[5..];
        else if (text.StartsWith("rgb(", StringComparison.Ordinal)) inside = text[4..];
        else return null;
        if (!inside.EndsWith(')')) return null;
        var parts = new List<float>();
        foreach (var part in inside[..^1].Replace('/', ' ').Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) parts.Add(number);
        if (parts.Count < 3) return null;
        var alpha = parts.Count > 3 ? parts[3] : 1f;
        return new Color(Ui.Byte(MathF.Truncate(parts[0])), Ui.Byte(MathF.Truncate(parts[1])), Ui.Byte(MathF.Truncate(parts[2])), Ui.Byte(alpha * 255));
    }

    public static readonly Color White = new(255, 255, 255);

    /// <summary>Тёмная обводка интерфейса (Skins.Ink).</summary>
    public static readonly Color Ink = Hex(0x0b111b);
}

/// <summary>Прямоугольник в логических пикселях.</summary>
public readonly record struct Rect(float X, float Y, float W, float H)
{
    public bool Contains(float x, float y) => x >= X && y >= Y && x < X + W && y < Y + H;
}

/// <summary>Как кладутся дети: рядом, столбцом или стопкой.</summary>
public enum Direction : byte { Column, Row, Stack }

/// <summary>Выравнивание по поперечной оси (align), по главной (justify) или своё место в стопке (self).</summary>
public enum Align : byte { Start, Center, End, Stretch, Between }

/// <summary>Шрифты интерфейса.</summary>
public enum FontName : byte { Body, Small, Title, Logo }

/// <summary>Рамки скина.</summary>
public enum Skin : byte { Panel, Well, Button, ButtonHover, ButtonActive, ButtonDisabled, ButtonPrimary, ButtonPrimaryHover, Gold, Danger }

/// <summary>Отступы: сверху, справа, снизу, слева. Число — со всех сторон, пара — (по вертикали, по горизонтали).</summary>
public readonly record struct Padding(float Top, float Right, float Bottom, float Left)
{
    public static implicit operator Padding(float all) => new(all, all, all, all);
    public static implicit operator Padding((float Vertical, float Horizontal) pair) => new(pair.Vertical, pair.Horizontal, pair.Vertical, pair.Horizontal);
    public static implicit operator Padding((float Top, float Right, float Bottom, float Left) all) => new(all.Top, all.Right, all.Bottom, all.Left);
}

/// <summary>Привязка корневой панели к краям экрана в пикселях; CenterX/CenterY — сдвиг от середины.</summary>
public readonly record struct Anchor(float? Left = null, float? Right = null, float? Top = null, float? Bottom = null, float? CenterX = null, float? CenterY = null);

/// <summary>Стиль узла — те же поля, что Style интерфейса клиента (и BoxStyle в ui.ts).</summary>
public record struct Style()
{
    public string? Id { get; set; }
    public Direction Direction { get; set; } = Direction.Column;
    public (Align Horizontal, Align Vertical)? Self { get; set; }
    public float Gap { get; set; }
    public Padding Padding { get; set; }
    public Align Align { get; set; } = Align.Start;
    public Align Justify { get; set; } = Align.Start;
    public float? Width { get; set; }
    public float? Height { get; set; }
    public float MinWidth { get; set; }
    public float? MaxWidth { get; set; }
    public float MinHeight { get; set; }
    public float? MaxHeight { get; set; }
    public float Grow { get; set; }
    public bool Shrink { get; set; }
    public bool Wrap { get; set; }
    public Skin? Skin { get; set; }
    public Color? Background { get; set; }
    public Color? Outline { get; set; }
    public float? Alpha { get; set; }
    public bool Clip { get; set; }
    /// <summary>Прокрутка колесом (нужен Id).</summary>
    public bool Scroll { get; set; }
    public Anchor? Anchor { get; set; }
    /// <summary>Корневая панель не закрывает собой мир.</summary>
    public bool Passive { get; set; }
    /// <summary>Прокрутка держится у низа, пока её не сдвинули (лента чата); нужны Id и <see cref="Scroll"/>.</summary>
    public bool ScrollEnd { get; set; }
    /// <summary>
    /// Корневая панель — модальное окно: ввод ниже неё не доходит ни до интерфейса, ни до модов, клавиши игры молчат
    /// (нужен Id). Закрыть — перестать описывать.
    /// </summary>
    public bool Modal { get; set; }
    /// <summary>Нажатие по узлу (и его детям) не снимает фокус с поля ввода: кнопки рядом со строкой чата (нужен Id).</summary>
    public bool KeepFocus { get; set; }

    /// <summary>Стиль кнопки по умолчанию: ряд, по центру, отступы 4 × 10, промежуток 6.</summary>
    public static Style Button => new() { Direction = Direction.Row, Align = Align.Center, Justify = Align.Center, Padding = (4, 10), Gap = 6 };
}

/// <summary>Текст: шрифт, цвет, тень со сдвигом lift, перенос по ширине (∞ — по ширине родителя), многоточие.</summary>
public record struct TextStyle()
{
    /// <summary>Цвет текста по умолчанию.</summary>
    public static readonly Color DefaultColor = Color.Hex(0xd6e6f5);

    public FontName Font { get; set; } = FontName.Body;
    public Color Color { get; set; } = DefaultColor;
    public Color? Shadow { get; set; }
    public float Lift { get; set; } = 1;
    public float? Wrap { get; set; }
    public bool Ellipsis { get; set; }
}

/// <summary>Настройки поля ввода (как FieldOptions в ui.ts): только цифры, пароль, предел длины, подсказка, фокус в этом кадре.</summary>
public record struct FieldOptions()
{
    /// <summary>Только цифры: количество в сделке.</summary>
    public bool Digits { get; set; }
    /// <summary>Вместо букв — точки.</summary>
    public bool Password { get; set; }
    /// <summary>Не длиннее (0 — без предела, не больше 65535).</summary>
    public int MaxLength { get; set; }
    /// <summary>Серая подсказка в пустом поле.</summary>
    public string? Placeholder { get; set; }
    /// <summary>Поле получает фокус в этом кадре (чат открывается по Enter).</summary>
    public bool Focus { get; set; }
}

/// <summary>Щелчок: кнопка (0 — левая, 1 — средняя, 2 — правая), клавиши, долгое ли касание.</summary>
public readonly record struct Click(byte Button, bool Shift = false, bool Ctrl = false, bool Alt = false, bool Long = false);
