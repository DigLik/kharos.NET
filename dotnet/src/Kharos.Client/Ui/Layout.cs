using Kharos.Gpu;

namespace Kharos.Client.Ui;

/// <summary>Чем узел является сам: коробка с детьми, строка текста, картинка или пустое место.</summary>
public enum LeafKind : byte { Box, Text, Image, Space, Caret }

/// <summary>Узел дерева кадра: стиль, лист и дети; после раскладки — желаемый размер и место. Узлы берутся из пула.</summary>
public sealed class Node
{
    public Style Style;
    public LeafKind Kind;
    public string Text = "";
    public TextStyle TextStyle;
    public ImageSpec Image;
    public readonly List<Node> Children = [];
    /// <summary>Строки текста после переноса и многоточия.</summary>
    public readonly List<string> Lines = [];
    /// <summary>Желаемый размер (measure) и место на экране (place).</summary>
    public (float W, float H) Want;
    public Rect Rect;
    /// <summary>Высота содержимого с отступами — для прокрутки.</summary>
    internal float Content;
    /// <summary>Ряд с переносом: номера детей по строкам; null — без переноса.</summary>
    internal List<List<int>>? WrapLines;
    /// <summary>Полоса прокрутки: русло, бегунок и состояние (0 — покой, 1 — наведена, 2 — тянут); после <see cref="Gui.Finish()"/>.</summary>
    internal bool HasBar;
    internal Rect BarTrack, BarThumb;
    internal byte BarState;

    internal void Reset(in Style style, LeafKind kind)
    {
        Style = style;
        Kind = kind;
        Text = "";
        TextStyle = default;
        Image = default;
        Children.Clear();
        Lines.Clear();
        Want = default;
        Rect = default;
        Content = 0;
        WrapLines = null;
        HasBar = false;
    }
}

/// <summary>Раскладка и рисование — measure, place и paint из gui.rs (они же из ui.ts).</summary>
internal static class Layout
{
    /// <summary>Толщина обводки в пикселях.</summary>
    private const float OutlineWidth = 3;

    private static bool Yields(Node node) =>
        node.Style.Width is null && (node.Style.Grow > 0 || node.Style.Shrink || node.Kind == LeafKind.Text && (node.TextStyle.Wrap is not null || node.TextStyle.Ellipsis));

    /// <summary>Желаемый размер узла при ширине room.</summary>
    public static (float W, float H) Measure(Fonts fonts, Node node, float room)
    {
        var style = node.Style;
        var pad = style.Padding;
        var inner = MathF.Max(MathF.Min(style.Width ?? room, style.MaxWidth ?? float.PositiveInfinity) - pad.Left - pad.Right, 0);
        float width, height;
        switch (node.Kind)
        {
            case LeafKind.Text:
            {
                var text = node.TextStyle;
                var lift = text.Shadow is not null ? text.Lift : 0;
                var space = MathF.Max((text.Wrap is { } wrap ? MathF.Min(wrap, inner) : inner) - lift, 1);
                node.Lines.Clear();
                foreach (var line in node.Text.Split('\n'))
                {
                    if (text.Wrap is not null) node.Lines.AddRange(fonts.Wrap(text.Font, line, space));
                    else node.Lines.Add(line);
                }
                if (text.Ellipsis)
                    for (var i = 0; i < node.Lines.Count; i++) node.Lines[i] = fonts.Clip(text.Font, node.Lines[i], MathF.Max(inner - lift, 1));
                width = 0;
                foreach (var line in node.Lines) width = MathF.Max(width, fonts.Width(text.Font, line));
                width += lift;
                height = Fonts.Line(text.Font) * node.Lines.Count;
                break;
            }
            case LeafKind.Image:
                (width, height) = (node.Image.Width, node.Image.Height);
                break;
            case LeafKind.Space:
                (width, height) = (0, 0);
                break;
            case LeafKind.Caret:
                (width, height) = (1, Fonts.Line(FontName.Body));
                break;
            default:
                if (style.Direction == Direction.Stack)
                {
                    (width, height) = (0, 0);
                    foreach (var child in node.Children)
                    {
                        var size = Measure(fonts, child, inner);
                        width = MathF.Max(width, size.W);
                        height = MathF.Max(height, size.H);
                    }
                    break;
                }
                (width, height) = MeasureFlow(fonts, node, inner);
                break;
        }
        node.Content = height + pad.Top + pad.Bottom;
        var w = MathF.Max(MathF.Min(style.Width ?? width + pad.Left + pad.Right, style.MaxWidth ?? float.PositiveInfinity), style.MinWidth);
        var h = MathF.Max(MathF.Min(style.Height ?? height + pad.Top + pad.Bottom, style.MaxHeight ?? float.PositiveInfinity), style.MinHeight);
        node.Want = (w, h);
        return node.Want;
    }

