using Kharos.Client.Looks;

namespace Kharos.Client.Tests.Looks;

/// <summary>Перенос packages/tests/pixmap.test.ts: примитивы холста (Pixmap клиента TS — Canvas в C#), заголовки дословно.</summary>
public sealed class PixmapTests
{
    /// <summary>Рисунок символами: # — закрашенный пиксель.</summary>
    private static string[] Picture(Canvas image)
    {
        var rows = new string[image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            var row = new char[image.Width];
            for (var x = 0; x < image.Width; x++)
                row[x] = image.Data[(y * image.Width + x) * 4 + 3] != 0 ? '#' : '.';
            rows[y] = new string(row);
        }
        return rows;
    }

    [Fact(DisplayName = "rect закрашивает ровно свои пиксели и пишет цвет")]
    public void RectPaintsExactlyItsPixelsAndWritesColor()
    {
        var image = new Canvas(4, 3).Rect(1, 1, 2, 1, 0x102030);
        Assert.Equal(["....", ".##.", "...."], Picture(image));
        Assert.Equal([0x10, 0x20, 0x30, 255], image.Data.AsSpan(20, 4).ToArray());
    }

    [Fact(DisplayName = "фигуры обрезаются по краям холста и сдвигаются началом координат")]
    public void ShapesAreClippedByCanvasEdgesAndShiftedByOrigin()
    {
        var image = new Canvas(3, 3) { OriginX = 1, OriginY = 1 };
        image.Rect(-5, 0, 6, 1, 0xffffff);
        Assert.Equal(["...", "##.", "..."], Picture(image));
    }

    [Fact(DisplayName = "circle закрашивает пиксели, центр которых внутри круга")]
    public void CirclePaintsPixelsWhoseCenterIsInsideTheCircle()
    {
        var image = new Canvas(6, 6).Circle(3, 3, 2, 0xffffff);
        Assert.Equal(["......", "..##..", ".####.", ".####.", "..##..", "......"], Picture(image));
    }

    [Fact(DisplayName = "ring оставляет середину пустой")]
    public void RingLeavesTheMiddleEmpty()
    {
        var image = new Canvas(8, 8).Ring(4, 4, 3, 1, 0xffffff);
        Assert.Equal(".#....#.", Picture(image)[4]);
    }

    [Fact(DisplayName = "line: горизонтальный отрезок заданной толщины")]
    public void LineHorizontalSegmentOfGivenThickness()
    {
        var image = new Canvas(6, 4).Line(1, 2, 5, 2, 2, 0xffffff);
        Assert.Equal(["......", ".####.", ".####.", "......"], Picture(image));
    }
}
