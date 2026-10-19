using Kharos.Gpu;

namespace Kharos.Client.Ui;

/// <summary>Кнопки указателя по порядку оболочки: левая, правая, средняя.</summary>
[Flags]
public enum PointerButtons : byte { None = 0, Left = 1, Right = 2, Middle = 4 }

/// <summary>Ввод интерфейса за кадр — то, что gui.rs берёт из <c>Input</c>; заполняет оболочка.</summary>
public record struct UiInput()
{
    /// <summary>Секунды с запуска.</summary>
    public double Time { get; set; }
    /// <summary>Экран в логических пикселях и физических пикселей на логический.</summary>
    public float Width { get; set; }
    public float Height { get; set; }
    public float Scale { get; set; } = 1;
    /// <summary>Указатель в логических пикселях; null — вне окна.</summary>
    public (float X, float Y)? Pointer { get; set; }
    /// <summary>Нажаты и отпущены с прошлого кадра.</summary>
    public PointerButtons Pressed { get; set; }
    public PointerButtons Released { get; set; }
    /// <summary>Колесо с прошлого кадра: строк, вверх — больше нуля.</summary>
    public float Wheel { get; set; }
    public bool Shift { get; set; }
    /// <summary>Ctrl или Meta.</summary>
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    /// <summary>Последним указателем было касание, а не мышь.</summary>
    public bool Touch { get; set; }
    /// <summary>Набранный за кадр текст (для поля ввода).</summary>
    public string Text { get; set; } = "";
    /// <summary>Клавиши, нажатые за кадр ("a", "left", "escape", "backspace"…), и зажатые сейчас — для поля ввода и модального окна.</summary>
    public IReadOnlyList<string> Hits { get; set; } = [];
    public IReadOnlyList<string> Keys { get; set; } = [];
}

/// <summary>Узел с номером в прошлом кадре: видимая часть и ближайший предок с номером (индекс в списке, −1 — нет).</summary>
public readonly record struct Hit(string Id, Rect Rect, int Parent);

/// <summary>Картинка облика для интерфейса: uv в атласе обликов и размер в пикселях (портрет или кадр значка).</summary>
public readonly record struct Icon(Uv Uv, float Width, float Height);

/// <summary>
/// Интерфейс в немедленном режиме — перенос <c>apps/client/src/gui.rs</c> (он же <c>gui/ui.ts</c>). Панели
/// описываются вызовами каждый кадр, из них собирается дерево, в <see cref="Finish(Batch)"/> оно раскладывается (ряды,
/// столбцы и стопки с промежутками, отступами, выравниванием и растяжением — как flex) и рисуется в батч. Состояние
/// панелей — у их кода; интерфейс помнит места узлов прошлого кадра: по ним видно, что под указателем и что нажали.
/// <para>
/// Ядро — данные: <see cref="Style"/>, <see cref="TextStyle"/>, <see cref="ImageSpec"/> и строковые номера. Удобный
/// C# (<see cref="Box"/>, <see cref="Button"/> с лямбдами) — обёртка над <see cref="Open"/>/<see cref="Close"/>/
/// <see cref="Text"/>/<see cref="Image(ImageSpec)"/>, которые без изменений лягут в поток команд мода клиента.
/// </para>
/// </summary>
public sealed partial class Gui
{
    /// <summary>Через сколько секунд наведения появляется подсказка.</summary>
    public const double TipDelay = 0.35;
    /// <summary>Сколько держать палец, чтобы нажатие стало долгим (правым щелчком), и насколько можно сдвинуть его.</summary>
    public const double Hold = 0.5;
    public const float HoldSlop = 8;
    /// <summary>Ширина подсказки, дальше строки переносятся.</summary>
    public const float TipWidth = 320;
    /// <summary>Шаг прокрутки колесом.</summary>
    public const float WheelLine = 40;
    /// <summary>Цвет текста по умолчанию.</summary>
    public static readonly Color TextColor = Color.Hex(0xd6e6f5);

    private sealed record Press(string? Id, byte Button, float X, float Y)
    {
        public double Time;
        public bool Long;
    }