    /// <summary>Ряд или столбец: перенос строк, сжатие уступчивых детей, сумма вдоль и наибольшее поперёк.</summary>
    private static (float W, float H) MeasureFlow(Fonts fonts, Node node, float inner)
    {
        var style = node.Style;
        var row = style.Direction == Direction.Row;
        var gap = style.Gap;
        var children = node.Children;
        var gaps = gap * Math.Max(children.Count - 1, 0);
        var sumWidth = 0f;
        foreach (var child in children) sumWidth += Measure(fonts, child, inner).W;
        node.WrapLines = null;
        if (row && style.Wrap && sumWidth + gaps > inner)
        {
            var lines = new List<List<int>> { new() };
            var used = 0f;
            for (var index = 0; index < children.Count; index++)
            {
                var line = lines[^1];
                var want = children[index].Want.W;
                if (line.Count > 0 && used + gap + want > inner)
                {
                    lines.Add([index]);
                    used = want;
                }
                else
                {
                    used += (line.Count == 0 ? 0 : gap) + want;
                    line.Add(index);
                }
            }
            float width = 0, height = 0;
            foreach (var line in lines)
            {
                float lineWidth = gap * Math.Max(line.Count - 1, 0), lineHeight = 0;
                foreach (var i in line)
                {
                    lineWidth += children[i].Want.W;
                    lineHeight = MathF.Max(lineHeight, children[i].Want.H);
                }
                width = MathF.Max(width, lineWidth);
                height += lineHeight;
            }
            height += gap * Math.Max(lines.Count - 1, 0);
            node.WrapLines = lines;
            return (width, height);
        }
        var total = sumWidth + gaps;
        if (row && total > inner)
        {
            // Не влезает: уступчивые дети (растущие, сжимаемые, текст с переносом или многоточием) делят остаток,
            // начиная с самых узких.
            var flexible = new List<int>();
            for (var i = 0; i < children.Count; i++)
                if (Yields(children[i])) flexible.Add(i);
            for (var i = 1; i < flexible.Count; i++)
            {
                // Вставками — устойчиво, как sort_by в Rust.
                var item = flexible[i];
                var j = i - 1;
                for (; j >= 0 && children[flexible[j]].Want.W > children[item].Want.W; j--) flexible[j + 1] = flexible[j];
                flexible[j + 1] = item;
            }
            var free = inner - total;
            foreach (var i in flexible) free += children[i].Want.W;
            for (var index = 0; index < flexible.Count; index++)
            {
                var child = children[flexible[index]];
                var share = MathF.Max(free / (flexible.Count - index), 0);
                if (child.Want.W > share) Measure(fonts, child, share);
                free -= child.Want.W;
            }
        }
        float along = gaps, across = 0;
        foreach (var child in children)
        {
            along += row ? child.Want.W : child.Want.H;
            across = MathF.Max(across, row ? child.Want.H : child.Want.W);
        }
        return row ? (along, across) : (across, along);
    }

