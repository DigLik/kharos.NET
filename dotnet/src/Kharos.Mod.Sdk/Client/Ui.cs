using System.Buffers.Binary;
using System.Text;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk.Client;

/// <summary>
/// Интерфейс клиентского мода — тот же API, что Gui клиента (и c.ui пилота): панели описываются каждый кадр, клиент их
/// раскладывает и рисует. Вызовы пишутся записями в выход кадра (ClientAbi.Op*), а состояние узлов (наведение, щелчки,
/// места) приходит во входе кадра — по местам прошлого кадра, как у Gui. Щелчок приходит кадром позже.
/// </summary>
public sealed class Ui
{
    private readonly ByteWriter _out;
    private readonly Dictionary<string, IdState> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _edits = new(StringComparer.Ordinal);
    private int _depth;

    private readonly record struct IdState(byte Flags, Rect Rect, float U, float V, Click Click);

    internal Ui(ByteWriter output) => _out = output;

    /// <summary>Экран в логических пикселях.</summary>
    public float Width { get; private set; }
    public float Height { get; private set; }

    /// <summary>Секунды интерфейса.</summary>
    public double Now { get; private set; }

    /// <summary>Указатель над интерфейсом (по прошлому кадру).</summary>
    public bool Covered { get; private set; }

    /// <summary>Нажали кнопку за кадр: недоступна ли она (для звука «deny»); null — не нажимали.</summary>
    public bool? PressedButton { get; private set; }

    /// <summary>Навели на доступную кнопку.</summary>
    public bool HoveredButton { get; private set; }

    /// <summary>Округление как у Rust f32::round: половина — от нуля.</summary>
    public static float Round(float value) => MathF.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>Байт с округлением и насыщением.</summary>
    public static byte Byte(float value) => (byte)Math.Clamp(Round(value), 0, 255);

    internal void Begin(ref ByteReader r)
    {
        _ids.Clear();
        _depth = 0;
        Width = r.F32();
        Height = r.F32();
        Now = r.F64();
        var flags = r.U8();
        Covered = (flags & ClientAbi.UiCovered) != 0;
        HoveredButton = (flags & ClientAbi.UiHoveredButton) != 0;
        PressedButton = (flags & ClientAbi.UiPressedButton) != 0 ? (flags & ClientAbi.UiPressedDisabled) != 0 : null;
        var count = r.U16();
        for (var i = 0; i < count; i++)
        {
            var id = r.Str();
            var state = r.U8();
            var rect = new Rect(r.F32(), r.F32(), r.F32(), r.F32());
            float u = 0, v = 0;
            if ((state & ClientAbi.IdHeld) != 0)
                (u, v) = (r.F32(), r.F32());
            var click = default(Click);
            if ((state & ClientAbi.IdClicked) != 0)
            {
                var button = r.U8();
                var mods = r.U8();
                click = new Click(button, (mods & ClientAbi.ClickShift) != 0, (mods & ClientAbi.ClickCtrl) != 0, (mods & ClientAbi.ClickAlt) != 0, (mods & ClientAbi.ClickLong) != 0);
            }
            _ids[id] = new IdState(state, rect, u, v, click);
        }
    }

    /// <summary>Правки полей ввода за кадр (вход кадра, ABI 2).</summary>
    internal void ReadEdits(ref ByteReader r)
    {
        _edits.Clear();
        for (int i = 0, n = r.U16(); i < n; i++)
        {
            var id = r.Str();
            _edits[id] = r.Str();
        }
    }

    /// <summary>Закрывает узлы, что обработчик оставил открытыми (после исключения).</summary>
    internal void Unwind(int depth)
    {
        while (_depth > depth)
            Close();
    }

    internal int Depth => _depth;

    private bool Has(string id, byte flag) => _ids.TryGetValue(id, out var state) && (state.Flags & flag) != 0;

    // ------------------------------------------------------------------ состояние узлов

