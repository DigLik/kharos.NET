using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Генератор щита (shieldGenerator) — перенос из buildingArt.ts: восьмигранная плита, четыре излучателя с бегущими огнями и дышащий шар.</summary>
internal static class ShieldGenerator
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) =>
    {
        Slab(g, 0, 2, 32, 30, 3, Steel);
        // Основание: восьмигранная плита с кольцевой канавкой.
        g.Circle(16, 17, 12, Ink);
        g.Circle(16, 17, 11, Iron[1]);
        g.Circle(16, 17, 9, Iron[0]);
        g.Circle(16, 17, 8, Iron[2]);
        // Четыре ребра-излучателя по углам; огни бегут к шару.
        var i = 0;
        foreach (var (cx, cy) in (ReadOnlySpan<(int, int)>)[(6, 7), (26, 7), (6, 27), (26, 27)])
        {
            g.Line(cx, cy, 16, 15, 3, Ink);
            g.Line(cx, cy, 16, 15, 1, Steel[3]);
            Tower(g, cx, cy, 2, 3, Steel);
            var k = Chase(t * 2, i / 4.0);
            var along = (t * 2 + i / 4.0) % 1;
            g.Rect(KitB.Round(cx + (16 - cx) * along) - 1, KitB.Round(cy - 3 + (15 - cy + 3) * along) - 1, 2, 2, TeamColor(0.4 + 0.6 * k));
            i++;
        }
        // Шар: стекло с бликом и свет, который дышит.
        var glow = 0.55 + 0.45 * Pulse(t * 2);
        g.Circle(16, 13, 7, Ink);
        g.Circle(16, 13, 6, Team[0]);
        g.Circle(16, 13, 5, TeamColor(0.3 + 0.4 * glow));
        g.Circle(15, 12, 3, TeamColor(0.5 + 0.5 * glow));
        g.Rect(13, 10, 2, 1, Team[3]);
        g.Light(16, 13, 6, glow);
    });
}
