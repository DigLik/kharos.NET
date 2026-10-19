using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Реактор (reactor, 3×3) — перенос reactor из buildingArt.ts.</summary>
internal static class Reactor
{
    private const double Turn = Math.PI * 2;

    /// <summary>
    /// Герметичный купол над активной зоной, градирня с паром и щит с полосами опасности. Пока горит стержень, из окна
    /// купола идёт синее свечение, огни по кольцу бегут, а градирня парит.
    /// </summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(3, 3, (g, t, _, working) =>
    {
        uint[] concrete = [0x5a5650, 0x837e76, 0xa8a299, 0xc8c2b8, 0xe6e1d8];
        uint[] glow = [0x1a3a8a, 0x3f8cff, 0xbfe4ff];
        Slab(g, 0, 2, 48, 46, 4, concrete);
        for (var x = 2; x < 46; x += 4)
        {
            g.Rect(x, 42, 2, 2, Hazard[1]);
            g.Rect(x + 2, 42, 2, 2, Hazard[0]);
        }
        // Градирня справа: широкое основание, сужение и пар над ней.
        Tower(g, 36, 26, 8, 10, concrete);
        g.Circle(36, 16, 6, Ink);
        g.Circle(36, 16, 5, concrete[0]);
        if (working)
        {
            for (var i = 0; i < 3; i++)
            {
                var rise = (t + i / 3.0) % 1 * 10;
                g.Circle(36 + (i - 1) * 2, 14 - rise, 3 + rise * 0.3, i % 2 != 0 ? 0xdfe6eau : 0xf4f7f9u);
            }
        }
        // Купол над активной зоной слева: окно со свечением и кольцо огней.
        Tower(g, 16, 26, 12, 4, Steel);
        var level = working ? 0.6 + 0.4 * Pulse(t * 2) : 0.1;
        g.Rect(13, 18, 7, 5, Ink);
        g.Rect(14, 19, 5, 3, working ? glow[1] : glow[0]);
        if (working)
            g.Rect(15, 20, 3, 1, glow[2]);
        g.Light(16, 20, 6, level * 0.8);
        const int lights = 8;
        for (var i = 0; i < lights; i++)
        {
            var angle = (double)i / lights * Turn;
            var x = Round(16 + Math.Cos(angle) * 9);
            var y = Round(22 + Math.Sin(angle) * 9);
            Bulb(g, x - 1, y - 1, working ? Chase(t, (double)i / lights) : 0.1);
        }
        // Трубы от купола к градирне.
        g.Rect(27, 30, 4, 3, Ink);
        g.Rect(27, 31, 4, 1, Rust[1]);
        Lamp(g, 6, 38, 2, Pulse(t * (working ? 2 : 1)));
    }, 1, true);
}
