using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Шкаф компонентов (partsLocker) — перенос из buildingArt.ts: закрытая стойка с ящичками, на каждом огонёк учёта.</summary>
internal static class PartsLocker
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) =>
    {
        var tint = KitB.Accent(KitB.Parts);
        Slab(g, 0, 1, 16, 15, 2, Steel);
        g.Rect(2, 3, 12, 9, Ink);
        // Ящички в два столбца, огоньки бегут сверху вниз.
        for (var row = 0; row < 3; row++)
        {
            foreach (var x in (ReadOnlySpan<int>)[3, 9])
            {
                g.Rect(x, 4 + row * 3, 4, 2, Steel[2]);
                g.Rect(x + 1, 4 + row * 3, 2, 1, Steel[3]);
            }
            var level = Chase(t, row / 3.0);
            g.Rect(7, 4 + row * 3, 2, 2, level > 0.3 ? tint[2] : tint[0]);
        }
        g.Light(8, 7, 2, 0.3 + 0.3 * Pulse(t));
    });
}
