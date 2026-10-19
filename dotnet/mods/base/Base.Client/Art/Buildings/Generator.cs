using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Электростанция (generator, 2×2) — перенос generator из buildingArt.ts.</summary>
internal static class Generator
{
    private const double Turn = Math.PI * 2;

    /// <summary>
    /// Реактор под куполом с кольцом огней и два бака с подачей по трубам. Под нагрузкой огни бегут по кругу и по трубам
    /// идёт топливо; станция в запасе, которой питать нечего, стоит тёмной и чуть дышит одной лампой.
    /// </summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(2, 2, (g, t, _, working) =>
    {
        Slab(g, 0, 1, 32, 31, 3, Steel);
        g.Rect(3, 4, 26, 22, Steel[0]);
        g.Rect(4, 5, 25, 21, Steel[1]);
        g.Rect(4, 27, 7, 1, Rust[1]);
        g.Rect(21, 27, 7, 1, Rust[1]);

        // Трубы от баков к реактору; под нагрузкой по ним бежит порция топлива.
        var step = (int)Math.Floor(t * Frames) % 8;
        foreach (var y in (ReadOnlySpan<int>)[8, 20])
        {
            g.Rect(8, y - 1, 10, 3, Ink);
            g.Rect(8, y, 10, 1, Rust[1]);
            if (working)
                g.Rect(9 + step, y, 2, 1, Team[3]);
            Tower(g, 6, y + 1, 3, 2, Iron);
        }

        Tower(g, 21, 17, 9, 4, Steel);
        const int lights = 6;
        for (var i = 0; i < lights; i++)
        {
            var angle = (double)i / lights * Turn;
            var x = Round(21 + Math.Cos(angle) * 6.5);
            var y = Round(13 + Math.Sin(angle) * 6.5);
            Bulb(g, x - 1, y - 1, working ? Chase(t, (double)i / lights) : 0);
        }
        Lamp(g, 21, 13, 3, working ? Pulse(t * 2) : 0.12 + 0.1 * Pulse(t));
    }, 1, true);
}
