using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Урановый контейнер (uraniumCask) — перенос из buildingArt.ts: свинцовый бочонок с крышкой на болтах и зелёной меткой радиации.</summary>
internal static class UraniumCask
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) =>
    {
        var tint = KitB.Accent(KitB.Uranium);
        Slab(g, 0, 1, 16, 15, 2, Iron);
        g.Circle(8, 7, 6, Ink);
        g.Circle(8, 7, 5, Iron[1]);
        g.Circle(7, 6, 4, Iron[2]);
        foreach (var (x, y) in (ReadOnlySpan<(int, int)>)[(4, 7), (12, 7), (8, 3), (8, 11)])
            g.Rect(x, y, 1, 1, Iron[4]);
        var glow = 0.5 + 0.5 * Pulse(t);
        g.Circle(8, 7, 2, Ink);
        g.Circle(8, 7, 1.5, glow > 0.6 ? tint[2] : tint[1]);
        g.Light(8, 7, 3, 0.2 + 0.3 * glow);
    });
}
