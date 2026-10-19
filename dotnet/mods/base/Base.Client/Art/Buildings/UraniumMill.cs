using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Урановая фабрика (uraniumMill, 2×2) — перенос uraniumMill из buildingArt.ts.</summary>
internal static class UraniumMill
{
    private const double Turn = Math.PI * 2;

    /// <summary>
    /// Бак выщелачивания с мешалкой и зелёным раствором, рядом линия бочек с жёлтым кеком; полосы опасности по краю
    /// и лампа радиации.
    /// </summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(2, 2, (g, t, _, working) =>
    {
        var tint = Accent(GoodColor.Uranium);
        Slab(g, 0, 2, 32, 30, 3, Iron);
        for (var x = 2; x < 30; x += 4)
        {
            g.Rect(x, 4, 2, 2, Hazard[1]);
            g.Rect(x + 2, 4, 2, 2, Hazard[0]);
        }
        // Бак: раствор светится, мешалка крутится в работе.
        const double cx = 12, cy = 16;
        g.Circle(cx, cy, 9, Ink);
        g.Circle(cx, cy, 8, Iron[3]);
        g.Circle(cx, cy, 6, Ink);
        g.Circle(cx, cy, 5, working ? tint[1] : tint[0]);
        var angle = Turn * (working ? t : 0.1);
        g.Line(cx - Math.Cos(angle) * 4, cy - Math.Sin(angle) * 4, cx + Math.Cos(angle) * 4, cy + Math.Sin(angle) * 4, 1, Iron[4]);
        g.Circle(cx, cy, 1.5, Iron[4]);
        g.Light(cx, cy, 5, working ? 0.4 + 0.3 * Pulse(t * 2) : 0.1);
        // Бочки с кеком: в работе по одной загорается крышка — бочка наполнена.
        for (var i = 0; i < 3; i++)
        {
            var y = 9 + i * 6;
            g.Rect(23, y, 6, 5, Ink);
            g.Rect(24, y + 1, 4, 3, Hazard[1]);
            g.Rect(24, y + 1, 4, 1, working && Chase(t, i / 3.0) > 0.3 ? tint[2] : 0xf0d860);
        }
        Lamp(g, 27, 27, 2, Pulse(t * (working ? 3 : 1)));
    }, 1, true);
}
