using System.Globalization;

namespace Kharos.Client.Ui;

/// <summary>Цвет RGBA8 — как <c>Color</c> в gui.rs.</summary>
public readonly record struct Color(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>Из 0xRRGGBB, непрозрачный.</summary>
    public static Color Hex(uint value) => new((byte)(value >> 16), (byte)(value >> 8), (byte)value);

    /// <summary>Упаковка для вершины <see cref="Kharos.Gpu.Vertex"/>: r | g ≪ 8 | b ≪ 16 | a ≪ 24.</summary>
    public uint Rgba => (uint)(R | G << 8 | B << 16 | A << 24);

    /// <summary>Та же краска с альфой, умноженной на alpha (faded).</summary>
    public Color Faded(float alpha) => this with { A = Gui.Byte(A * alpha) };

    /// <summary>Строка CSS, как у модов: "#rrggbb", "#rrggbbaa", "rgb(r g b / a)", "rgba(r, g, b, a)".</summary>
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
        return new Color(Gui.Byte(MathF.Truncate(parts[0])), Gui.Byte(MathF.Truncate(parts[1])), Gui.Byte(MathF.Truncate(parts[2])), Gui.Byte(alpha * 255));
    }

    public static readonly Color White = new(255, 255, 255);
}

/// <summary>Прямоугольник в логических пикселях.</summary>
public readonly record struct Rect(float X, float Y, float W, float H)
{
    public bool Contains(float x, float y) => x >= X && y >= Y && x < X + W && y < Y + H;

    public Rect Intersect(Rect other)
    {
        float x = MathF.Max(X, other.X), y = MathF.Max(Y, other.Y);
        return new Rect(x, y, MathF.Max(MathF.Min(X + W, other.X + other.W) - x, 0), MathF.Max(MathF.Min(Y + H, other.Y + other.H) - y, 0));
    }
}

/// <summary>Как кладутся дети: рядом, столбцом или стопкой друг на друга.</summary>
public enum Direction : byte { Column, Row, Stack }

/// <summary>Выравнивание по поперечной оси (align), по главной (justify) или своё место в стопке (self).</summary>
public enum Align : byte { Start, Center, End, Stretch, Between }

/// <summary>Шрифты интерфейса, как FONTS в text.ts.</summary>
public enum FontName : byte { Body, Small, Title, Logo }

/// <summary>Отступы: сверху, справа, снизу, слева. Число — со всех сторон, пара — (по вертикали, по горизонтали).</summary>
public readonly record struct Padding(float Top, float Right, float Bottom, float Left)
{
    public static implicit operator Padding(float all) => new(all, all, all, all);
    public static implicit operator Padding((float Vertical, float Horizontal) pair) => new(pair.Vertical, pair.Horizontal, pair.Vertical, pair.Horizontal);
    public static implicit operator Padding((float Top, float Right, float Bottom, float Left) all) => new(all.Top, all.Right, all.Bottom, all.Left);
}

/// <summary>Привязка корневой панели к краям экрана в пикселях; CenterX/CenterY — сдвиг от середины.</summary>
public readonly record struct Anchor(float? Left = null, float? Right = null, float? Top = null, float? Bottom = null, float? CenterX = null, float? CenterY = null);

/// <summary>Рамки скина, как paintSkins в skin.ts; порядок — их место в текстуре скина.</summary>
public enum Skin : byte { Panel, Well, Button, ButtonHover, ButtonActive, ButtonDisabled, ButtonPrimary, ButtonPrimaryHover, Gold, Danger }

/// <summary>
/// Стиль узла — те же поля, что BoxStyle в ui.ts и <c>Style</c> в gui.rs. Только данные: мод клиента передаст его
/// записью в потоке команд (см. <see cref="Gui"/>).
/// </summary>
public record struct Style()
{
    /// <summary>Номер узла: по нему видно наведение, щелчки, прокрутку и место прошлого кадра.</summary>
    public string? Id { get; set; }
    public Direction Direction { get; set; } = Direction.Column;
    /// <summary>Место в стопке родителя: по горизонтали и по вертикали.</summary>
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
    /// <summary>Ряд переносится на новые строки, когда не влезает.</summary>
    public bool Wrap { get; set; }
    public Skin? Skin { get; set; }
    public Color? Background { get; set; }
    public Color? Outline { get; set; }
    public float? Alpha { get; set; }
    /// <summary>Обрезать детей по своему прямоугольнику.</summary>
    public bool Clip { get; set; }
    /// <summary>Прокрутка колесом (нужен Id): содержимое выше узла ездит внутри него; включает обрезку.</summary>
    public bool Scroll { get; set; }
    public Anchor? Anchor { get; set; }
    /// <summary>Корневая панель не закрывает собой мир: щелчки сквозь неё идут дальше.</summary>
    public bool Passive { get; set; }

    /// <summary>Стиль кнопки по умолчанию (как у <c>ui:button</c>): ряд, по центру, отступы 4 × 10, промежуток 6.</summary>
    public static Style Button => new() { Direction = Direction.Row, Align = Align.Center, Justify = Align.Center, Padding = (4, 10), Gap = 6 };

    internal readonly bool Clips => Clip || Scroll;
}

/// <summary>Текст: шрифт, цвет, тень со сдвигом lift, перенос по ширине (∞ — по ширине родителя), многоточие.</summary>
public record struct TextStyle()
{
    public FontName Font { get; set; } = FontName.Body;
    public Color Color { get; set; } = Gui.TextColor;
    public Color? Shadow { get; set; }
    public float Lift { get; set; } = 1;
    public float? Wrap { get; set; }
    public bool Ellipsis { get; set; }
}

/// <summary>Что за картинка: кадр атласа обликов стороной Team, монета скина, мини-карта (с туманом поверх).</summary>
public enum ImageKind : byte { Sprite, Coin, Minimap }

/// <summary>uv кусочка текстуры: левый верхний и правый нижний углы.</summary>
public readonly record struct Uv(float U0, float V0, float U1, float V1)
{
    public static implicit operator (float, float, float, float)(Uv uv) => (uv.U0, uv.V0, uv.U1, uv.V1);
}

/// <summary>Картинка узла.</summary>
public readonly record struct ImageSpec(ImageKind Kind, float Width, float Height, Uv Uv = default, int Team = 0);

/// <summary>Щелчок: кнопка (0 — левая, 1 — средняя, 2 — правая, как у страницы), клавиши, долгое ли касание.</summary>
public readonly record struct Click(byte Button, bool Shift = false, bool Ctrl = false, bool Alt = false, bool Long = false);
