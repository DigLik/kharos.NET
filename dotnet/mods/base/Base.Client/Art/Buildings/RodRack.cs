using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Стойка стержней (rodRack) — перенос из buildingArt.ts: шкаф со стержнями, по которым бежит заряд.</summary>
internal static class RodRack
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) =>
    {
        var tint = KitB.Accent(KitB.Rods);
        Slab(g, 0, 1, 16, 15, 2, Iron);
        g.Rect(2, 3, 12, 9, Ink);
        g.Rect(3, 4, 10, 7, Iron[1]);
        for (var i = 0; i < 3; i++)
        {
            var x = 4 + i * 3;
            g.Rect(x, 5, 2, 5, Iron[3]);
            g.Rect(x, 5, 2, 1, Chase(t, i / 3.0) > 0.3 ? tint[2] : tint[1]);
        }
        g.Light(8, 7, 2, 0.2 + 0.3 * Pulse(t));
    });
}