    public bool Hovered(string id) => Has(id, ClientAbi.IdHovered);

    /// <summary>Указатель на узле id или на его потомке.</summary>
    public bool Within(string id) => Has(id, ClientAbi.IdWithin);

    /// <summary>Левая кнопка зажата на узле id.</summary>
    public bool Pressing(string id) => Has(id, ClientAbi.IdPressing);

    /// <summary>Зажата ли левая кнопка на узле id: где указатель в нём (доли ширины и высоты).</summary>
    public (float U, float V)? Held(string id) => _ids.TryGetValue(id, out var state) && (state.Flags & ClientAbi.IdHeld) != 0 ? (state.U, state.V) : null;

    /// <summary>Щелчок по узлу id в этом кадре.</summary>
    public Click? Clicked(string id) => _ids.TryGetValue(id, out var state) && (state.Flags & ClientAbi.IdClicked) != 0 ? state.Click : null;

    /// <summary>Пора ли показать подсказку узла id.</summary>
    public bool TipDue(string id) => Has(id, ClientAbi.IdTipDue);

    /// <summary>Место узла id в прошлом кадре.</summary>
    public Rect? RectOf(string id) => _ids.TryGetValue(id, out var state) ? state.Rect : null;

    /// <summary>Новое значение поля ввода id, если игрок правил его за кадр; null — не правил.</summary>
    public string? Edited(string id) => _edits.GetValueOrDefault(id);

    /// <summary>Все правки полей ввода за кадр: номер поля → значение.</summary>
    public IReadOnlyDictionary<string, string> Edits => _edits;

    /// <summary>Ширина строки шрифтом font (инструмент клиента: шрифты — у него).</summary>
    public float Measure(string text, FontName font = FontName.Body) => ClientTools.Measure(text, font);

    // ------------------------------------------------------------------ описание кадра

    /// <summary>Открывает узел: следующие — его дети до <see cref="Close"/>.</summary>
    public Scope Open(in Style style)
    {
        _out.U8(ClientAbi.OpOpen);
        WriteStyle(style);
        _depth++;
        return new Scope(this);
    }

    public void Close()
    {
        if (_depth == 0)
            throw new InvalidOperationException("нет открытого узла");
        _depth--;
        _out.U8(ClientAbi.OpClose);
    }

    /// <summary>Закрывает узел в конце <c>using</c>.</summary>
    public readonly ref struct Scope(Ui ui)
    {
        public void Dispose() => ui.Close();
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
        _out.U8(ClientAbi.OpText).Str(text);
        WriteText(style);
    }

    public void Text(string text) => Text(text, new TextStyle());

    /// <summary>Облик вида proto (номер вида), вписанный в width × height, стороной team (0 — своя).</summary>
    public void Image(int proto, float width, float height, int team = 0) =>
        _out.U8(ClientAbi.OpImageLook).I32(proto).F32(width).F32(height).U8((byte)team);

    /// <summary>Монета кредитов.</summary>
    public void Coin(float size = 16) => _out.U8(ClientAbi.OpImage).U8(ClientAbi.ImageCoin).F32(size).F32(size);

    /// <summary>Мини-карта с туманом поверх.</summary>
    public void Minimap(float width, float height) => _out.U8(ClientAbi.OpImage).U8(ClientAbi.ImageMinimap).F32(width).F32(height);

    /// <summary>
    /// Поле ввода id с текстом value (ABI 2): что набрал игрок, приходит кадром позже — <see cref="Edited"/>; value мод
    /// держит сам и показывает каждый кадр. Номер узла — id (Id стиля не нужен).
    /// </summary>
    public void Field(string id, string value, Style? style = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        _out.U8(ClientAbi.OpField).Str(id).Str(value);
        WriteStyle(style ?? new Style());
    }