    private readonly List<Node> _pool = [];
    private int _used;
    private (float X, float Y)? _pointer;
    private Press? _press;
    private Dictionary<string, Click> _clicks = [], _frameClicks = [];
    /// <summary>Места узлов с номерами по прошлому кадру — в порядке рисования.</summary>
    private List<Hit> _hits = [], _nextHits = [];
    private readonly List<Rect> _roots = [];
    private readonly Dictionary<string, float> _scrolls = [];
    private string? _over;
    private double _since;
    /// <summary>Дерево кадра: корневые панели и открытые узлы; подсказки — готовыми панелями, показывается последняя.</summary>
    private readonly List<Node> _top = [];
    private List<Node> _stack = [];
    private readonly List<Node> _tips = [];
    private readonly Stack<List<Node>> _savedStacks = new();
    /// <summary>Кнопки, что встречались: номер — недоступна ли (как в gui.rs, не забываются).</summary>
    private readonly Dictionary<string, bool> _buttons = [];

    public Gui(Fonts fonts) => Fonts = fonts;

    public Fonts Fonts { get; }
    /// <summary>Батч по умолчанию для <see cref="Finish()"/>.</summary>
    public Batch Batch { get; } = new();
    public float Width { get; private set; }
    public float Height { get; private set; }
    public double Now { get; private set; }
    /// <summary>Указатель над интерфейсом по прошлому кадру.</summary>
    public bool Covered { get; private set; }
    /// <summary>Кнопку нажали за кадр (недоступна ли она) — для звука интерфейса.</summary>
    public bool? PressedButton { get; private set; }
    /// <summary>Навели на доступную кнопку.</summary>
    public bool HoveredButton { get; private set; }
    /// <summary>Щелчки этого кадра по номерам.</summary>
    public IReadOnlyDictionary<string, Click> FrameClicks => _frameClicks;
    /// <summary>Места узлов с номерами по прошлому кадру.</summary>
    public IReadOnlyList<Hit> Hits => _hits;
    /// <summary>Корневые панели последнего кадра (после <see cref="Finish(Batch)"/>).</summary>
    public IReadOnlyList<Node> Roots => _top;

    /// <summary>Округление как у Rust f32::round: половина — от нуля.</summary>
    public static float Round(float value) => MathF.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>Байт с округлением и насыщением, как <c>as u8</c> в Rust.</summary>
    public static byte Byte(float value) => (byte)Math.Clamp(Round(value), 0, 255);

    /// <summary>Верхний узел с номером в точке: индекс в местах прошлого кадра, −1 — нет.</summary>
    private int Topmost(float x, float y)
    {
        for (var i = _hits.Count - 1; i >= _modalFrom; i--)
            if (_hits[i].Rect.Contains(x, y)) return i;
        return -1;
    }

    private string? TopmostId(float x, float y) => Topmost(x, y) is >= 0 and var i ? _hits[i].Id : null;

    /// <summary>Лежит ли в точке панель или узел с номером.</summary>
    public bool Covers(float x, float y)
    {
        if (ModalOpen) return true;
        foreach (var rect in _roots) if (rect.Contains(x, y)) return true;
        foreach (var hit in _hits) if (hit.Rect.Contains(x, y)) return true;
        return false;
    }

