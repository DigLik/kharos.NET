using System.Runtime.InteropServices;

namespace Kharos.Gpu;

/// <summary>Вершина батча: позиция, uv, цвет RGBA8.</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct Vertex(float X, float Y, float U, float V, uint Rgba);

/// <summary>
/// Чем красить отрезок батча: шрифтовой атлас (альфа), атлас обликов с палитрой стороны, скин, мини-карта, туман на
/// мини-карте; Glow — белый угол шрифтового атласа, но цвет прибавляется (свечение, blend add у combatPass.ts).
/// </summary>
public enum PaintKind : byte { Font, Sprite, Skin, Minimap, Fog, Glow }

/// <summary>Краска отрезка; Team — сторона для <see cref="PaintKind.Sprite"/>.</summary>
public readonly record struct Paint(PaintKind Kind, int Team = 0)
{
    public static readonly Paint Font = new(PaintKind.Font);
    public static readonly Paint Glow = new(PaintKind.Glow);
    public static readonly Paint Skin = new(PaintKind.Skin);
}

/// <summary>Прямоугольник обрезки в единицах батча.</summary>
public readonly record struct Clip(float X, float Y, float Width, float Height);

/// <summary>Отрезок индексов: с какого начинается, чем красить и как обрезать.</summary>
public readonly record struct Segment(int Start, Paint Paint, Clip? Clip);

/// <summary>
/// Батч треугольников интерфейса и слоя поверх мира — перенос <c>Batch</c> из <c>apps/client/src/ui.rs</c>.
/// Отрезки помнят краску и обрезку; сплошная заливка — белым углом шрифтового атласа.
/// </summary>
public sealed class Batch
{
    /// <summary>Сторона шрифтового атласа R8; левый верхний угол 4 × 4 — белый.</summary>
    public const int AtlasSize = 1024;
    public const float WhiteU = 2f / AtlasSize, WhiteV = 2f / AtlasSize;

    public readonly List<Vertex> Vertices = [];
    public readonly List<uint> Indices = [];
    public readonly List<Segment> Segments = [];
    private Clip? _clip;

    /// <summary>Заливки (прямоугольники, отрезки, круги, пунктир) прибавляют цвет, а не кладут его поверх (<see cref="PaintKind.Glow"/>).</summary>
    public bool Additive { get; set; }

    /// <summary>Чем заливать: обычно белым углом шрифтового атласа, при <see cref="Additive"/> — свечением.</summary>
    private Paint Fill => Additive ? Paint.Glow : Paint.Font;

    public void Clear()
    {
        Vertices.Clear(); Indices.Clear(); Segments.Clear();
        _clip = null;
        Additive = false;
    }

    private void Use(Paint paint)
    {
        if (Segments.Count == 0 || Segments[^1].Paint != paint || Segments[^1].Clip != _clip)
            Segments.Add(new Segment(Indices.Count, paint, _clip));
    }

    public static uint Rgba(byte r, byte g, byte b, byte a = 255) => (uint)(r | g << 8 | b << 16 | a << 24);

    /// <summary>Четырёхугольник по углам по часовой: a, b, c, d; uv — левый верхний и правый нижний.</summary>
    public void Quad(ReadOnlySpan<(float X, float Y)> points, (float U0, float V0, float U1, float V1) uv, uint color, Paint paint)
    {
        Use(paint);
        var start = (uint)Vertices.Count;
        Vertices.Add(new Vertex(points[0].X, points[0].Y, uv.U0, uv.V0, color));
        Vertices.Add(new Vertex(points[1].X, points[1].Y, uv.U1, uv.V0, color));
        Vertices.Add(new Vertex(points[2].X, points[2].Y, uv.U1, uv.V1, color));
        Vertices.Add(new Vertex(points[3].X, points[3].Y, uv.U0, uv.V1, color));
        Indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
    }

    public void Quad(float x0, float y0, float x1, float y1, (float U0, float V0, float U1, float V1) uv, uint color, Paint paint) =>
        Quad([(x0, y0), (x1, y0), (x1, y1), (x0, y1)], uv, color, paint);

    /// <summary>Четырёхугольник с обрезкой.</summary>
    public void QuadClipped(float x0, float y0, float x1, float y1, (float, float, float, float) uv, uint color, Paint paint, Clip? clip)
    {
        _clip = clip;
        Quad(x0, y0, x1, y1, uv, color, paint);
        _clip = null;
    }

    public void Rect(float x0, float y0, float x1, float y1, uint color) =>
        Quad(x0, y0, x1, y1, (WhiteU, WhiteV, WhiteU, WhiteV), color, Fill);