    /// <summary>Значок внутри текста (ABI 2): ячейка cell облика вида proto (номер вида), size × size, своей стороной.</summary>
    public void Icon(int proto, int cell = 0, float size = 12) =>
        _out.U8(ClientAbi.OpIcon).I32(proto).U16(checked((ushort)cell)).F32(size);

    /// <summary>Пустое место; grow — растёт в остаток.</summary>
    public void Space(float width = 0, float height = 0, float grow = 0) => _out.U8(ClientAbi.OpSpace).F32(width).F32(height).F32(grow);

    /// <summary>
    /// Кнопка: рамка по наведению и нажатию; active — нажатое состояние, disabled — не нажимается. Стиль по умолчанию —
    /// <see cref="Style.Button"/>. Возвращает щелчок в кадре после него.
    /// </summary>
    public Click? Button(string id, Style? style = null, Action? children = null, bool active = false, bool disabled = false)
    {
        var given = style ?? Style.Button;
        _out.U8(ClientAbi.OpButton).Str(id).U8((byte)((active ? ClientAbi.ButtonActive : 0) | (disabled ? ClientAbi.ButtonDisabled : 0)));
        WriteStyle(given);
        _depth++;
        try { children?.Invoke(); }
        finally { Close(); }
        return disabled ? null : Clicked(id);
    }

    /// <summary>Подсказка строкой, с переносом.</summary>
    public void Tip(string id, string text)
    {
        if (text.Length == 0 || !TipDue(id)) return;
        _out.U8(ClientAbi.OpTipText).Str(id).Str(text);
    }

    /// <summary>Подсказка карточкой: build строит панель узлами; anchor — где она на экране, без него — у указателя.</summary>
    public void Tip(string id, Action build, Anchor? anchor = null)
    {
        if (!TipDue(id)) return;
        _out.U8(ClientAbi.OpTipBegin);
        var depth = _depth;
        try { build(); }
        finally
        {
            Unwind(depth);
            _out.U8(ClientAbi.OpTipEnd);
            WriteAnchor(anchor);
        }
    }

    // ------------------------------------------------------------------ запись стиля

    private void WriteStyle(in Style s)
    {
        uint mask = 0;
        if (s.Id is not null) mask |= ClientAbi.StyleId;
        if (s.Direction != Direction.Column) mask |= ClientAbi.StyleDirection;
        if (s.Self is not null) mask |= ClientAbi.StyleSelf;
        if (s.Gap != 0) mask |= ClientAbi.StyleGap;
        if (s.Padding != default) mask |= ClientAbi.StylePadding;
        if (s.Align != Align.Start) mask |= ClientAbi.StyleAlign;
        if (s.Justify != Align.Start) mask |= ClientAbi.StyleJustify;
        if (s.Width is not null) mask |= ClientAbi.StyleWidth;
        if (s.Height is not null) mask |= ClientAbi.StyleHeight;
        if (s.MinWidth != 0) mask |= ClientAbi.StyleMinWidth;
        if (s.MaxWidth is not null) mask |= ClientAbi.StyleMaxWidth;
        if (s.MinHeight != 0) mask |= ClientAbi.StyleMinHeight;
        if (s.MaxHeight is not null) mask |= ClientAbi.StyleMaxHeight;
        if (s.Grow != 0) mask |= ClientAbi.StyleGrow;
        if (s.Shrink) mask |= ClientAbi.StyleShrink;
        if (s.Wrap) mask |= ClientAbi.StyleWrap;
        if (s.Skin is not null) mask |= ClientAbi.StyleSkin;
        if (s.Background is not null) mask |= ClientAbi.StyleBackground;
        if (s.Outline is not null) mask |= ClientAbi.StyleOutline;
        if (s.Alpha is not null) mask |= ClientAbi.StyleAlpha;
        if (s.Clip) mask |= ClientAbi.StyleClip;
        if (s.Scroll) mask |= ClientAbi.StyleScroll;
        if (s.Anchor is not null) mask |= ClientAbi.StyleAnchor;
        if (s.Passive) mask |= ClientAbi.StylePassive;
        _out.U32(mask);
        if (s.Id is { } id) _out.Str(id);
        if ((mask & ClientAbi.StyleDirection) != 0) _out.U8((byte)s.Direction);
        if (s.Self is { } self) _out.U8((byte)self.Horizontal).U8((byte)self.Vertical);
        if ((mask & ClientAbi.StyleGap) != 0) _out.F32(s.Gap);
        if ((mask & ClientAbi.StylePadding) != 0) _out.F32(s.Padding.Top).F32(s.Padding.Right).F32(s.Padding.Bottom).F32(s.Padding.Left);
        if ((mask & ClientAbi.StyleAlign) != 0) _out.U8((byte)s.Align);
        if ((mask & ClientAbi.StyleJustify) != 0) _out.U8((byte)s.Justify);
        if (s.Width is { } width) _out.F32(width);
        if (s.Height is { } height) _out.F32(height);
        if ((mask & ClientAbi.StyleMinWidth) != 0) _out.F32(s.MinWidth);
        if (s.MaxWidth is { } maxWidth) _out.F32(maxWidth);
        if ((mask & ClientAbi.StyleMinHeight) != 0) _out.F32(s.MinHeight);
        if (s.MaxHeight is { } maxHeight) _out.F32(maxHeight);
        if ((mask & ClientAbi.StyleGrow) != 0) _out.F32(s.Grow);
        if (s.Skin is { } skin) _out.U8((byte)skin);
        if (s.Background is { } background) _out.U32(background.Rgba);
        if (s.Outline is { } outline) _out.U32(outline.Rgba);
        if (s.Alpha is { } alpha) _out.F32(alpha);
        if (s.Anchor is { } anchor) WriteAnchorBody(anchor);
    }

