using System.Globalization;
using Kharos.Client.Ui;

namespace Kharos.Client.Shell;

/// <summary>Цвета текста оболочки (COLORS в widgets.ts).</summary>
public static class Colors
{
    public static readonly Color Ink = Skins.Ink;
    public static readonly Color Bright = Color.Hex(0xeef6ff);
    public static readonly Color Text = Color.Hex(0xd6e6f5);
    public static readonly Color Soft = Color.Hex(0xb4c9dd);
    public static readonly Color Dim = Color.Hex(0x8fa9c2);
    public static readonly Color Muted = Color.Hex(0x6184a3);
    public static readonly Color Team = Color.Hex(0x5aa9ff);
    public static readonly Color TeamMid = Color.Hex(0x1f63d8);
    public static readonly Color Warn = Color.Hex(0xffb070);
    public static readonly Color Bad = Color.Hex(0xff7a66);
    public static readonly Color Line = Color.Hex(0x1c2b3e);
    public static readonly Color Link = Color.Hex(0x7fb7ff);
    public static readonly Color LinkHover = Color.Hex(0xa5d0ff);
    /// <summary>Фон меню без витрины и затемнение экрана под окнами оболочки.</summary>
    public static readonly Color Backdrop = Color.Hex(0x08111c);
    public static readonly Color Shade = new(8, 17, 28, 224);
    public static readonly Color PauseShade = new(8, 17, 28, 153);
}

/// <summary>
/// Поля окон меню на интерфейсе (form.ts): окно с заголовком и подвалом, кнопки, ползунок, флажок, ряд вариантов с
/// переносом, числовое и текстовое поле, раскрывающийся блок, подпись слева от поля. <see cref="Clicked"/> — что нажали
/// за кадр (для звука щелчка), <see cref="Slid"/> — ползунок, который сдвинули (для тика).
/// </summary>
public sealed class Forms(Gui gui)
{
    /// <summary>Рамка кнопки страницы: border в 4 пикселя поверх отступа.</summary>
    private const float Frame = 4;

    public Gui Gui { get; } = gui;

    /// <summary>Кнопку нажали в этом кадре (номер).</summary>
    public string? Clicked { get; private set; }

    /// <summary>Ползунок сдвинули в этом кадре: где теперь ручка, 0..1.</summary>
    public float? Slid { get; private set; }

    /// <summary>Начало кадра: забыть щелчки прошлого.</summary>
    public void Begin() => (Clicked, Slid) = (null, null);

    private static readonly TextStyle Shadow = new() { Shadow = Skins.Ink, Lift = 1 };

    /// <summary>Кнопка меню: высота 32, текст с тенью. primary — главное действие окна, danger — опасное.</summary>
    public bool Button(string id, string label, bool primary = false, bool danger = false, bool active = false, bool disabled = false,
        float grow = 0, float? width = null, string? tip = null, float height = 32, (float V, float H)? padding = null)
    {
        Skin? skin = danger ? Skin.Danger : primary ? Skin.ButtonPrimary : null;
        var color = disabled ? Colors.Muted : primary || danger || active ? Colors.Bright : Colors.Text;
        var (vertical, horizontal) = padding ?? (4, 12);
        var style = Style.Button with
        {
            Skin = skin, Grow = grow, Width = width, Height = height, Padding = (vertical, horizontal + Frame),
            Alpha = disabled ? 0.6f : null,
        };
        var click = Gui.Button(id, style, () => Gui.Text(label, Shadow with { Color = color, Ellipsis = true }), active, disabled);
        if (tip is not null)
            Gui.Tip(id, tip);
        if (click is not null)
            Clicked = id;
        return click is not null;
    }

    /// <summary>Ширина кнопки с подписью — для переноса рядов.</summary>
    public float ButtonWidth(string label, float padding = 12) => Gui.Measure(label) + 1 + (padding + Frame) * 2;