    /// <summary>Ставит узел в rect (с округлением до пикселя) и его детей на места.</summary>
    public static void Place(Node node, Rect rect, Dictionary<string, float> scrolls, Dictionary<string, float>? rooms = null)
    {
        node.Rect = new Rect(Gui.Round(rect.X), Gui.Round(rect.Y), Gui.Round(rect.W), Gui.Round(rect.H));
        var children = node.Children;
        if (children.Count == 0) return;
        var style = node.Style;
        var row = style.Direction == Direction.Row;
        var pad = style.Padding;
        var inner = new Rect(node.Rect.X + pad.Left, node.Rect.Y + pad.Top, node.Rect.W - pad.Left - pad.Right, node.Rect.H - pad.Top - pad.Bottom);
        if (style.Scroll && style.Id is { } id)
        {
            var room = MathF.Max(node.Content - node.Rect.H, 0);
            var wanted = scrolls.TryGetValue(id, out var kept) ? kept : style.ScrollEnd ? room : 0;
            // Держится низа: если в прошлом кадре бегунок был внизу, а содержимое выросло, ездит за ним.
            if (style.ScrollEnd && rooms is not null && rooms.TryGetValue(id, out var before) && wanted >= before - 1) wanted = room;
            var offset = Math.Clamp(wanted, 0, room);
            scrolls[id] = offset;
            if (rooms is not null) rooms[id] = room;
            inner = inner with { Y = inner.Y - offset, H = node.Content - pad.Top - pad.Bottom };
        }
        if (style.Direction == Direction.Stack)
        {
            static float At(Align align, float room, float want) => align switch
            {
                Align.Center => (room - want) / 2,
                Align.End => room - want,
                _ => 0,
            };
            foreach (var child in children)
            {
                var (horizontal, vertical) = child.Style.Self ?? (Align.Start, Align.Start);
                var w = child.Style.Grow > 0 ? inner.W : child.Want.W;
                var h = child.Style.Grow > 0 ? inner.H : child.Want.H;
                Place(child, new Rect(inner.X + At(horizontal, inner.W, w), inner.Y + At(vertical, inner.H, h), w, h), scrolls, rooms);
            }
            return;
        }
        var gap = style.Gap;
        if (node.WrapLines is { } lines)
        {
            var y = inner.Y;
            foreach (var line in lines)
            {
                var height = 0f;
                foreach (var i in line) height = MathF.Max(height, children[i].Want.H);
                var x = inner.X;
                foreach (var i in line)
                {
                    var child = children[i];
                    var w = child.Want.W;
                    var cross = style.Align switch
                    {
                        Align.Center => (height - child.Want.H) / 2,
                        Align.End => height - child.Want.H,
                        _ => 0,
                    };
                    var h = style.Align == Align.Stretch ? height : child.Want.H;
                    Place(child, new Rect(x, y + cross, w, h), scrolls, rooms);
                    x += w + gap;
                }
                y += height + gap;
            }
            return;
        }
        var roomAlong = row ? inner.W : inner.H;
        var count = children.Count;
        float sum = 0, grows = 0;
        foreach (var child in children)
        {
            sum += row ? child.Want.W : child.Want.H;
            grows += child.Style.Grow;
        }
        var slack = roomAlong - (sum + gap * Math.Max(count - 1, 0));
        var free = MathF.Max(slack, 0);
        float offsetAlong = 0, spacing = gap;
        if (grows == 0)
        {
            switch (style.Justify)
            {
                case Align.Center: offsetAlong = free / 2; break;
                case Align.End: offsetAlong = slack; break;
                case Align.Between when count > 1: spacing = gap + free / (count - 1); break;
            }
        }
        var along = (row ? inner.X : inner.Y) + offsetAlong;
        foreach (var child in children)
        {
            var extra = grows > 0 ? free * child.Style.Grow / grows : 0;
            var size = (row ? child.Want.W : child.Want.H) + extra;
            var crossRoom = row ? inner.H : inner.W;
            var crossWant = style.Align == Align.Stretch ? crossRoom : row ? child.Want.H : child.Want.W;
            var crossOffset = style.Align switch
            {
                Align.Center => (crossRoom - crossWant) / 2,
                Align.End => crossRoom - crossWant,
                _ => 0,
            };
            var cross = (row ? inner.Y : inner.X) + crossOffset;
            Place(child, row ? new Rect(along, cross, size, crossWant) : new Rect(cross, along, crossWant, size), scrolls, rooms);
            along += size + spacing;
        }
    }

    /// <summary>Место корневой панели по привязке.</summary>
    public static Rect Anchored(Node node, float width, float height)
    {
        var anchor = node.Style.Anchor ?? default;
        var (wantW, wantH) = node.Want;
        var w = anchor is { Left: { } l, Right: { } r } ? width - l - r : wantW;
        var h = anchor is { Top: { } t, Bottom: { } b } ? height - t - b : wantH;
        var x = anchor.Left ?? (anchor.Right is { } right ? width - right - w : anchor.CenterX is { } shiftX ? (width - w) / 2 + shiftX : 0);
        var y = anchor.Top ?? (anchor.Bottom is { } bottom ? height - bottom - h : anchor.CenterY is { } shiftY ? (height - h) / 2 + shiftY : 0);
        return new Rect(x, y, w, h);
    }

    private static readonly (float, float, float, float) White = (Batch.WhiteU, Batch.WhiteV, Batch.WhiteU, Batch.WhiteV);
    private static readonly Uv CoinUv = Skins.UvOf(Skins.Coin, 0, 8, 8);
    private static readonly Uv Whole = new(0, 0, 1, 1);

    /// <summary>Рамка скина девятью частями: углы по Border, полосы тянутся, середина разливается.</summary>
    private static void PaintSkin(Batch batch, Skin skin, Rect rect, float alpha, Clip? clip)
    {
        const float corner = Skins.Corner, border = Skins.Border;
        var source = (int)skin * 12f;
        ReadOnlySpan<float> cuts = [0, corner, 12 - corner];
        ReadOnlySpan<float> sizes = [corner, 12 - corner * 2, corner];
        var color = Color.White.Faded(alpha).Rgba;
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
            {
                var w = column == 1 ? MathF.Max(rect.W - border * 2, 0) : MathF.Min(border, rect.W / 2);
                var h = row == 1 ? MathF.Max(rect.H - border * 2, 0) : MathF.Min(border, rect.H / 2);
                var x = column switch { 0 => rect.X, 1 => rect.X + border, _ => rect.X + rect.W - w };
                var y = row switch { 0 => rect.Y, 1 => rect.Y + border, _ => rect.Y + rect.H - h };
                if (w > 0 && h > 0)
                    batch.QuadClipped(x, y, x + w, y + h, Skins.UvOf(source + cuts[column], cuts[row], sizes[column], sizes[row]), color, Paint.Skin, clip);
            }
    }

