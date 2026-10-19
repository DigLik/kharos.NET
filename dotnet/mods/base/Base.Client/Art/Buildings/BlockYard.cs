using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Склад стройблоков (blockYard) — перенос из buildingArt.ts: двор с поддонами кирпичных блоков и погрузчиком.</summary>
internal static class BlockYard
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) =>
    {
        var tint = KitB.Accent(KitB.Blocks);
        Slab(g, 0, 1, 32, 31, 3, Iron);
        g.Rect(2, 3, 28, 22, Iron[1]);
        // Поддоны: деревянная решётка, на ней кладка блоков со швами.
        foreach (var (x, y) in (ReadOnlySpan<(int, int)>)[(3, 4), (17, 4), (3, 14), (17, 14)])
        {
            g.Rect(x, y, 12, 9, Ink);
            g.Rect(x + 1, y + 7, 10, 1, Rust[1]);
            for (var row = 0; row < 3; row++)
                for (var k = 0; k < 3; k++)
                {
                    var bx = x + 1 + k * 3 + row % 2;
                    g.Rect(bx, y + 1 + row * 2, 3, 2, row == 0 ? tint[2] : tint[1]);
                    g.Rect(bx + 2, y + 1 + row * 2, 1, 2, tint[0]);
                }
        }
        // Погрузчик ездит по проходу внизу.
        var loader = KitB.Round(4 + 20 * (0.5 - 0.5 * Math.Cos(KitB.Turn * t)));
        g.Rect(loader, 26, 5, 3, Ink);
        g.Rect(loader + 1, 26, 3, 2, Hazard[1]);
        Bulb(g, 28, 27, Pulse(t));
    });
}