    /// <summary>
    /// Окно меню: заголовок, содержимое и подвал — «Назад» слева, действия справа, главное из них последним. Окно не
    /// выше экрана: длинное содержимое прокручивается внутри. modal — окно поверх игры: затемняет экран и забирает ввод,
    /// Escape — как «Назад». Возвращает true, если нажали «Назад».
    /// </summary>
    public bool Window(string id, string title, float width, Action body, Anchor? anchor = null, string? back = null, Action? actions = null,
        float? maxHeight = null, bool modal = false)
    {
        var place = modal ? new Anchor(CenterX: 0, CenterY: 0) : anchor ?? new Anchor(CenterX: 0, CenterY: 0);
        var room = Gui.Height - (place.Top ?? place.Bottom ?? 16) - 16;
        var pressed = false;
        var modalId = id + ":modal";
        if (modal)
            Gui.Open(new Style { Id = modalId, Direction = Direction.Stack, Anchor = new Anchor(Left: 0, Right: 0, Top: 0, Bottom: 0), Background = Colors.PauseShade, Modal = true });
        try
        {
            Gui.Box(new Style
            {
                Id = id, Anchor = modal ? null : place, Self = modal ? (Align.Center, Align.Center) : null, Width = width,
                MaxHeight = MathF.Min(maxHeight ?? room, room), Skin = Skin.Panel, Padding = 18, Direction = Direction.Column, Align = Align.Stretch,
            }, () =>
            {
                Gui.Box(new Style { Padding = (0, 0, 12, 0), Direction = Direction.Row, Justify = Align.Center },
                    () => Gui.Text(title, new TextStyle { Font = FontName.Title, Color = Colors.Dim, Shadow = Colors.Ink, Lift = 2, Ellipsis = true }));
                // Тело уступает высоту, когда окну тесно: заголовок и подвал остаются на месте, тело прокручивается.
                var footer = back is null && actions is null ? 0 : 14 + 2 + 12 + 32;
                var bodyRoom = MathF.Max(MathF.Min(maxHeight ?? room, room) - 36 - 12 - Fonts.Line(FontName.Title) - footer, 48);
                Gui.Box(new Style { Id = id + ":body", Direction = Direction.Column, Align = Align.Stretch, Gap = 10, Scroll = true, MinHeight = 48, MaxHeight = bodyRoom }, body);
                if (back is null && actions is null)
                    return;
                Gui.Space(0, 14);
                Gui.Box(new Style { Height = 2, Background = Colors.Line });
                Gui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 6, Padding = (12, 0, 0, 0), Wrap = true }, () =>
                {
                    if (back is not null && Button(id + ":back", back))
                        pressed = true;
                    if (actions is not null)
                        Gui.Box(new Style { Direction = Direction.Row, Gap = 6, Wrap = true, Grow = 1, Justify = Align.End }, actions);
                });
            });
        }
        finally
        {
            if (modal)
                Gui.Close();
        }
        // Escape закрывает верхнее модальное окно; поле ввода в фокусе отдаёт Escape окну так же.
        if (modal && Gui.KeyHit("escape") && (Gui.ModalId is null || Gui.ModalId == modalId))
            pressed = true;
        return pressed;
    }

    /// <summary>Подпись поля слева и поле справа.</summary>
    public void Row(string label, float labelWidth, Action control, string? tip = null, string? id = null)
    {
        Gui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 8 }, () =>
        {
            var labelId = id ?? "label:" + label;
            Gui.Box(new Style { Id = tip is not null ? labelId : null, Width = labelWidth },
                () => Gui.Text(label, new TextStyle { Color = Colors.Dim, Wrap = float.PositiveInfinity }));
            if (tip is not null)
                Gui.Tip(labelId, tip);
            control();
        });
    }

    /// <summary>Подпись над полем.</summary>
    public void Above(string label, Action control) =>
        Gui.Box(new Style { Direction = Direction.Column, Align = Align.Stretch, Gap = 4 }, () =>
        {
            Gui.Text(label, new TextStyle { Color = Colors.Dim, Wrap = float.PositiveInfinity });
            control();
        });

    /// <summary>
    /// Ползунок: дорожка в утопленном окне, заливка огней команды до ручки. Тянут или щёлкают — значение идёт за
    /// указателем, по шагу step. Возвращает новое значение (или прежнее).
    /// </summary>
    public double Slider(string id, double value, double min, double max, double step, float width, bool disabled = false)
    {
        var share = max > min ? Math.Clamp((value - min) / (max - min), 0, 1) : 0;
        var inner = width - 8;
        Gui.Box(new Style { Id = id, Width = width, Height = 20, Skin = Skin.Well, Padding = 4, Direction = Direction.Row, Alpha = disabled ? 0.5f : null }, () =>
        {
            Gui.Box(new Style { Width = MathF.Round(inner * (float)share), Height = 12, Background = Colors.TeamMid });
            Gui.Box(new Style { Width = 4, Height = 12, Background = Colors.Bright });
        });
        if (disabled || Gui.Held(id) is not { } held)
            return value;
        var raw = min + held.U * (max - min);
        var stepped = Math.Round((raw - min) / step) * step + min;
        var next = Math.Clamp(Math.Round(stepped * 1e6) / 1e6, min, max);
        if (next != value)
            Slid = (float)((next - min) / (max - min));
        return next;
    }

    /// <summary>Флажок с подписью. Возвращает новое состояние.</summary>
    public bool Checkbox(string id, bool value, string label, bool disabled = false)
    {
        Gui.Box(new Style { Id = id, Direction = Direction.Row, Align = Align.Center, Gap = 8, Alpha = disabled ? 0.5f : null }, () =>
        {
            Gui.Box(new Style { Width = 18, Height = 18, Skin = Skin.Well, Direction = Direction.Row, Justify = Align.Center, Align = Align.Center }, () =>
            {
                if (value)
                    Gui.Box(new Style { Width = 8, Height = 8, Background = Colors.Team });
            });
            Gui.Box(new Style { Shrink = true }, () => Gui.Text(label, new TextStyle { Wrap = float.PositiveInfinity }));
        });
        if (disabled || Gui.Clicked(id) is null)
            return value;
        Clicked = id;
        return !value;
    }

    /// <summary>
    /// Ряд вариантов: кнопки, выбранная нажата; не влезают в width — переносятся. fill — кнопки делят строку поровну.
    /// Возвращает индекс выбранного щелчком варианта или −1.
    /// </summary>
    public int Chips(string prefix, IReadOnlyList<string> labels, int selected, float width, bool fill = false, IReadOnlyList<string?>? tips = null)
    {
        const float gap = 6;
        var rows = new List<List<int>> { new() };
        var used = 0f;
        for (var index = 0; index < labels.Count; index++)
        {
            var w = ButtonWidth(labels[index]);
            var line = rows[^1];
            if (line.Count > 0 && used + gap + w > width)
            {
                rows.Add([index]);
                used = w;
            }
            else
            {
                used += (line.Count > 0 ? gap : 0) + w;
                line.Add(index);
            }
        }
        var picked = -1;
        Gui.Box(new Style { Direction = Direction.Column, Gap = gap, Width = width }, () =>
        {
            foreach (var line in rows)
                Gui.Box(new Style { Direction = Direction.Row, Gap = gap }, () =>
                {
                    foreach (var index in line)
                        if (Button($"{prefix}:{index}", labels[index], active: index == selected, grow: fill ? 1 : 0, padding: (2, 12), tip: tips is not null && index < tips.Count ? tips[index] : null))
                            picked = index;
                });
        });
        return picked;
    }

    /// <summary>Текстовое поле: набранное (сразу, по буквам) и нажатые в нём клавиши.</summary>
    public Field TextField(string id, string value, float? width = null, string? placeholder = null, int maxLength = 0, bool password = false, bool focus = false) =>
        Gui.Input(id, value, new FieldOptions { Width = width, Placeholder = placeholder, MaxLength = maxLength, Password = password, Focus = focus, Color = Colors.Bright });

    /// <summary>
    /// Числовое поле: без live число уходит по каждой правке, с live — по Enter или уходу фокуса. Возвращает новое
    /// число или null.
    /// </summary>
    public long? NumberField(string id, long value, float? width = null, long min = long.MinValue, long max = long.MaxValue, bool live = false)
    {
        var field = Gui.Input(id, value.ToString(CultureInfo.InvariantCulture), new FieldOptions { Width = width, Digits = true, MaxLength = 12, Color = Colors.Bright });
        var commit = !live || field.Keys.Contains("enter") || !field.Focused;
        if (!commit || !long.TryParse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return null;
        var clamped = Math.Clamp(number, min, max);
        return clamped == value ? null : clamped;
    }

    /// <summary>Раскрывающийся блок: строка-заголовок со стрелкой. Возвращает, открыт ли он теперь.</summary>
    public bool Details(string id, string title, bool open, Action body)
    {
        Gui.Box(new Style { Id = id, Direction = Direction.Row, Gap = 6 }, () => Gui.Text($"{(open ? "▾" : "▸")} {title}", new TextStyle { Color = Colors.Dim, Wrap = float.PositiveInfinity }));
        var next = Gui.Clicked(id) is not null ? !open : open;
        if (open)
            body();
        return next;
    }

    /// <summary>Заголовок группы полей: мелкий, бледный, ПРОПИСНЫМИ, с чертой сверху (кроме первого).</summary>
    public void GroupTitle(string title, bool first = false)
    {
        if (!first)
            Gui.Box(new Style { Height = 2, Background = Colors.Line });
        Gui.Box(new Style { Padding = (first ? 0 : 6, 0, 0, 0) }, () => Gui.Text(title.ToUpperInvariant(), new TextStyle { Color = Colors.Muted }));
    }

    /// <summary>Строка текста с цветом и переносом по ширине родителя.</summary>
    public void Note(string text, Color? color = null, FontName font = FontName.Body) =>
        Gui.Text(text, new TextStyle { Color = color ?? Colors.Text, Wrap = float.PositiveInfinity, Font = font });

    /// <summary>Ссылка: подсвечивается при наведении; щелчок — true (открыть адрес решает оболочка).</summary>
    public bool Link(string id, string label, bool center = false)
    {
        var color = Gui.Hovered(id) ? Colors.LinkHover : Colors.Link;
        Gui.Box(new Style { Id = id, Direction = Direction.Row, Justify = center ? Align.Center : Align.Start }, () => Gui.Text(label, new TextStyle { Color = color, Ellipsis = true }));
        return Gui.Clicked(id) is not null;
    }
}
