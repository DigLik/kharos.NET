using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Перегонный завод (distillery, 2×3) — перенос distillery из buildingArt.ts.</summary>
internal static class Distillery
{
    private const double Turn = Math.PI * 2;

    /// <summary>Ректификационная колонна сверху, шаровые баки и факел, где сгорает лишний газ.</summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(2, 3, (g, t, _, working) =>
    {
        var tint = Accent(GoodColor.Fuel);
        Slab(g, 0, 2, 32, 46, 3, Steel);
        g.Rect(3, 42, 26, 1, Rust[1]);

        Conduit(g, 4, 30, 24);
        Conduit(g, 10, 22, 3);
        foreach (var x in (ReadOnlySpan<int>)[8, 22])
        {
            g.Circle(x, 36, 6, Ink);
            g.Circle(x, 36, 5, tint[0]);
            g.Circle(x - 1, 35, 4, tint[1]);
            g.Rect(x - 3, 33, 2, 1, tint[2]);
        }

        // Колонна сверху: толстый круг с кольцевой площадкой; в работе по кольцу бегут огни.
        Tower(g, 12, 16, 8, 3, Steel);
        g.Circle(12, 13, 5, Ink);
        g.Circle(12, 13, 4, Steel[2]);
        g.Circle(11, 12, 2, Steel[3]);
        for (var i = 0; i < 6; i++)
        {
            var angle = Turn * i / 6;
            Bulb(g, Round(12 + Math.Cos(angle) * 6.5) - 1, Round(13 + Math.Sin(angle) * 6.5) - 1, working ? Chase(t, i / 6.0) : 0);
        }
        Conduit(g, 19, 10, 5);

        // Факел сверху: в работе пляшет огонь, в простое тлеет запальник.
        var flame = working ? Pulse(t * 4) : 0;
        g.Circle(25, 10, 4, Ink);
        g.Circle(25, 10, 3, Iron[2]);
        if (working)
        {
            g.Circle(25, 10, 2 + flame, FireColor(0.55 + flame * 0.2));
            g.Circle(25, 9, 1 + flame * 0.6, FireColor(0.95));
        }
        else
            g.Rect(25, 10, 1, 1, FireColor(0.5 + 0.3 * Pulse(t)));
        g.Light(25, 9, 4, working ? 0.7 + flame * 0.3 : 0.1);
        Lamp(g, 27, 24, 1, Pulse(t));
    }, 1, true);
}