    private void WriteAnchor(Anchor? anchor)
    {
        if (anchor is { } value) WriteAnchorBody(value);
        else _out.U8(0);
    }

    private void WriteAnchorBody(Anchor a)
    {
        byte mask = 0;
        if (a.Left is not null) mask |= ClientAbi.AnchorLeft;
        if (a.Right is not null) mask |= ClientAbi.AnchorRight;
        if (a.Top is not null) mask |= ClientAbi.AnchorTop;
        if (a.Bottom is not null) mask |= ClientAbi.AnchorBottom;
        if (a.CenterX is not null) mask |= ClientAbi.AnchorCenterX;
        if (a.CenterY is not null) mask |= ClientAbi.AnchorCenterY;
        // Пустая привязка — тоже привязка (у корня — левый верхний угол): метка 0x80 отличает её от «нет».
        _out.U8(mask == 0 ? (byte)0x80 : mask);
        if (a.Left is { } left) _out.F32(left);
        if (a.Right is { } right) _out.F32(right);
        if (a.Top is { } top) _out.F32(top);
        if (a.Bottom is { } bottom) _out.F32(bottom);
        if (a.CenterX is { } centerX) _out.F32(centerX);
        if (a.CenterY is { } centerY) _out.F32(centerY);
    }

    private void WriteText(in TextStyle t)
    {
        byte mask = 0;
        if (t.Font != FontName.Body) mask |= ClientAbi.TextFont;
        if (t.Color != TextStyle.DefaultColor) mask |= ClientAbi.TextColor;
        if (t.Shadow is not null) mask |= ClientAbi.TextShadow;
        if (t.Lift != 1) mask |= ClientAbi.TextLift;
        if (t.Wrap is not null) mask |= ClientAbi.TextWrap;
        if (t.Ellipsis) mask |= ClientAbi.TextEllipsis;
        _out.U8(mask);
        if ((mask & ClientAbi.TextFont) != 0) _out.U8((byte)t.Font);
        if ((mask & ClientAbi.TextColor) != 0) _out.U32(t.Color.Rgba);
        if (t.Shadow is { } shadow) _out.U32(shadow.Rgba);
        if ((mask & ClientAbi.TextLift) != 0) _out.F32(t.Lift);
        if (t.Wrap is { } wrap) _out.F32(wrap);
    }
}