    /// <summary>Начало кадра: ввод против мест прошлого кадра — нажатия, щелчки, долгое касание, колесо.</summary>
    public void Begin(in UiInput input)
    {
        Fonts.SetScale(input.Scale);
        (Width, Height, Now, _pointer) = (input.Width, input.Height, input.Time, input.Pointer);
        Batch.Clear();
        _top.Clear();
        _stack.Clear();
        _tips.Clear();
        _savedStacks.Clear();
        _used = 0;
        PressedButton = null;
        HoveredButton = false;
        BeginFields(input);
        var shift = input.Shift;
        var ctrl = input.Ctrl;
        var alt = input.Alt;
        Click Modifiers(byte button, bool held) => new(button, shift, ctrl, alt, held);
        var (px, py) = input.Pointer ?? (-1, -1);
        Covered = input.Pointer is not null && Covers(px, py);
        // Кнопки указателя: левая — 0, правая — 2, средняя — 1, как у страницы.
        foreach (var (flag, button) in (ReadOnlySpan<(PointerButtons, byte)>)[(PointerButtons.Left, 0), (PointerButtons.Right, 2), (PointerButtons.Middle, 1)])
        {
            if (input.Pressed.HasFlag(flag))
            {
                var id = TopmostId(px, py);
                if (id is not null && _buttons.TryGetValue(id, out var disabled) && button == 0) PressedButton = disabled;
                _press = new Press(id, button, px, py) { Time = input.Time };
                PressOnFields(id, button, input);
                PressOnBar(id, button, py);
            }
            if (input.Released.HasFlag(flag) && _press is { } press)
            {
                _press = null;
                if (!press.Long && press.Button == button && TopmostId(px, py) is { } hit && hit == press.Id)
                    _clicks[hit] = Modifiers(button, false);
            }
        }
        // Долгое касание — правый щелчок: у сенсорного экрана правой кнопки нет.
        if (_press is { Button: 0, Long: false, Id: { } pressed } hold && input.Touch)
        {
            if (MathF.Sqrt((px - hold.X) * (px - hold.X) + (py - hold.Y) * (py - hold.Y)) > HoldSlop) hold.Time = double.PositiveInfinity;
            else if (input.Time - hold.Time >= Hold)
            {
                hold.Long = true;
                _clicks[pressed] = Modifiers(2, true);
            }
        }
        DragScrollbar(input);
        DragSelection(input);
        TypeIntoField(input);
        // Колесо — ближайшему прокручиваемому списку под указателем.
        if (input.Wheel != 0 && input.Pointer is not null)
        {
            for (var index = Topmost(px, py); index >= 0; index = _hits[index].Parent)
                if (_scrolls.ContainsKey(_hits[index].Id))
                {
                    _scrolls[_hits[index].Id] -= input.Wheel * WheelLine;
                    break;
                }
        }
        (_frameClicks, _clicks) = (_clicks, _frameClicks);
        _clicks.Clear();
        var now = input.Pointer is not null ? TopmostId(px, py) : null;
        if (now != _over)
        {
            if (now is not null && _buttons.TryGetValue(now, out var off) && !off) HoveredButton = true;
            _over = now;
            _since = Now;
        }
    }

    // ------------------------------------------------------------------ описание кадра

    private Node Rent(in Style style, LeafKind kind)
    {
        if (_used == _pool.Count) _pool.Add(new Node());
        var node = _pool[_used++];
        node.Reset(style, kind);
        return node;
    }

    /// <summary>Открывает узел: следующие узлы — его дети до <see cref="Close"/>. На верхнем уровне — корневая панель.</summary>
    public Scope Open(in Style style)
    {
        _stack.Add(Rent(style, LeafKind.Box));
        return new Scope(this);
    }

    public void Close()
    {
        if (_stack.Count == 0) throw new InvalidOperationException("нет открытого узла");
        var node = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        if (_stack.Count > 0) _stack[^1].Children.Add(node);
        else _top.Add(node);
    }

    /// <summary>Закрывает узел в конце <c>using</c>.</summary>
    public readonly ref struct Scope(Gui gui)
    {
        public void Dispose() => gui.Close();
    }

    private Node Leaf(in Style style, LeafKind kind)
    {
        var node = Rent(style, kind);
        if (_stack.Count > 0) _stack[^1].Children.Add(node);
        else _top.Add(node);
        return node;
    }

    /// <summary>Узел с детьми: children строит их.</summary>
    public void Box(in Style style, Action? children = null)
    {
        Open(style);
        try { children?.Invoke(); }
        finally { Close(); }
    }

    /// <summary>Строка текста (с \n — несколько строк).</summary>
    public void Text(string text, in TextStyle style)
    {
        var node = Leaf(default, LeafKind.Text);
        node.Text = text;
        node.TextStyle = style;
    }

    public void Text(string text) => Text(text, new TextStyle());

    public void Image(ImageSpec image)
    {
        var node = Leaf(default, LeafKind.Image);
        node.Image = image;
    }

    /// <summary>Облик вида, вписанный в width × height, как toCanvas в portraits.ts: вдвое и больше — целым увеличением.</summary>
    public void Image(Icon icon, float width, float height, int team = 0)
    {
        var fit = MathF.Min(width / icon.Width, height / icon.Height);
        var scale = fit >= 2 ? MathF.Floor(fit) : fit;
        float w = Round(icon.Width * scale), h = Round(icon.Height * scale);
        Open(new Style { Width = width, Height = height, Padding = (MathF.Floor((height - h) / 2), 0, 0, MathF.Floor((width - w) / 2)) });
        Image(new ImageSpec(ImageKind.Sprite, w, h, icon.Uv, team));
        Close();
    }

