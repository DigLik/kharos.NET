using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>Параметр вида для карточки: ключ, подпись, значение.</summary>
internal readonly record struct StatRow(string Key, string Label, string Value);

/// <summary>
/// Общие кусочки панелей — перенос lib/widgets.luau (widgets.ts): цвета текста, тень подписей, монета, полоса
/// готовности, подпись клавиши.
/// </summary>
internal static class Widgets
{
    // Цвета текста — те же, что у страницы (style.css).
    public static readonly Color Ink = Color.Hex(0x0b111b);
    public static readonly Color Bright = Color.Hex(0xeef6ff);
    public static readonly Color Text = Color.Hex(0xd6e6f5);
    public static readonly Color Soft = Color.Hex(0xb4c9dd);
    public static readonly Color Dim = Color.Hex(0x8fa9c2);
    public static readonly Color Muted = Color.Hex(0x6184a3);
    public static readonly Color TeamMid = Color.Hex(0x1f63d8);
    public static readonly Color Gold = Color.Hex(0xf0c95a);
    public static readonly Color Good = Color.Hex(0x8fe0a8);
    public static readonly Color Warn = Color.Hex(0xffb070);

    /// <summary>Тень подписей кнопок и ячеек: пиксель чернил правее и ниже.</summary>
    public static TextStyle Shadowed(Color? color = null, bool ellipsis = false) =>
        new() { Color = color ?? TextStyle.DefaultColor, Shadow = Ink, Lift = 1, Ellipsis = ellipsis };

    /// <summary>Монета кредитов.</summary>
    public static void Coin(Ui ui, float size = 16) => ui.Coin(size);

    /// <summary>Полоса готовности с подписью: утопленное окно, внутри — полоса огней команды на долю value, поверх — подпись.</summary>
    public static void Bar(Ui ui, double value, string label, float? width = null)
    {
        var fill = (float)Math.Clamp(value, 0, 1);
        ui.Box(new Style { Skin = Skin.Well, Width = width, Height = 24, Padding = 4, Direction = Direction.Stack, Clip = true }, () =>
        {
            ui.Box(new Style { Grow = 1, Direction = Direction.Row }, () =>
            {
                ui.Box(new Style { Grow = fill, Background = TeamMid });
                ui.Box(new Style { Grow = 1 - fill });
            });
            ui.Box(new Style { Self = (Align.Start, Align.Center), Padding = (0, 8) }, () => ui.Text(label, Shadowed(Text, ellipsis: true)));
        });
    }

    /// <summary>Подпись клавиши — золотая.</summary>
    public static void Key(Ui ui, string label, Color? color = null) => ui.Text(label, Shadowed(color ?? Gold));
}

/// <summary>
/// Параметры вида юнита для карточек и панели — перенос lib/stats.luau (unitStats.ts): строки собираются из данных
/// вида, а не пишутся текстом — поменяются числа, поменяется и карточка. Оружейные строки добавятся с боем.
/// </summary>
internal static class Stats
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["hp"] = "Прочность",
        ["class"] = "Класс",
        ["speed"] = "Скорость",
        ["sight"] = "Обзор",
        ["cargo"] = "Груз",
        ["repair"] = "Радиус ремонта",
    };

    private static readonly Dictionary<string, string> Classes = new()
    {
        ["infantry"] = "пехота",
        ["vehicle"] = "лёгкая техника",
        ["heavy"] = "тяжёлая техника",
        ["air"] = "авиация",
        ["building"] = "здания",
    };

    /// <summary>До одного знака после запятой, без хвостового нуля.</summary>
    private static string Tidy(double value)
    {
        var rounded = Lua.Round(value * 10) / 10;
        return rounded == Math.Floor(rounded) ? Lua.ToString(Math.Floor(rounded)) : Lua.ToString(rounded);
    }

    /// <summary>Строки параметров вида: прочность, класс, скорость, обзор, груз, ремонт.</summary>
    public static List<StatRow> Unit(Kind kind)
    {
        var rows = new List<StatRow>();
        void Row(string key, string value) => rows.Add(new StatRow(key, Labels[key], value));
        if (kind.TryNumber("hp", out var hp))
            Row("hp", Lua.ToString(hp));
        if (kind.Text("kind") is { } cls)
            Row("class", Classes.GetValueOrDefault(cls, cls));
        if (kind.TryNumber("speed", out var speed))
            Row("speed", Tidy(speed));
        if (kind.TryNumber("sight", out var sight))
            Row("sight", Tidy(sight));
        if (kind.TryNumber("inventory", out var inventory))
            Row("cargo", Lua.ToString(inventory));
        if (kind.TryNumber("repair", out var repair))
            Row("repair", Tidy(repair));
        return rows;
    }

    /// <summary>Параметры одним текстом, строка на параметр: для всплывающей подсказки (unitStatsText).</summary>
    public static string Text(Kind kind) => string.Join('\n', Unit(kind).Select(row => row.Label + ": " + row.Value));

    /// <summary>Главное одной строкой — для панели выбранного (unitStatsLine).</summary>
    public static string Line(Kind kind)
    {
        string[] brief = ["hp", "speed", "sight", "cargo", "damage", "range"];
        return string.Join(" · ", Unit(kind).Where(row => Array.IndexOf(brief, row.Key) >= 0).Select(row => row.Label + " " + row.Value));
    }
}
