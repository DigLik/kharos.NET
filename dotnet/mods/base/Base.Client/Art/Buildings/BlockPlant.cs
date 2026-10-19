using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Цех стройблоков (blockPlant, 2×2) — перенос blockPlant из buildingArt.ts.</summary>
internal static class BlockPlant
{
    private const double Turn = Math.PI * 2;

    /// <summary>Бетономешалка с лопастями, формовочный стол, где блок выдавливается из формы, и паллеты кладки.</summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(2, 2, (g, t, _, working) =>
    {
        var tint = Accent(GoodColor.Blocks);
        uint[] concrete = [0x4a4844, 0x6e6a63, 0x8f8a80, 0xb1aba0, 0xd4cfc4];
        Slab(g, 0, 2, 32, 30, 3, concrete);

        // Мешалка: барабан с лопастями, в работе они крутятся в сером растворе.
        const double cx = 10, cy = 11;
        g.Circle(cx, cy, 8, Ink);
        g.Circle(cx, cy, 7, Rust[1]);
        g.Circle(cx, cy, 5, Ink);
        g.Circle(cx, cy, 4, concrete[1]);
        for (var i = 0; i < 3; i++)
        {
            var angle = Turn * ((working ? t : 0) + i / 3.0);
            g.Line(cx, cy, cx + Math.Cos(angle) * 4, cy + Math.Sin(angle) * 4, 1, concrete[3]);
        }
        g.Rect(cx, cy, 1, 1, Ink);

        // Формовочный стол под мешалкой: блок выдавливается из формы и уезжает вправо.
        g.Rect(2, 21, 16, 7, Ink);
        g.Rect(3, 22, 14, 5, Iron[1]);
        g.Rect(3, 22, 4, 5, Iron[0]);
        var slide = working ? (int)Math.Floor(t * Frames) % 8 : 0;
        if (working)
        {
            g.Rect(4 + slide, 23, 4, 3, Ink);
            g.Rect(5 + slide, 23, 3, 2, tint[1]);
            g.Rect(5 + slide, 23, 3, 1, tint[2]);
        }

        // Паллеты стройблоков: кладка вперевязку.
        foreach (var (x, y) in (ReadOnlySpan<(int, int)>)[(20, 5), (20, 17)])
        {
            g.Rect(x, y, 10, 9, Ink);
            for (var row = 0; row < 3; row++)
            {
                var offset = row % 2 != 0 ? 2 : 0;
                for (var col = -1; col < 3; col++)
                {
                    var bx = x + 1 + offset + col * 4;
                    var left = Math.Max(bx, x + 1);
                    var right = Math.Min(bx + 3, x + 9);
                    if (right <= left)
                        continue;
                    g.Rect(left, y + 1 + row * 3, right - left, 2, tint[1]);
                    g.Rect(left, y + 1 + row * 3, right - left, 1, tint[2]);
                }
            }
        }
        Lamp(g, 18, 3, 1, Pulse(t));
        Bulb(g, 15, 18, working ? Chase(t, 0) : 0);
    }, 1, true);
}
