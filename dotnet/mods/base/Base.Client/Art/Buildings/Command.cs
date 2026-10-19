using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Главное здание (command, 3×3) — перенос command из buildingArt.ts.</summary>
internal static class Command
{
    /// <summary>Двухъярусная цитадель с куполом-маяком, угловыми башенками и мачтой связи.</summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(3, 3, (g, t, _, _) =>
    {
        Slab(g, 0, 2, 48, 46, 3, Steel);
        g.Rect(18, 36, 12, 1, Rust[1]);
        g.Rect(18, 40, 12, 1, Rust[1]);
        g.Rect(18, 36, 1, 5, Rust[1]);
        g.Rect(29, 36, 1, 5, Rust[1]);
        foreach (var (x, offset) in (ReadOnlySpan<(double, double)>)[(5, 0.25), (43, 0.75)])
        {
            Tower(g, x, 40, 3, 3, Iron);
            Bulb(g, x - 1, 36, Pulse(t, offset));
        }

        Slab(g, 6, -2, 36, 36, 8, Iron);
        g.Rect(20, 25, 8, 8, Ink);
        g.Rect(21, 26, 6, 7, Dark);
        Windows(g, [9, 14, 31, 36], 27);
        for (var i = 0; i < 4; i++)
            Bulb(g, 20 + i * 2, 22, Chase(t, i / 4.0));

        Slab(g, 12, -8, 24, 24, 5, Steel);
        Windows(g, [14, 19, 26, 31], 11);
        // Платформа пушки: на ней стоит турель главного здания (см. mounts в sim/buildings.ts).
        g.Circle(24, 6, 9.5, Ink);
        g.Circle(24, 6, 8.5, Iron[1]);
        g.Circle(24, 6, 7, Iron[0]);
        foreach (var (x, y) in (ReadOnlySpan<(double, double)>)[(17, 6), (31, 6), (24, -1), (24, 13)])
            g.Rect(x - 0.5, y - 0.5, 1, 1, Iron[2]);
        Lamp(g, 14, -5, 2, Pulse(t));

        Tower(g, 38, 20, 2, 7, Iron);
        Bulb(g, 37, 12, Chase(t * 2, 0));
    }, 1, false, g =>
    {
        // Портрет несёт башню главного здания (mounts в data/buildings.json: core, along 0, across −1,125) под углом портрета.
        const double angle = -Math.PI / 4, centre = 3 * Tile / 2.0, across = -1.125;
        var (sin, cos) = ArtTrig.Of(angle);
        double x = (cos * 0 - sin * across) * 16, y = (sin * 0 + cos * across) * 16;
        g.Translate(centre, centre);
        g.Translate(x, y);
        // Портрет здания красит башню цветами своей стороны, а не маской тонов (TEAMS.own в portraits.ts).
        UnitArt.Turret("core")(g, angle, [0x1f63d8, 0x5aa9ff, 0xe4f4ff], 0);
        g.Translate(-x, -y);
        g.Translate(-centre, -centre);
    });
}
