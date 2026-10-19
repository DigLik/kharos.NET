using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>
/// Ворота (gate) — перенос из buildingArt.ts: плита, выезжающая из земли. Вариант — маска соседей как у стены (младшие
/// 4 бита: 1 север, 2 восток, 4 юг, 8 запад) плюс бит 16 (<see cref="OpenVariant"/>) — плита опущена; 32 варианта.
/// Поднятая — секция стены в предупредительных полосах, опущенная — люк вровень с землёй.
/// </summary>
internal static class Gate
{
    public const int OpenVariant = 16;

    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, variant) => Draw(g, t, variant), variants: 32);

    private static void Draw(Canvas g, double t, int variant)
    {
        bool north = (variant & Wall.North) != 0, east = (variant & Wall.East) != 0, south = (variant & Wall.South) != 0, west = (variant & Wall.West) != 0;
        // Одиночная секция и секция в ряду слева направо лежат поперёк; в ряду сверху вниз — вдоль.
        var upright = (north || south) && !east && !west;
        var left = west ? 0 : 2;
        var right = east ? 16 : 14;
        var top = north ? 0 : 2;
        var bottom = south ? 16 : 14;
        if ((variant & OpenVariant) != 0)
        {
            // Люк: рама до края тайла со стороны соседа, внутри — тёмная крышка со щелью, куда ушла плита.
            var (x, y, width, height) = upright ? (3, top, 10, bottom - top) : (left, 3, right - left, 10);
            g.Rect(x, y, width, height, Ink);
            g.Rect(
                x + (upright ? 1 : west ? 0 : 1),
                y + (upright ? (north ? 0 : 1) : 1),
                width - (upright ? 2 : (west ? 0 : 1) + (east ? 0 : 1)),
                height - (upright ? (north ? 0 : 1) + (south ? 0 : 1) : 2),
                Steel[0]);
            if (upright)
            {
                g.Rect(7, top, 2, bottom - top, Ink);
                for (var mark = top + 2; mark + 1 < bottom; mark += 4)
                {
                    g.Rect(4, mark, 2, 1, Hazard[1]);
                    g.Rect(10, mark, 2, 1, Hazard[1]);
                }
            }
            else
            {
                g.Rect(left, 7, right - left, 2, Ink);
                for (var mark = left + 2; mark + 1 < right; mark += 4)
                {
                    g.Rect(mark, 4, 1, 2, Hazard[1]);
                    g.Rect(mark, 10, 1, 2, Hazard[1]);
                }
            }
            g.Rect(7, 7, 2, 2, 0x5fe08a);
            g.Light(8, 8, 1.5, 0.3);
            return;
        }
        // Поднятая плита: как секция стены, но сплошная и в полосах.
        {
            var (x, y, width, height) = upright ? (4, top, 9, bottom - top) : (left, 4, right - left, 9);
            g.Rect(x, y, width, height, Ink);
            if (upright)
            {
                g.Rect(5, top, 6, bottom - top, Iron[1]);
                g.Rect(5, top, 1, bottom - top, Iron[3]);
                g.Rect(11, top, 1, bottom - top, Iron[0]);
                for (var mark = top + 1; mark + 2 < bottom; mark += 4)
                    g.Rect(6, mark, 4, 2, Hazard[1]);
            }
            else
            {
                g.Rect(left, 5, right - left, 6, Iron[1]);
                g.Rect(left, 5, right - left, 1, Iron[3]);
                g.Rect(left, 11, right - left, 1, Iron[0]);
                for (var mark = left + 1; mark + 2 < right; mark += 4)
                    g.Rect(mark, 7, 2, 3, Hazard[1]);
            }
        }
        g.Rect(7, 7, 2, 2, KitB.Fire[2]);
        g.Light(8, 8, 1.5, 0.45 + 0.3 * Pulse(t));
    }
}
