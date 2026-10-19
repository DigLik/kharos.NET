using System.Text;

namespace Kharos.Client.Ui;

/// <summary>Буфер обмена: оболочка подставляет системный, по умолчанию — в памяти (как у ui.ts без navigator.clipboard).</summary>
public interface IClipboard
{
    string Get();
    void Set(string text);
}

/// <summary>Буфер обмена в памяти.</summary>
public sealed class MemoryClipboard : IClipboard
{
    private string _text = "";
    public string Get() => _text;
    public void Set(string text) => _text = text;
}

/// <summary>Настройки поля ввода, как FieldOptions в ui.ts.</summary>
public record struct FieldOptions()
{
    /// <summary>Ширина; без неё поле растёт на свободное место ряда.</summary>
    public float? Width { get; set; }
    public string? Placeholder { get; set; }
    public bool Password { get; set; }
    public int MaxLength { get; set; }
    /// <summary>Только цифры: количество в сделке.</summary>
    public bool Digits { get; set; }
    /// <summary>Поле получает фокус в этом кадре — так чат открывается по Enter.</summary>
    public bool Focus { get; set; }
    /// <summary>Вместо утопленной рамки — заливка: строка чата.</summary>
    public Color? Background { get; set; }
    public Color? Outline { get; set; }
    public Color? Color { get; set; }
}

/// <summary>Поле ввода: что набрано, в фокусе ли и какие клавиши поле отдало панели за кадр (enter, escape, tab, up, down).</summary>
public readonly record struct Field(string Value, bool Focused, IReadOnlyList<string> Keys);

public sealed partial class Gui
{
    /// <summary>Клавиши, что поле не берёт себе: их забирает панель (чат отправляет по Enter, закрывается по Escape).</summary>
    public static readonly string[] FieldPassKeys = ["enter", "escape", "tab", "up", "down"];
    /// <summary>Через сколько секунд зажатая клавиша правки начинает повторяться и как часто потом.</summary>
    public const double RepeatDelay = 0.4, RepeatEvery = 0.035;
    public static readonly Color SelectionColor = Color.Hex(0x1f63d8);
    public static readonly Color CaretColor = Color.Hex(0xeef6ff);
    public static readonly Color PlaceholderColor = Color.Hex(0x8a8a8a);
    /// <summary>Высота и боковой отступ поля по умолчанию.</summary>
    public const float FieldHeight = 28;
    public const float FieldPadX = 8;

    private sealed class Edit
    {
        public string Value = "", Given = "";
        public int Caret, Anchor;
        public bool Digits, Password;
        public int MaxLength;
        /// <summary>Отступ текста слева и на сколько он сдвинут, чтобы каретка была видна.</summary>
        public float PadLeft = FieldPadX, Shift;
        public string Shown => Password ? new string('•', Value.Length) : Value;
    }

    private readonly Dictionary<string, Edit> _fields = [];
    private readonly HashSet<string> _seenFields = [];
    private readonly List<(string Id, string Value)> _edits = [];
    private readonly List<string> _fieldKeys = [];
    private IReadOnlyList<string> _hitKeys = [];
    private string? _focused;
    private string? _repeatKey;
    private double _repeatAt;

    /// <summary>Буфер обмена для Ctrl+C, X и V.</summary>
    public IClipboard Clipboard { get; set; } = new MemoryClipboard();
    /// <summary>В поле ввода набирают текст: клавиши игры молчат.</summary>
    public bool Typing => _focused is not null;
    /// <summary>Поле в фокусе.</summary>
    public string? Focused => _focused;
    /// <summary>Правки полей за кадр (номер, новое значение — последнее за кадр): мод получает их входом кадра.</summary>
    public IReadOnlyList<(string Id, string Value)> FrameEdits => _edits;

    /// <summary>Выделение поля: начало и конец (равные — выделения нет); поля нет — null.</summary>
    public (int From, int To)? Selection(string id) =>
        _fields.TryGetValue(id, out var edit) ? (Math.Min(edit.Anchor, edit.Caret), Math.Max(edit.Anchor, edit.Caret)) : null;

