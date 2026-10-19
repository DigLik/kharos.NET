using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Сейф харита (khariteVault) — перенос из buildingArt.ts: бронированный куб с толстой дверью и замком, сквозь щели светится груз.</summary>
internal static class KhariteVault
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) =>
    {
        var tint = KitB.Accent(KitB.Kharite);
        Slab(g, 0, 1, 16, 15, 2, Iron);
        g.Rect(2, 3, 12, 9, Iron[0]);
        g.Rect(3, 4, 10, 7, Iron[2]);
        g.Rect(3, 4, 10, 1, Iron[3]);
        // Щели светятся цветом харита.
        var glow = 0.5 + 0.5 * Pulse(t);
        g.Rect(3, 7, 10, 1, glow > 0.6 ? tint[2] : tint[1]);
        g.Light(8, 7, 3, 0.3 + 0.4 * glow);
        // Замок.
        g.Circle(8, 7, 2, Ink);
        g.Circle(8, 7, 1, Hazard[1]);
    });
}
