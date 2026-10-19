using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Обогатитель харита (enricher, 2×2) — перенос enricher из buildingArt.ts.</summary>
internal static class Enricher
{
    private const double Turn = Math.PI * 2;

    /// <summary>Круглая центрифуга во всю площадку за полосами опасности, в центре — светящееся ядро.</summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(2, 2, (g, t, _, working) =>
    {
        var tint = Accent(GoodColor.Kharite);
        Slab(g, 0, 2, 32, 30, 3, Iron);
        for (var x = 2; x < 30; x += 4)
        {
            g.Rect(x, 4, 2, 2, Hazard[1]);
            g.Rect(x + 2, 4, 2, 2, Hazard[0]);
        }

        // Центрифуга: в работе капсулы бегут по кольцу, в простое стоят.
        const double cx = 16, cy = 16;
        g.Circle(cx, cy, 11, Ink);
        g.Circle(cx, cy, 10, Iron[2]);
        g.Circle(cx, cy, 7, Ink);
        g.Circle(cx, cy, 6, Iron[0]);
        for (var i = 0; i < 4; i++)
        {
            var angle = Turn * ((working ? t : 0.125) + i / 4.0);
            var x = cx + Math.Cos(angle) * 8.5;
            var y = cy + Math.Sin(angle) * 8.5;
            g.Line(cx, cy, x, y, 1, Iron[3]);
            g.Circle(x, y, 2, working ? tint[1] : tint[0]);
            g.Rect(Round(x) - 1, Round(y) - 1, 1, 1, tint[2]);
        }
        // Ядро: в работе дышит светом харита, в простое едва теплится.
        var core = working ? 0.6 + 0.4 * Pulse(t * 2) : 0.15;
        g.Circle(cx, cy, 4, tint[0]);
        g.Circle(cx, cy, 3, core > 0.85 ? tint[2] : working ? tint[1] : tint[0]);
        g.Light(cx, cy, 5, core);
        Lamp(g, 28, 26, 2, Pulse(t * (working ? 3 : 1)));
    }, 1, true);
}
