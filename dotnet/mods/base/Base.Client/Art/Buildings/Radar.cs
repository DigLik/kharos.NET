using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Радар (radar, 2×2) — перенос radar из buildingArt.ts.</summary>
internal static class Radar
{
    private const double Turn = Math.PI * 2;

    /// <summary>Мачта с вращающейся тарелкой и аппаратная будка.</summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(2, 2, (g, t, _) =>
    {
        Slab(g, 0, 2, 32, 30, 3, Steel);
        g.Rect(3, 5, 26, 21, Steel[0]);
        g.Rect(4, 6, 25, 20, Steel[1]);
        Bulb(g, 4, 6, Chase(t, 0));
        Bulb(g, 26, 23, Chase(t, 0.5));

        Slab(g, 3, 15, 11, 12, 4, Iron);
        Windows(g, [5, 9], 22);
        Lamp(g, 8, 18, 1, Pulse(t * 2));

        Tower(g, 20, 17, 6, 7, Iron);
        // Тарелка видна под наклоном: по вертикали всё сжато.
        var angle = t * Turn;
        var (dx, dy) = (Math.Cos(angle), Math.Sin(angle) * 0.65);
        var (px, py) = (-Math.Sin(angle) * 9, Math.Cos(angle) * 9 * 0.65);
        const double cx = 20, cy = 8;
        g.Line(cx - px, cy - py, cx + px, cy + py, 5, Ink);
        g.Line(cx - px, cy - py, cx + px, cy + py, 3, Iron[3]);
        g.Line(cx - px + dx, cy - py + dy, cx + px + dx, cy + py + dy, 1.2, Iron[4]);
        g.Line(cx, cy, cx + dx * 5, cy + dy * 5, 1.2, Iron[1]);
        g.Rect(Round(cx + dx * 5) - 1, Round(cy + dy * 5) - 1, 2, 2, Team[3]);
    });
}
