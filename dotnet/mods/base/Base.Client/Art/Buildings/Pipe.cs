using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>
/// Наземная труба (pipe) — перенос pipeArt из buildingArt.ts: рукава к соседям по сторонам, вариант — маска соседей как у
/// стены (1 север, 2 восток, 4 юг, 8 запад), 16 вариантов. Одиночная — короткий обрубок с фланцами.
/// </summary>
internal static class Pipe
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, _, _, connections) => Draw(g, connections), variants: 16);

    private static void Draw(Canvas g, int connections)
    {
        bool north = (connections & Wall.North) != 0, east = (connections & Wall.East) != 0, south = (connections & Wall.South) != 0, west = (connections & Wall.West) != 0;
        var lone = connections == 0;
        if (east || west || lone)
        {
            var left = west ? 0 : 4;
            var right = east ? 16 : 12;
            g.Rect(left, 5, right - left, 6, Ink);
            g.Rect(left, 6, right - left, 4, Iron[2]);
            g.Rect(left, 6, right - left, 1, Iron[4]);
            g.Rect(left, 9, right - left, 1, Iron[0]);
        }
        if (north || south)
        {
            var top = north ? 0 : 4;
            var bottom = south ? 16 : 12;
            g.Rect(5, top, 6, bottom - top, Ink);
            g.Rect(6, top, 4, bottom - top, Iron[2]);
            g.Rect(6, top, 1, bottom - top, Iron[4]);
            g.Rect(9, top, 1, bottom - top, Iron[0]);
        }
        // Фланец на стыке: у поворотов и развилок он закрывает наложение рукавов.
        if ((north || south) && (east || west))
        {
            g.Rect(4, 4, 8, 8, Ink);
            g.Rect(5, 5, 6, 6, Iron[1]);
            g.Rect(5, 5, 6, 1, Iron[3]);
            g.Rect(7, 7, 2, 2, Rust[1]);
        }
    }
}
