using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Выпарка лития (evaporator, 3×2) — перенос evaporator из buildingArt.ts.</summary>
internal static class Evaporator
{
    /// <summary>
    /// Два мелких пруда розового рассола, в работе по ним бегут блики; насосная с трубой к прудам и куча соли, которую
    /// подгребает скребок.
    /// </summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(3, 2, (g, t, _, working) =>
    {
        var tint = Accent(GoodColor.Lithium);
        uint[] salt = [0x7a7068, 0xa89e94, 0xd0c8be, 0xe8e2da, 0xffffff];
        Slab(g, 0, 2, 48, 30, 3, salt);
        // Пруды: обваловка, рассол и блики, которые в работе плывут.
        foreach (var (x, y, offset) in (ReadOnlySpan<(int, int, double)>)[(3, 5, 0), (3, 16, 0.5)])
        {
            g.Rect(x, y, 26, 10, Ink);
            g.Rect(x + 1, y + 1, 24, 8, salt[0]);
            g.Rect(x + 2, y + 2, 22, 6, tint[0]);
            g.Rect(x + 2, y + 2, 22, 2, tint[1]);
            for (var i = 0; i < 3; i++)
            {
                var shift = working ? Math.Floor((t + offset + i / 3.0) % 1 * 18) : i * 6;
                g.Rect(x + 3 + shift, y + 4 + i % 2, 3, 1, tint[2]);
            }
        }
        // Насосная: короб с трубой к прудам, лампа насоса мигает в такт.
        g.Rect(31, 5, 13, 11, Ink);
        g.Rect(32, 6, 11, 9, Iron[2]);
        g.Rect(32, 6, 11, 1, Iron[3]);
        g.Rect(29, 9, 3, 2, Iron[1]);
        g.Rect(29, 20, 3, 2, Iron[1]);
        g.Rect(30, 9, 1, 13, Iron[1]);
        Lamp(g, 40, 10, 2, working ? Pulse(t * 4) : 0.2);
        // Куча соли и скребок, который её подгребает.
        g.Circle(38, 23, 5, Ink);
        g.Circle(38, 23, 4, salt[3]);
        g.Circle(37, 22, 2, salt[4]);
        g.Rect(37, 20, 2, 1, tint[2]);
        var reach = working ? Round(Pulse(t) * 3) : 0;
        g.Rect(31 + reach, 27, 4, 2, Ink);
        g.Rect(31 + reach, 27, 3, 1, Hazard[1]);
    }, 1, true);
}
