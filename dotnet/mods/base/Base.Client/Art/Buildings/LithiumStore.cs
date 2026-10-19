using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Склад лития (lithiumStore) — перенос из buildingArt.ts: стеллаж брикетов, площадка и лампа учёта.</summary>
internal static class LithiumStore
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) =>
    {
        var tint = KitB.Accent(KitB.Lithium);
        Slab(g, 0, 1, 32, 15, 2, Iron);
        g.Rect(2, 3, 28, 9, Iron[0]);
        // Стеллаж: ярусы брикетов.
        for (var x = 3; x < 22; x += 4)
            foreach (var y in (ReadOnlySpan<int>)[4, 7])
            {
                g.Rect(x, y, 3, 2, tint[1]);
                g.Rect(x, y, 3, 1, tint[2]);
            }
        // Площадка и лампа учёта.
        g.Rect(23, 4, 6, 7, Iron[2]);
        g.Rect(23, 4, 6, 1, Iron[3]);
        Bulb(g, 25, 6, 0.4 + 0.6 * Pulse(t));
    });
}
