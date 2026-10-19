using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>
/// Колодец подземной трубы (well) — перенос wellArt из buildingArt.ts: люк в бетонном кольце, по нему труба уходит под
/// землю. Вариант — маска соседей как у стены (1 север, 2 восток, 4 юг, 8 запад), 16 вариантов.
/// </summary>
internal static class Well
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, connections) => Draw(g, t, connections), variants: 16);

    private static void Draw(Canvas g, double t, int connections)
    {
        var sides = new List<(int X, int Y, int W, int H)>();
        if ((connections & Wall.North) != 0) sides.Add((5, 0, 6, 4));
        if ((connections & Wall.South) != 0) sides.Add((5, 12, 6, 4));
        if ((connections & Wall.West) != 0) sides.Add((0, 5, 4, 6));
        if ((connections & Wall.East) != 0) sides.Add((12, 5, 4, 6));
        foreach (var (x, y, w, h) in sides)
        {
            g.Rect(x, y, w, h, Ink);
            g.Rect(x + 1, y + 1, Math.Max(1, w - 2), Math.Max(1, h - 2), Iron[2]);
        }
        g.Circle(8, 8, 6, Ink);
        g.Circle(8, 8, 5, Steel[2]);
        g.Ring(8, 8, 4, 1, Steel[0]);
        g.Circle(8, 8, 3, Iron[1]);
        g.Rect(6, 7, 4, 1, Iron[3]);
        g.Rect(6, 9, 4, 1, Iron[3]);
        Bulb(g, 12, 3, Pulse(t));
    }
}