    /// <summary>Монета кредитов.</summary>
    public void Coin(float size = 16) => Image(new ImageSpec(ImageKind.Coin, size, size));

    /// <summary>Мини-карта с туманом поверх.</summary>
    public void Minimap(float width, float height) => Image(new ImageSpec(ImageKind.Minimap, width, height));

    /// <summary>Пустое место; grow — растёт в остаток.</summary>
    public void Space(float width = 0, float height = 0, float grow = 0) =>
        Leaf(new Style { Width = width, Height = height, Grow = grow }, LeafKind.Space);

    public bool Hovered(string id) => _over == id;

    /// <summary>Указатель на узле id или на его потомке.</summary>
    public bool Within(string id)
    {
        if (_over is null) return false;
        if (_over == id) return true;
        for (var i = 0; i < _hits.Count; i++)
        {
            if (_hits[i].Id != _over) continue;
            for (var parent = _hits[i].Parent; parent >= 0; parent = _hits[parent].Parent)
                if (_hits[parent].Id == id) return true;
        }
        return false;
    }

    /// <summary>Зажата ли левая кнопка на узле id: где указатель в нём (доли ширины и высоты).</summary>
    public (float U, float V)? Held(string id)
    {
        if (_press is not { Button: 0 } press || press.Id != id) return null;
        if (RectOf(id) is not { } rect || _pointer is not { } pointer) return null;
        return (Math.Clamp((pointer.X - rect.X) / rect.W, 0, 1), Math.Clamp((pointer.Y - rect.Y) / rect.H, 0, 1));
    }

    public bool Pressing(string id) => _press is { Button: 0 } press && press.Id == id;

    public Click? Clicked(string id) => _frameClicks.TryGetValue(id, out var click) ? click : null;

    /// <summary>Рамка кнопки по состоянию (недоступна, нажата, наведена), как button в ui.ts.</summary>
    public Skin ButtonSkin(string id, Skin? skin, bool active, bool disabled)
    {
        _buttons[id] = disabled;
        var hover = !disabled && Hovered(id);
        var held = hover && Pressing(id);
        if (disabled) return Skin.ButtonDisabled;
        if (active || held) return Skin.ButtonActive;
        if (hover) return skin == Ui.Skin.ButtonPrimary ? Skin.ButtonPrimaryHover : Skin.ButtonHover;
        return skin ?? Skin.Button;
    }

    /// <summary>
    /// Кнопка: рамка по наведению и нажатию; active — нажатое состояние, disabled — не нажимается. Стиль по умолчанию —
    /// <see cref="Style.Button"/>. Возвращает щелчок в кадре после него.
    /// </summary>
    public Click? Button(string id, Style? style = null, Action? children = null, bool active = false, bool disabled = false)
    {
        var given = style ?? Style.Button;
        Box(given with { Id = id, Skin = ButtonSkin(id, given.Skin, active, disabled) }, children);
        return disabled ? null : Clicked(id);
    }

    /// <summary>Пора ли показать подсказку узла id: указатель на нём дольше <see cref="TipDelay"/> и ничего не нажато.</summary>
    public bool TipDue(string id) => Hovered(id) && _press is null && Now - _since >= TipDelay;

    /// <summary>Начало подсказки: дальше узлы кладутся в её панель до <see cref="EndTip"/>.</summary>
    public void BeginTip()
    {
        _savedStacks.Push(_stack);
        _stack = [];
        Open(new Style { Direction = Direction.Column, Gap = 2, Padding = (6, 10), Skin = Ui.Skin.Panel, Passive = true });
    }

    /// <summary>Конец подсказки: панель — в подсказки кадра; anchor — где она на экране, без него — у указателя.</summary>
    public void EndTip(Anchor? anchor = null)
    {
        while (_stack.Count > 1)
        {
            var node = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            _stack[^1].Children.Add(node);
        }
        if (_stack.Count == 1)
        {
            var panel = _stack[0];
            panel.Style = panel.Style with { Anchor = anchor };
            _tips.Add(panel);
        }
        _stack = _savedStacks.Pop();
    }