    /// <summary>Отрезок толщиной width.</summary>
    public void Line(float ax, float ay, float bx, float by, float width, uint color)
    {
        float dx = bx - ax, dy = by - ay;
        var length = MathF.Max(MathF.Sqrt(dx * dx + dy * dy), 1e-6f);
        float nx = -dy / length * width / 2, ny = dx / length * width / 2;
        Quad([(ax + nx, ay + ny), (bx + nx, by + ny), (bx - nx, by - ny), (ax - nx, ay - ny)], (WhiteU, WhiteV, WhiteU, WhiteV), color, Fill);
    }

    /// <summary>
    /// Пунктир от (fromX, fromY) к (toX, toY) — как dashes у selectionPass.ts: квадратные точки стороной dot через step,
    /// отсчёт от конца (у цели точка есть всегда). Возвращает, сколько точек.
    /// </summary>
    public int Dashes(float fromX, float fromY, float toX, float toY, float step, float dot, uint color)
    {
        var length = MathF.Sqrt((toX - fromX) * (toX - fromX) + (toY - fromY) * (toY - fromY));
        if (length <= 0 || step <= 0) return 0;
        var count = 0;
        for (var along = 0f; along < length; along += step, count++)
        {
            var x = toX + (fromX - toX) / length * along;
            var y = toY + (fromY - toY) / length * along;
            Rect(x - dot / 2, y - dot / 2, x + dot / 2, y + dot / 2, color);
        }
        return count;
    }

    /// <summary>
    /// Пунктир по ломаной (путь, очередь приказов): отрезки подряд, шаг точек сквозной — точка на изломе не двоится.
    /// Отсчёт — от первой точки. Возвращает, сколько точек.
    /// </summary>
    public int Dashes(ReadOnlySpan<(float X, float Y)> points, float step, float dot, uint color)
    {
        if (points.Length < 2 || step <= 0) return 0;
        var count = 0;
        var carry = 0f;
        for (var i = 1; i < points.Length; i++)
        {
            var (ax, ay) = points[i - 1];
            var (bx, by) = points[i];
            var length = MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            var along = carry;
            for (; along < length; along += step, count++)
            {
                var x = ax + (bx - ax) / length * along;
                var y = ay + (by - ay) / length * along;
                Rect(x - dot / 2, y - dot / 2, x + dot / 2, y + dot / 2, color);
            }
            carry = along - length;
        }
        return count;
    }

    /// <summary>
    /// Граница зоны точками (cursorPass.ts): dashes точек стороной dot по окружности, с угла 0; skip(x, y) — точку не
    /// ставить (лежит внутри соседней зоны). Возвращает, сколько точек.
    /// </summary>
    public int DashedCircle(float cx, float cy, float radius, int dashes, float dot, uint color, Func<float, float, bool>? skip = null)
    {
        var count = 0;
        for (var dash = 0; dash < dashes; dash++)
        {
            var angle = (float)dash / dashes * MathF.Tau;
            float x = cx + MathF.Cos(angle) * radius, y = cy + MathF.Sin(angle) * radius;
            if (skip?.Invoke(x, y) == true) continue;
            Rect(x - dot / 2, y - dot / 2, x + dot / 2, y + dot / 2, color);
            count++;
        }
        return count;
    }

    /// <summary>Выпуклый многоугольник веером.</summary>
    public void Fan(ReadOnlySpan<(float X, float Y)> points, uint color)
    {
        Use(Fill);
        var start = (uint)Vertices.Count;
        foreach (var (x, y) in points) Vertices.Add(new Vertex(x, y, WhiteU, WhiteV, color));
        for (uint i = 1; i + 1 < points.Length; i++) Indices.AddRange([start, start + i, start + i + 1]);
    }

    /// <summary>Окружность: заливка (width = null) или кольцо толщиной width.</summary>
    public void Circle(float cx, float cy, float radius, float? width, uint color, int segments)
    {
        (float, float) Point(int i, float r)
        {
            var angle = (float)i / segments * MathF.Tau;
            return (cx + MathF.Cos(angle) * r, cy + MathF.Sin(angle) * r);
        }
        if (width is not { } w)
        {
            Span<(float, float)> points = segments <= 256 ? stackalloc (float, float)[segments] : new (float, float)[segments];
            for (var i = 0; i < segments; i++) points[i] = Point(i, radius);
            Fan(points, color);
            return;
        }
        float outer = radius + w / 2, inner = MathF.Max(radius - w / 2, 0);
        for (var i = 0; i < segments; i++)
            Quad([Point(i, outer), Point(i + 1, outer), Point(i + 1, inner), Point(i, inner)], (WhiteU, WhiteV, WhiteU, WhiteV), color, Fill);
    }
}