/// <summary>Инструменты клиента (kh.tool, ClientAbi.Tool*) — для API мода; ответы — в общем буфере до следующего вызова.</summary>
internal static class ClientTools
{
    private static byte[] _input = new byte[256];
    private static int _length;
    private static byte[] _output = new byte[4096];

    public static float Measure(string text, FontName font)
    {
        Begin();
        Byte((byte)font);
        Utf8(text);
        var output = Call(ClientAbi.ToolMeasure);
        return output.Length >= 4 ? BinaryPrimitives.ReadSingleLittleEndian(output) : 0;
    }

    /// <summary>48 Text: строка ключа key на языке игрока с аргументами.</summary>
    public static string Text(string key, ReadOnlySpan<string> args)
    {
        Begin();
        Str(key);
        U16(checked((ushort)args.Length));
        foreach (var arg in args) Str(arg);
        var output = Call(ClientAbi.ToolText);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(output);
        return Encoding.UTF8.GetString(output.Slice(2, length));
    }

    /// <summary>49 Prefs: байты настройки мода key (нет — пусто).</summary>
    public static byte[] Prefs(string key)
    {
        Begin();
        Str(key);
        return Call(ClientAbi.ToolPrefs).ToArray();
    }

    /// <summary>50 Roster: игроки партии.</summary>
    public static RosterEntry[] Roster()
    {
        Begin();
        var output = Call(ClientAbi.ToolRoster);
        var at = 0;
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(output);
        at += 4;
        var entries = new RosterEntry[count];
        for (var i = 0; i < count; i++)
        {
            var player = BinaryPrimitives.ReadUInt32LittleEndian(output[at..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(output[(at + 4)..]);
            var nick = Encoding.UTF8.GetString(output.Slice(at + 6, length));
            at += 6 + length;
            entries[i] = new RosterEntry(player, nick, output[at] != 0, output[at + 1] != 0);
            at += 2;
        }
        return entries;
    }

    /// <summary>51 Settings: JSON настроек партии и прав.</summary>
    public static string Settings()
    {
        Begin();
        return Encoding.UTF8.GetString(Call(ClientAbi.ToolSettings));
    }

    public static void Begin() => _length = 0;

    public static Span<byte> Room(int count)
    {
        if (_length + count > _input.Length)
            Array.Resize(ref _input, Math.Max(_length + count, _input.Length * 2));
        var room = _input.AsSpan(_length, count);
        _length += count;
        return room;
    }

    public static void Byte(byte value) => Room(1)[0] = value;
    public static void U16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Room(2), value);

    /// <summary>Строка str: u16 длина байт и UTF-8.</summary>
    public static void Str(string text)
    {
        var count = Encoding.UTF8.GetByteCount(text);
        U16(checked((ushort)count));
        Encoding.UTF8.GetBytes(text, Room(count));
    }
    public static void U32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Room(4), value);
    public static void F64(double value) => BinaryPrimitives.WriteDoubleLittleEndian(Room(8), value);
    public static void Utf8(string text) => Encoding.UTF8.GetBytes(text, Room(Encoding.UTF8.GetByteCount(text)));

    /// <summary>kh.tool с входом из буфера; ошибка — <see cref="ToolException"/>; мало места под ответ — растит и зовёт снова.</summary>
    public static ReadOnlySpan<byte> Call(int tool)
    {
        while (true)
        {
            var code = Host.Tool(tool, _input.AsSpan(0, _length), _output);
            if (code < 0)
                throw new ToolException(tool, code);
            if (code <= _output.Length)
                return _output.AsSpan(0, code);
            _output = new byte[Math.Max(code, _output.Length * 2)];
        }
    }
}
