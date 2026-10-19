using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Станция заявок (requestStation) — перенос из buildingArt.ts: площадка с ящиками разного груза, табло приёма и мачта с антенной.</summary>
internal static class RequestStation
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) =>
    {
        Slab(g, 0, 1, 32, 31, 2, Iron);
        // Приёмная площадка: жёлто-чёрная рамка, внутри — бетон.
        g.Rect(2, 10, 20, 18, Ink);
        g.Rect(3, 11, 18, 16, Iron[2]);
        for (var i = 0; i < 18; i += 4)
        {
            g.Rect(3 + i, 11, 2, 1, Hazard[1]);
            g.Rect(3 + i, 26, 2, 1, Hazard[1]);
        }
        // Ящики разного груза: металл, стройблоки, патроны, кремний.
        foreach (var (x, y, color) in (ReadOnlySpan<(int, int, uint)>)[(5, 14, KitB.Metal), (12, 14, KitB.Blocks), (5, 20, KitB.Ammo), (12, 20, KitB.Silicon)])
        {
            var tint = KitB.Accent(color);
            g.Rect(x, y, 6, 5, Ink);
            g.Rect(x + 1, y + 1, 4, 3, tint[1]);
            g.Rect(x + 1, y + 1, 4, 1, tint[2]);
        }
        // Табло приёма над площадкой: огни бегут слева направо.
        g.Rect(3, 4, 18, 5, Ink);
        g.Rect(4, 5, 16, 3, Iron[1]);
        for (var i = 0; i < 4; i++)
            Bulb(g, 5 + i * 4, 5, Chase(t, i / 4.0));
        // Мачта с антенной справа: огонёк заявки на верхушке мигает.
        Tower(g, 26, 26, 3, 16, Steel);
        g.Line(26, 8, 30, 3, 1, Steel[2]);
        g.Line(26, 8, 22, 3, 1, Steel[2]);
        var level = Pulse(t * 1.5);
        g.Circle(26, 4, 1.6, Ink);
        g.Circle(26, 4, 1, level > 0.5 ? 0x5ff2d0u : 0x1d5a4cu);
        g.Light(26, 4, 2, level * 0.5);
    });
}