    /// <summary>Каретка поля.</summary>
    public int? CaretOf(string id) => _fields.TryGetValue(id, out var edit) ? edit.Caret : null;

    /// <summary>Снять фокус с поля.</summary>
    public void Blur() => _focused = null;

    private void BeginFields(in UiInput input)
    {
        _edits.Clear();
        _fieldKeys.Clear();
        _hitKeys = input.Hits;
        _seenFields.Clear();
        if (_focused is not null)
            foreach (var key in input.Hits)
                if (Array.IndexOf(FieldPassKeys, key) >= 0) _fieldKeys.Add(key);
    }

    private bool Keeps(string? id)
    {
        if (id is null) return false;
        if (_keeps.Contains(id)) return true;
        var index = _hits.FindIndex(hit => hit.Id == id);
        for (index = index >= 0 ? _hits[index].Parent : -1; index >= 0; index = _hits[index].Parent)
            if (_keeps.Contains(_hits[index].Id)) return true;
        return false;
    }

    /// <summary>Нажатие мимо поля снимает фокус, по полю — ставит его и каретку (с Shift — тянет выделение).</summary>
    private void PressOnFields(string? id, byte button, in UiInput input)
    {
        if (_focused is not null && id != _focused && !Keeps(id)) _focused = null;
        if (id is null || !_fields.TryGetValue(id, out var edit)) return;
        if (_focused != id) edit.Shift = 0;
        _focused = id;
        if (button != 0 || input.Pointer is not { } pointer) return;
        edit.Caret = IndexAt(id, edit, pointer.X);
        if (!input.Shift) edit.Anchor = edit.Caret;
    }

    /// <summary>Левая кнопка зажата на поле в фокусе — каретка идёт за указателем, выделяя текст.</summary>
    private void DragSelection(in UiInput input)
    {
        if (_press is not { Button: 0, Id: { } id } || id != _focused || input.Pointer is not { } pointer) return;
        if (input.Pressed.HasFlag(PointerButtons.Left) || !_fields.TryGetValue(id, out var edit)) return;
        edit.Caret = IndexAt(id, edit, pointer.X);
    }

    private int IndexAt(string id, Edit edit, float x)
    {
        var shown = edit.Shown;
        var origin = (RectOf(id)?.X ?? 0) + edit.PadLeft - edit.Shift;
        var (best, distance) = (0, float.PositiveInfinity);
        for (var i = 0; i <= shown.Length; i++)
        {
            var d = MathF.Abs(origin + Fonts.Width(FontName.Body, shown[..i]) - x);
            if (d < distance) (best, distance) = (i, d);
        }
        return best;
    }

    /// <summary>Клавиши и текст кадра — полю в фокусе: стрелки, Home и End, стирание, выделение, буфер обмена, повтор зажатой клавиши.</summary>
    private void TypeIntoField(in UiInput input)
    {
        if (_focused is not { } id || !_fields.TryGetValue(id, out var edit))
        {
            _repeatKey = null;
            return;
        }
        var before = edit.Value;
        foreach (var key in input.Hits) Key(edit, key, input.Shift, input.Ctrl);
        // Зажатая клавиша правки повторяется: после RepeatDelay — каждые RepeatEvery.
        string? held = null;
        foreach (var key in input.Keys)
            if (key is "backspace" or "delete" or "left" or "right") held = key;
        if (held is null) _repeatKey = null;
        else if (input.Hits.Contains(held) || _repeatKey != held) (_repeatKey, _repeatAt) = (held, input.Time + RepeatDelay);
        else if (input.Time >= _repeatAt)
        {
            Key(edit, held, input.Shift, input.Ctrl);
            _repeatAt = input.Time + RepeatEvery;
        }
        if (input.Text.Length > 0 && !input.Ctrl) Replace(edit, input.Text);
        if (edit.Value != before) Report(id, edit.Value);
    }

    private void Report(string id, string value)
    {
        var at = _edits.FindIndex(e => e.Id == id);
        if (at >= 0) _edits[at] = (id, value);
        else _edits.Add((id, value));
    }

