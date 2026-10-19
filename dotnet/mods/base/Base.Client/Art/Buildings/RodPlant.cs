using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Завод стержней (rodPlant, 2×2) — перенос rodPlant из buildingArt.ts.</summary>
internal static class RodPlant
{
    /// <summary>
    /// Пресс набивает таблетки урана в трубки, трубки лежат в кассете; зелёный огонёк на прессе в работе мигает с каждым
    /// ходом, лампа радиации — по краю.
    /// </summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(2, 2, (g, t, _, working) =>
    {
        var tint = Accent(GoodColor.Rods);
        Slab(g, 0, 2, 32, 30, 3, Iron);
        for (var x = 2; x < 30; x += 4)
        {
            g.Rect(x, 4, 2, 2, Hazard[1]);
            g.Rect(x + 2, 4, 2, 2, Hazard[0]);
        }
        // Пресс: станина и шток, который в работе ходит вниз-вверх.
        g.Rect(4, 8, 11, 14, Ink);
        g.Rect(5, 9, 9, 12, Iron[2]);
        g.Rect(5, 9, 9, 1, Iron[3]);
        var stroke = working ? Round(Pulse(t * 2) * 4) : 0;
        g.Rect(8, 10, 3, 5 + stroke, Ink);
        g.Rect(9, 10, 1, 5 + stroke, Iron[4]);
        Bulb(g, 11, 18, working ? Pulse(t * 2) : 0.15);
        // Кассета: трубки со стержнями, в работе по одной загорается торец.
        g.Rect(17, 8, 12, 19, Ink);
        g.Rect(18, 9, 10, 17, Iron[1]);
        for (var i = 0; i < 4; i++)
        {
            var x = 19 + i * 2;
            g.Rect(x, 10, 1, 15, Iron[3]);
            g.Rect(x, 10, 1, 1, working && Chase(t, i / 4.0) > 0.3 ? tint[2] : tint[0]);
        }
        g.Rect(4, 24, 11, 3, Iron[1]);
        Lamp(g, 27, 28, 1, Pulse(t));
    }, 1, true);
}