    /// <summary>Рисует узел и детей: заливка, скин, обводка, текст с тенью, картинка; обрезка — по узлам с Clip.</summary>
    public static void Draw(Fonts fonts, Batch batch, Node node, Clip? clip, float alpha)
    {
        var rect = node.Rect;
        if (rect.W <= 0 || rect.H <= 0) return;
        var style = node.Style;
        alpha *= MathF.Max(style.Alpha ?? 1, 0);
        float x0 = rect.X, y0 = rect.Y, x1 = rect.X + rect.W, y1 = rect.Y + rect.H;
        if (style.Background is { } background) batch.QuadClipped(x0, y0, x1, y1, White, background.Faded(alpha).Rgba, Paint.Font, clip);
        if (style.Skin is { } skin) PaintSkin(batch, skin, rect, alpha, clip);
        if (style.Outline is { } outline)
        {
            var color = outline.Faded(alpha).Rgba;
            const float o = OutlineWidth;
            batch.QuadClipped(x0, y0, x1, y0 + o, White, color, Paint.Font, clip);
            batch.QuadClipped(x0, y1 - o, x1, y1, White, color, Paint.Font, clip);
            batch.QuadClipped(x0, y0 + o, x0 + o, y1 - o, White, color, Paint.Font, clip);
            batch.QuadClipped(x1 - o, y0 + o, x1, y1 - o, White, color, Paint.Font, clip);
        }
        switch (node.Kind)
        {
            case LeafKind.Text:
            {
                var text = node.TextStyle;
                var line = Fonts.Line(text.Font);
                for (var index = 0; index < node.Lines.Count; index++)
                {
                    var y = rect.Y + index * line;
                    if (text.Shadow is { } shadow) fonts.Draw(batch, rect.X + text.Lift, y + text.Lift, text.Font, shadow.Faded(alpha), node.Lines[index], clip);
                    fonts.Draw(batch, rect.X, y, text.Font, text.Color.Faded(alpha), node.Lines[index], clip);
                }
                break;
            }
            case LeafKind.Caret:
                batch.QuadClipped(x0, y0 + 2, x0 + 1, y1 - 2, White, Color.Hex(0xeef6ff).Faded(alpha).Rgba, Paint.Font, clip);
                break;
            case LeafKind.Image:
            {
                var white = Color.White.Faded(alpha).Rgba;
                switch (node.Image.Kind)
                {
                    case ImageKind.Sprite:
                        batch.QuadClipped(x0, y0, x1, y1, node.Image.Uv, white, new Paint(PaintKind.Sprite, node.Image.Team), clip);
                        break;
                    case ImageKind.Coin:
                        batch.QuadClipped(x0, y0, x1, y1, CoinUv, white, Paint.Skin, clip);
                        break;
                    case ImageKind.Minimap:
                        batch.QuadClipped(x0, y0, x1, y1, Whole, white, new Paint(PaintKind.Minimap), clip);
                        // Туман поверх: та же картинка обзора, что у карты, без её рамки.
                        batch.QuadClipped(x0, y0, x1, y1, Whole, white, new Paint(PaintKind.Fog), clip);
                        break;
                }
                break;
            }
        }
        var inner = clip;
        if (style.Clips)
        {
            var own = clip is { } outer ? rect.Intersect(new Rect(outer.X, outer.Y, outer.Width, outer.Height)) : rect;
            inner = new Clip(own.X, own.Y, own.W, own.H);
        }
        foreach (var child in node.Children) Draw(fonts, batch, child, inner, alpha);
        if (node.HasBar) PaintBar(batch, node, inner, alpha);
    }

    /// <summary>Русло и бегунок полосы прокрутки.</summary>
    private static void PaintBar(Batch batch, Node node, Clip? clip, float alpha)
    {
        var (track, thumb) = (node.BarTrack, node.BarThumb);
        batch.QuadClipped(track.X, track.Y, track.X + track.W, track.Y + track.H, White, Gui.BarTrackColor.Faded(alpha).Rgba, Paint.Font, clip);
        var color = node.BarState switch { 2 => Gui.BarThumbActive, 1 => Gui.BarThumbHover, _ => Gui.BarThumbColor };
        batch.QuadClipped(thumb.X, thumb.Y, thumb.X + thumb.W, thumb.Y + thumb.H, White, color.Faded(alpha).Rgba, Paint.Font, clip);
    }
}