    private void Key(Edit edit, string key, bool shift, bool ctrl)
    {
        int from = Math.Min(edit.Anchor, edit.Caret), to = Math.Max(edit.Anchor, edit.Caret);
        void Move(int caret)
        {
            edit.Caret = Math.Clamp(caret, 0, edit.Value.Length);
            if (!shift) edit.Anchor = edit.Caret;
        }
        if (ctrl)
        {
            switch (key)
            {
                case "a":
                    (edit.Anchor, edit.Caret) = (0, edit.Value.Length);
                    break;
                case "c" or "x":
                    if (from != to) Clipboard.Set(edit.Value[from..to]);
                    if (key == "x") Replace(edit, "");
                    break;
                case "v":
                    Replace(edit, Clipboard.Get());
                    break;
                case "left":
                    Move(Word(edit.Value, edit.Caret, -1));
                    break;
                case "right":
                    Move(Word(edit.Value, edit.Caret, 1));
                    break;
                case "backspace" or "delete":
                    if (from == to) edit.Anchor = Word(edit.Value, edit.Caret, key == "backspace" ? -1 : 1);
                    Replace(edit, "");
                    break;
            }
            return;
        }
        switch (key)
        {
            case "backspace":
                if (from == to && from > 0) edit.Anchor = from - 1;
                Replace(edit, "");
                break;
            case "delete":
                if (from == to && from < edit.Value.Length)
                {
                    edit.Anchor = from;
                    edit.Caret = from + 1;
                }
                Replace(edit, "");
                break;
            case "left":
                Move(!shift && from != to ? from : edit.Caret - 1);
                break;
            case "right":
                Move(!shift && from != to ? to : edit.Caret + 1);
                break;
            case "home":
                Move(0);
                break;
            case "end":
                Move(edit.Value.Length);
                break;
        }
    }

    /// <summary>Начало слова слева (dir −1) или конец справа (dir 1) от места at.</summary>
    private static int Word(string text, int at, int dir)
    {
        if (dir < 0)
        {
            while (at > 0 && char.IsWhiteSpace(text[at - 1])) at--;
            while (at > 0 && !char.IsWhiteSpace(text[at - 1])) at--;
        }
        else
        {
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            while (at < text.Length && !char.IsWhiteSpace(text[at])) at++;
        }
        return at;
    }

    /// <summary>Заменяет выделение текстом: переводы строки — пробелами, управляющие знаки и лишнее по digits и maxLength отбрасываются.</summary>
    private static void Replace(Edit edit, string text)
    {
        var clean = new StringBuilder(text.Length);
        var newline = false;
        foreach (var c in text)
        {
            // Подряд идущие переводы строки — один пробел (как replace(/[\r\n]+/g, ' ') в ui.ts).
            if (c is '\r' or '\n')
            {
                if (!newline && !edit.Digits) clean.Append(' ');
                newline = true;
                continue;
            }
            newline = false;
            if (!char.IsControl(c) && (!edit.Digits || c is >= '0' and <= '9')) clean.Append(c);
        }
        int from = Math.Min(edit.Anchor, edit.Caret), to = Math.Max(edit.Anchor, edit.Caret);
        var insert = clean.ToString();
        if (edit.MaxLength > 0) insert = insert[..Math.Clamp(edit.MaxLength - (edit.Value.Length - (to - from)), 0, insert.Length)];
        edit.Value = edit.Value[..from] + insert + edit.Value[to..];
        edit.Caret = edit.Anchor = from + insert.Length;
    }

    /// <summary>Конец кадра: поля, которых больше не описывают, теряют правку и фокус.</summary>
    private void EndFields()
    {
        foreach (var id in _fields.Keys.ToArray())
            if (!_seenFields.Contains(id)) _fields.Remove(id);
        if (_focused is not null && !_seenFields.Contains(_focused)) _focused = null;
    }

    /// <summary>Поле ввода со стилем по умолчанию.</summary>
    public Field Input(string id, string value, FieldOptions options = default) => Input(id, value, default, options);

