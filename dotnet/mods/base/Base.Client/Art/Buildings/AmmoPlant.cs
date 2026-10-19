using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Патронный цех (ammoPlant, 2×2) — перенос ammoPlant из buildingArt.ts.</summary>
internal static class AmmoPlant
{
    /// <summary>Бункер пороха, по ленте рядами едут латунные гильзы, на краях — полосы опасности.</summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(2, 2, (g, t, _, working) =>
    {
        var tint = Accent(GoodColor.Ammo);
        Slab(g, 0, 2, 32, 30, 3, Iron);
        for (var x = 2; x < 30; x += 4)
        {
            g.Rect(x, 26, 2, 2, Hazard[1]);
            g.Rect(x + 2, 26, 2, 2, Hazard[0]);
        }

        // Бункер пороха: воронка сверху, тёмная засыпка; в работе из горла сыплется порох.
        g.Rect(2, 4, 11, 11, Ink);
        g.Rect(3, 5, 9, 9, Iron[2]);
        g.Rect(5, 7, 5, 5, Iron[1]);
        g.Rect(6, 8, 3, 3, 0x1a1a1a);
        if (working)
            g.Rect(7, 9 + (int)Math.Floor(t * Frames) % 2, 1, 1, Iron[3]);
        // Подача с бункера на ленту.
        g.Rect(13, 8, 4, 3, Ink);
        g.Rect(13, 9, 4, 1, Rust[1]);

        // Лента: три ряда гильз, в работе ползут вниз; донца гильз сверху — латунные кружки.
        g.Rect(16, 4, 14, 21, Ink);
        g.Rect(17, 5, 12, 19, Iron[0]);
        var shift = working ? (int)Math.Floor(t * Frames) % 4 : 0;
        for (var row = 0; row < 6; row++)
        {
            var y = 5 + row * 4 + shift - 2;
            if (y < 5 || y > 21)
                continue;
            for (var col = 0; col < 3; col++)
            {
                var x = 19 + col * 4;
                g.Circle(x, y + 1, 1.5, tint[0]);
                g.Rect(x, y + 1, 1, 1, row > 2 ? tint[2] : Iron[3]);
            }
        }
        // Готовые ящики с патронами внизу слева.
        foreach (var x in (ReadOnlySpan<int>)[3, 9])
        {
            g.Rect(x, 17, 6, 7, Ink);
            g.Rect(x + 1, 18, 4, 5, 0x3d4a2a);
            g.Rect(x + 1, 20, 4, 1, tint[1]);
        }
        Lamp(g, 29, 3, 1, Pulse(t * (working ? 3 : 1)));
        Bulb(g, 14, 13, working ? Chase(t, 0.5) : 0);
    }, 1, true);
}