    /// <summary>Подсказка строкой, с переносом по <see cref="TipWidth"/>.</summary>
    public void Tip(string id, string text)
    {
        if (text.Length == 0 || !TipDue(id)) return;
        BeginTip();
        Text(text, new TextStyle { Wrap = TipWidth });
        EndTip();
    }

    /// <summary>Подсказка карточкой: build строит панель узлами; anchor — где она на экране.</summary>
    public void Tip(string id, Action build, Anchor? anchor = null)
    {
        if (!TipDue(id)) return;
        BeginTip();
        try { build(); }
        finally { EndTip(anchor); }
    }

    /// <summary>Место узла id в прошлом кадре.</summary>
    public Rect? RectOf(string id)
    {
        foreach (var hit in _hits) if (hit.Id == id) return hit.Rect;
        return null;
    }

    /// <summary>Ширина строки шрифтом font.</summary>
    public float Measure(string text, FontName font = FontName.Body) => Fonts.Width(font, text);

    // ------------------------------------------------------------------ раскладка и рисование

    /// <summary>Конец кадра в собственный <see cref="Batch"/>.</summary>
    public void Finish() => Finish(Batch);

    /// <summary>Конец кадра: подсказка, раскладка, места для ввода, рисование в batch (дописывается).</summary>
    public void Finish(Batch batch)
    {
        // Незакрытая подсказка закрывается первой: её узлы — в её панель, а не в корни.
        while (_savedStacks.Count > 0) EndTip();
        while (_stack.Count > 0) Close();
        // Модальные окна — поверх прочих панелей (в порядке описания), подсказка — поверх всего.
        StackModals();
        // Подсказка — своей корневой панелью у указателя (или где ей велено), поверх всего; нажатия сквозь неё идут.
        if (_tips.Count > 0)
        {
            var tip = _tips[^1];
            Layout.Measure(Fonts, tip, Width - 16);
            if (tip.Style.Anchor is null)
            {
                var (px, py) = _pointer ?? (0, 0);
                var (w, h) = tip.Want;
                var x = px + 14;
                if (x + w > Width - 8) x = px - 14 - w;
                var y = py - h - 12;
                if (y < 8) y = py + 20;
                tip.Style = tip.Style with { Anchor = new Anchor(Left: Math.Clamp(x, 8, MathF.Max(Width - 8 - w, 8)), Top: Math.Clamp(y, 8, MathF.Max(Height - 8 - h, 8))) };
            }
            _top.Add(tip);
        }
        foreach (var root in _top)
        {
            var anchor = root.Style.Anchor ?? default;
            Layout.Measure(Fonts, root, Width - (anchor.Left ?? 0) - (anchor.Right ?? 0));
            Layout.Place(root, Layout.Anchored(root, Width, Height), _scrolls, _rooms);
        }
        _nextHits.Clear();
        _roots.Clear();
        _nextBars.Clear();
        _nextKeeps.Clear();
        _nextModal = -1;
        var screen = new Rect(0, 0, Width, Height);
        foreach (var root in _top)
        {
            Walk(root, screen, -1);
            if (!root.Style.Passive) _roots.Add(root.Rect);
        }
        (_hits, _nextHits) = (_nextHits, _hits);
        (_bars, _nextBars) = (_nextBars, _bars);
        (_keeps, _nextKeeps) = (_nextKeeps, _keeps);
        ModalOpen = _nextModal >= 0;
        ModalId = ModalOpen ? _hits[_nextModal].Id : null;
        _modalFrom = Math.Max(_nextModal, 0);
        EndFields();
        foreach (var root in _top) Layout.Draw(Fonts, batch, root, null, 1);
    }

    private void Walk(Node node, Rect clip, int parent)
    {
        var visible = node.Rect.Intersect(clip);
        if (node.Style.Id is { } id)
        {
            _nextHits.Add(new Hit(id, visible, parent));
            parent = _nextHits.Count - 1;
        }
        if (node.Style.Modal && node.Style.Id is not null) _nextModal = Math.Max(_nextModal, parent);
        if (node.Style.KeepFocus && node.Style.Id is { } kept) _nextKeeps.Add(kept);
        var inner = node.Style.Clips ? visible : clip;
        foreach (var child in node.Children) Walk(child, inner, parent);
        WalkScrollbar(node, visible, parent);
    }
}