    /// <summary>
    /// Поле ввода — узел id с утопленной рамкой, текстом, мигающей кареткой и выделением (как <c>input</c> в ui.ts).
    /// Не заданное в style (рамка, высота, отступы, рост) берётся как у TS. Правки пользователя — в
    /// <see cref="FrameEdits"/>; value, что поменяла панель сама, перебивает набранное.
    /// </summary>
    public Field Input(string id, string value, Style style, FieldOptions options = default)
    {
        _seenFields.Add(id);
        if (!_fields.TryGetValue(id, out var edit))
            _fields[id] = edit = new Edit { Value = value, Given = value, Caret = value.Length, Anchor = value.Length };
        else if (value != edit.Given)
        {
            // Панель поставила своё значение (отправили, подставили подсказку) — каретка в конец.
            edit.Given = value;
            if (value != edit.Value) (edit.Value, edit.Caret, edit.Anchor) = (value, value.Length, value.Length);
        }
        if (options.Focus && _focused != id) _focused = id;
        (edit.Digits, edit.Password, edit.MaxLength) = (options.Digits, options.Password, options.MaxLength);
        if (options.Digits) edit.Value = new string([.. edit.Value.Where(char.IsAsciiDigit)]);
        if (options.MaxLength > 0 && edit.Value.Length > options.MaxLength) edit.Value = edit.Value[..options.MaxLength];
        edit.Caret = Math.Clamp(edit.Caret, 0, edit.Value.Length);
        edit.Anchor = Math.Clamp(edit.Anchor, 0, edit.Value.Length);
        var focused = _focused == id;
        var shown = edit.Shown;
        var box = style with { Id = id, Clip = true };
        if (options.Width is { } width) box.Width = width;
        if (box.Width is null && box.Grow == 0) box.Grow = 1;
        if (box.MinWidth == 0) box.MinWidth = 40;
        box.Height ??= FieldHeight;
        if (box.Padding == default) box.Padding = (6, FieldPadX);
        if (options.Background is { } fill) (box.Background, box.Skin) = (fill, null);
        else if (box.Skin is null && box.Background is null) box.Skin = Ui.Skin.Well;
        box.Outline = options.Outline ?? box.Outline;
        edit.PadLeft = box.Padding.Left;
        // Длинный текст сдвигается так, чтобы каретка была видна; без фокуса поле показывает начало.
        var room = box.Width ?? RectOf(id)?.W ?? 120;
        edit.Shift = focused ? MathF.Max(0, Fonts.Width(FontName.Body, shown[..edit.Caret]) - (room - 18)) : 0;
        var text = new TextStyle { Color = options.Color ?? TextColor };
        var current = edit;
        Box(box, () =>
        {
            // Отступ слева отрицательный: строка уезжает влево, и каретка остаётся в поле.
            Box(new Style { Direction = Direction.Row, Align = Align.Center, Padding = (0, 0, 0, -current.Shift) }, () =>
            {
                if (shown.Length == 0 && options.Placeholder is { } hint) Text(hint, new TextStyle { Color = PlaceholderColor });
                if (focused && current.Anchor != current.Caret)
                {
                    int from = Math.Min(current.Anchor, current.Caret), to = Math.Max(current.Anchor, current.Caret);
                    if (from > 0) Text(shown[..from], text);
                    Box(new Style { Background = SelectionColor }, () => Text(shown[from..to], text with { Color = CaretColor }));
                    if (to < shown.Length) Text(shown[to..], text);
                }
                else
                {
                    if (current.Caret > 0) Text(shown[..current.Caret], text);
                    // Каретка мигает дважды в секунду, пока поле в фокусе.
                    if (focused)
                    {
                        if ((long)Math.Floor(Now / 0.5) % 2 == 0) Leaf(default, LeafKind.Caret);
                        else Space(1, Fonts.Line(FontName.Body));
                    }
                    if (current.Caret < shown.Length) Text(shown[current.Caret..], text);
                }
            });
        });
        return new Field(edit.Value, focused, _fieldKeys.Count == 0 ? [] : [.. _fieldKeys]);
    }
}
