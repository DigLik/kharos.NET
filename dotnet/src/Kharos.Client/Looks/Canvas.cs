using Kharos.Engine.Numerics;

namespace Kharos.Client.Looks;

/// <summary>Огонь кадра: точка холста, радиус лампы в пикселях, яркость 0..1.</summary>
public readonly record struct CanvasLight(double X, double Y, double Size, double Level);

/// <summary>
/// Холст кадра облика — перенос <c>Pixmap</c> (packages/client/src/render/pixmap.ts) и <c>Canvas</c> из atlas.rs: фигуры
/// без сглаживания, пиксель закрашивается, если его центр попал в фигуру; координаты — от начала <see cref="OriginX"/>,
/// <see cref="OriginY"/>. Цвет — 0xRRGGBB, у цвета стороны в старшем байте тон + 1 (<see cref="Toned"/>): альфа такого
/// пикселя 254 − тон, шейдер перекрашивает его палитрой стороны. Арифметика — double в порядке TS, корень длины —
/// <see cref="DetMath.Hypot"/> (как Math.hypot в Bun), так что кадры совпадают с TS до байта.
/// </summary>
public sealed class Canvas(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    /// <summary>Пиксели RGBA, строки сверху вниз.</summary>
    public byte[] Data { get; } = new byte[width * height * 4];
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    /// <summary>Огни кадра — в пикселях холста от начала координат.</summary>
    public List<CanvasLight> Lights { get; } = [];

    /// <summary>Цвет с меткой тона tone (0 — тёмный, 1 — основной, 2 — светлый).</summary>
    public static uint Toned(uint color, int tone) => color + (uint)(tone + 1) * 0x1000000;

    public Canvas Clear()
    {
        Array.Clear(Data);
        Lights.Clear();
        return this;
    }

    private interface IShape
    {
        bool Inside(double x, double y);
    }

    private readonly struct Everywhere : IShape
    {
        public bool Inside(double x, double y) => true;
    }

    private readonly struct Disc(double centerX, double centerY, double radius) : IShape
    {
        public bool Inside(double x, double y) => (x - centerX) * (x - centerX) + (y - centerY) * (y - centerY) <= radius * radius;
    }

    private readonly struct Band(double centerX, double centerY, double radius, double width) : IShape
    {
        public bool Inside(double x, double y) => Math.Abs(DetMath.Hypot(x - centerX, y - centerY) - radius) <= width / 2;
    }

    private readonly struct Segment(double fromX, double fromY, double dx, double dy, double length, double half) : IShape
    {
        public bool Inside(double x, double y)
        {
            var along = ((x - fromX) * dx + (y - fromY) * dy) / length;
            var across = ((x - fromX) * dy - (y - fromY) * dx) / length;
            return along >= 0 && along <= length && Math.Abs(across) <= half;
        }
    }

    private readonly struct Polygon(double[] points) : IShape
    {
        /// <summary>Чётно-нечётное правило.</summary>
        public bool Inside(double x, double y)
        {
            var inside = false;
            var count = points.Length / 2;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                double xi = points[i * 2], yi = points[i * 2 + 1], xj = points[j * 2], yj = points[j * 2 + 1];
                if (yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }
    }

    /// <summary>Закрашивает пиксели в границах, центр которых внутри фигуры; пиксель i занимает [i, i + 1).</summary>
    private Canvas Fill<T>(double left, double top, double right, double bottom, uint color, T shape) where T : IShape
    {
        byte red = (byte)(color >> 16), green = (byte)(color >> 8), blue = (byte)color, alpha = (byte)(255 - (color >> 24));
        var fromX = (int)Math.Max(0, Math.Ceiling(left + OriginX - 0.5));
        var toX = (int)Math.Min(Width - 1, Math.Ceiling(right + OriginX - 0.5) - 1);
        var fromY = (int)Math.Max(0, Math.Ceiling(top + OriginY - 0.5));
        var toY = (int)Math.Min(Height - 1, Math.Ceiling(bottom + OriginY - 0.5) - 1);
        for (var pixelY = fromY; pixelY <= toY; pixelY++)
            for (var pixelX = fromX; pixelX <= toX; pixelX++)
            {
                if (!shape.Inside(pixelX + 0.5 - OriginX, pixelY + 0.5 - OriginY)) continue;
                var offset = (pixelY * Width + pixelX) * 4;
                Data[offset] = red;
                Data[offset + 1] = green;
                Data[offset + 2] = blue;
                Data[offset + 3] = alpha;
            }
        return this;
    }

    /// <summary>Прямоугольник: левый верхний угол (x, y), размер width × height.</summary>
    public Canvas Rect(double x, double y, double width, double height, uint color) =>
        Fill(x, y, x + width, y + height, color, new Everywhere());

    public Canvas Circle(double centerX, double centerY, double radius, uint color) =>
        Fill(centerX - radius, centerY - radius, centerX + radius, centerY + radius, color, new Disc(centerX, centerY, radius));

    /// <summary>Окружность линией толщиной width.</summary>
    public Canvas Ring(double centerX, double centerY, double radius, double width, uint color)
    {
        var outer = radius + width / 2;
        return Fill(centerX - outer, centerY - outer, centerX + outer, centerY + outer, color, new Band(centerX, centerY, radius, width));
    }

    /// <summary>Отрезок толщиной width с прямыми торцами.</summary>
    public Canvas Line(double fromX, double fromY, double toX, double toY, double width, uint color)
    {
        double dx = toX - fromX, dy = toY - fromY;
        var length = DetMath.Hypot(dx, dy);
        if (length == 0 || double.IsNaN(length)) return this;
        var half = width / 2;
        return Fill(Math.Min(fromX, toX) - half, Math.Min(fromY, toY) - half, Math.Max(fromX, toX) + half, Math.Max(fromY, toY) + half, color,
            new Segment(fromX, fromY, dx, dy, length, half));
    }

    /// <summary>Многоугольник: x1, y1, x2, y2, …; чётно-нечётное правило.</summary>
    public Canvas Poly(ReadOnlySpan<double> coords, uint color)
    {
        var count = coords.Length / 2;
        if (count < 3) return this;
        var points = coords[..(count * 2)].ToArray();
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        for (var i = 0; i < count; i++)
        {
            left = Math.Min(left, points[i * 2]);
            right = Math.Max(right, points[i * 2]);
            top = Math.Min(top, points[i * 2 + 1]);
            bottom = Math.Max(bottom, points[i * 2 + 1]);
        }
        return Fill(left, top, right, bottom, color, new Polygon(points));
    }

    /// <summary>Один пиксель чертежа, в котором лежит (x, y).</summary>
    public Canvas Pixel(double x, double y, uint color) =>
        Fill(Math.Floor(x), Math.Floor(y), Math.Floor(x) + 1, Math.Floor(y) + 1, color, new Everywhere());

    /// <summary>Сдвиг начала координат: так на портрете рисуют турели на их местах.</summary>
    public Canvas Translate(double dx, double dy)
    {
        OriginX += dx;
        OriginY += dy;
        return this;
    }

    /// <summary>Огонь кадра в точке (x, y): size — радиус лампы в пикселях, level — яркость 0..1.</summary>
    public Canvas Light(double x, double y, double size, double level)
    {
        Lights.Add(new CanvasLight(x, y, size, level));
        return this;
    }
}
