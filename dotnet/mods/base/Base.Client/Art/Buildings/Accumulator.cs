using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Накопитель энергии (accumulator, 2×2) — перенос accumulator из buildingArt.ts.</summary>
internal static class Accumulator
{
    /// <summary>
    /// Шесть банок в стойке под общей шиной; по банкам снизу вверх бежит заряд цвета команды, на конце шины мигает огонёк.
    /// </summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(2, 2, (g, t, _) =>
    {
        var tint = Accent(GoodColor.Lithium);
        Slab(g, 0, 2, 32, 30, 3, Steel);
        // Шина над банками.
        g.Rect(3, 4, 26, 3, Ink);
        g.Rect(4, 5, 24, 1, Rust[2]);
        for (var i = 0; i < 6; i++)
        {
            var x = 4 + i % 3 * 8;
            var y = 8 + Math.Floor(i / 3.0) * 10;
            g.Rect(x, y, 7, 9, Ink);
            g.Rect(x + 1, y + 1, 5, 7, Steel[2]);
            g.Rect(x + 1, y + 1, 1, 7, Steel[3]);
            g.Rect(x + 2, y, 3, 1, tint[1]);
            // Заряд: столбик растёт и опадает, у каждой банки со своим сдвигом.
            var level = Round(Pulse(t, i / 6.0) * 5);
            if (level > 0)
                g.Rect(x + 3, y + 7 - level, 2, level, TeamColor(0.5 + 0.1 * level));
        }
        Bulb(g, 27, 4, Pulse(t * 2));
    });
}
