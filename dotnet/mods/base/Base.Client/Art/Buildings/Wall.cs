using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>
/// Стена (wall) — перенос из buildingArt.ts: центральная опора и рукава до соседних секций. Вариант — маска соседей
/// (WALL_CONNECTION: 1 север, 2 восток, 4 юг, 8 запад), 16 вариантов от одиночной секции до перекрёстка.
/// </summary>
internal static class Wall
{
    public const int North = 1, East = 2, South = 4, West = 8;

    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, _, _, connections) => Draw(g, connections), variants: 16);

    /// <summary>Секция стены с рукавами по маске соседей.</summary>
    public static void Draw(Canvas g, int connections)
    {
        bool north = (connections & North) != 0, east = (connections & East) != 0, south = (connections & South) != 0, west = (connections & West) != 0;

        // Рукава доходят до края тайла только со стороны соседа: две секции встречаются без прозрачного шва.
        if (east || west)
        {
            var left = west ? 0 : 3;
            var right = east ? 16 : 13;
            g.Rect(left, 4, right - left, 9, Ink);
            g.Rect(left, 5, right - left, 6, Steel[1]);
            g.Rect(left, 5, right - left, 1, Steel[3]);
            g.Rect(left, 11, right - left, 1, Steel[0]);
        }
        if (north || south)
        {
            var top = north ? 0 : 3;
            var bottom = south ? 16 : 13;
            g.Rect(4, top, 9, bottom - top, Ink);
            g.Rect(5, top, 6, bottom - top, Steel[1]);
            g.Rect(5, top, 1, bottom - top, Steel[3]);
            g.Rect(11, top, 1, bottom - top, Steel[0]);
        }

        // Центральная опора скрывает наложение рукавов и остаётся у одиночной секции.
        g.Rect(3, 3, 10, 10, Ink);
        g.Rect(4, 4, 8, 8, Iron[1]);
        g.Rect(4, 4, 8, 1, Iron[3]);
        g.Rect(4, 11, 8, 1, Iron[0]);
        g.Rect(4, 5, 1, 6, Iron[2]);
        g.Rect(11, 5, 1, 6, Iron[0]);
        foreach (var (x, y) in (ReadOnlySpan<(int, int)>)[(5, 5), (10, 5), (5, 10), (10, 10)])
            g.Rect(x, y, 1, 1, Rust[1]);
    }
}
