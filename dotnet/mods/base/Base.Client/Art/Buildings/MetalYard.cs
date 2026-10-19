using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Склад металла (metalYard) — перенос из buildingArt.ts: открытый двор со штабелями слитков и козловым краном над ними.</summary>
internal static class MetalYard
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) =>
    {
        var tint = KitB.Accent(KitB.Metal);
        Slab(g, 0, 1, 48, 31, 3, Iron);
        g.Rect(2, 3, 44, 24, Iron[1]);
        for (var x = 3; x < 46; x += 8)
            g.Rect(x, 26, 4, 1, Hazard[1]);
        // Штабеля: каждый — три ряда слитков.
        foreach (var sx in (ReadOnlySpan<int>)[4, 19, 34])
            for (var row = 0; row < 3; row++)
                for (var k = 0; k < 2; k++)
                {
                    var x = sx + k * 5;
                    var y = 6 + row * 6;
                    g.Rect(x, y, 5, 5, Ink);
                    g.Rect(x + 1, y + 1, 3, 3, tint[1]);
                    g.Rect(x + 1, y + 1, 3, 1, tint[2]);
                }
        // Козловой кран ходит над штабелями.
        var crane = KitB.Round(6 + 34 * (0.5 - 0.5 * Math.Cos(KitB.Turn * t)));
        g.Rect(crane, 2, 3, 25, Ink);
        g.Rect(crane + 1, 2, 1, 25, Hazard[1]);
        g.Rect(crane - 1, 12, 5, 3, Iron[3]);
        Bulb(g, 2, 28, Pulse(t));
        Bulb(g, 44, 28, Pulse(t, 0.5));
    });
}
