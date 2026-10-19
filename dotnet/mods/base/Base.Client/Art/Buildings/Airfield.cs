using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Аэродром (airfield, 4×3) — перенос airfield из buildingArt.ts.</summary>
internal static class Airfield
{
    private const double Turn = Math.PI * 2;

    /// <summary>
    /// Взлётная полоса с разметкой и бегущими огнями вдоль неё, ангар с полукруглой крышей и открытыми воротами, где
    /// стоит самолёт, и диспетчерская вышка с вращающимся маяком.
    /// </summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(4, 3, (g, t, _) =>
    {
        Slab(g, 0, 2, 64, 46, 3, Steel);

        // Полоса вдоль нижнего края: асфальт, осевая разметка, огни по краям бегут к торцу.
        g.Rect(3, 30, 58, 13, Ink);
        g.Rect(4, 31, 56, 11, 0x23262b);
        for (var x = 7; x < 58; x += 7)
            g.Rect(x, 36, 4, 1, 0xd8d2bf);
        g.Rect(5, 32, 1, 9, 0xd8d2bf);
        g.Rect(58, 32, 1, 9, 0xd8d2bf);
        const int lights = 8;
        for (var i = 0; i < lights; i++)
        {
            var x = 7 + i * 7;
            Bulb(g, x, 30, Chase(t, (double)i / lights));
            Bulb(g, x, 41, Chase(t, (double)i / lights));
        }

        // Ангар: полукруглая крыша с рёбрами и открытые ворота.
        Slab(g, 3, 1, 34, 28, 4, Iron);
        for (var x = 6; x < 35; x += 4)
        {
            g.Rect(x, 3, 1, 18, Iron[0]);
            g.Rect(x + 1, 3, 1, 18, Iron[2]);
        }
        g.Rect(9, 18, 22, 10, Ink);
        g.Rect(10, 19, 20, 9, Dark);
        // Самолёт в воротах носом наружу.
        g.Rect(19, 20, 2, 8, Steel[3]);
        g.Rect(13, 23, 14, 2, Steel[2]);
        g.Rect(17, 20, 6, 1, Steel[2]);
        g.Rect(19, 27, 2, 1, Team[2]);

        // Вышка: тонкая башня, остеклённая кабина и маяк на крыше.
        Tower(g, 52, 22, 3, 12, Steel);
        g.Rect(46, 4, 13, 7, Ink);
        g.Rect(47, 5, 11, 5, Team[1]);
        g.Rect(47, 5, 11, 1, Team[2]);
        var sweep = Math.Cos(Turn * t);
        g.Rect(52 + Round(sweep * 3), 2, 2, 2, Team[3]);
        Lamp(g, 52, 1, 1, Pulse(t * 2));
        Lamp(g, 42, 24, 2, Pulse(t, 0.5));
    });
}
