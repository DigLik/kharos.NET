using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Склад кремния (siliconStore) — перенос из buildingArt.ts: белый герметичный корпус с боксами под стеклом и фильтрами.</summary>
internal static class SiliconStore
{
    private static readonly uint[] White = [0x6d7680, 0x9aa4ae, 0xc3cbd3, 0xe1e7ec, 0xffffff];

    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) =>
    {
        var tint = KitB.Accent(KitB.Silicon);
        Slab(g, 0, 1, 32, 31, 3, White);
        // Боксы: окна в корпусе, за стеклом — стопки пластин.
        foreach (var (x, y) in (ReadOnlySpan<(int, int)>)[(3, 4), (17, 4), (3, 15), (17, 15)])
        {
            g.Rect(x, y, 12, 9, Ink);
            g.Rect(x + 1, y + 1, 10, 7, 0x1a2a38);
            for (var k = 0; k < 3; k++)
            {
                g.Rect(x + 2 + k * 3, y + 3, 2, 4, tint[1]);
                g.Rect(x + 2 + k * 3, y + 3, 2, 1, tint[2]);
            }
            g.Rect(x + 1, y + 1, 3, 1, 0x5a7a90);
        }
        // Фильтры на крыше: решётки вентиляторов, огонёк чистоты.
        foreach (var x in (ReadOnlySpan<int>)[8, 22])
        {
            g.Circle(x, 27, 2, Ink);
            g.Rect(x - 1, 27 - (int)Math.Floor(t * Frames) % 2, 2, 1, White[3]);
        }
        Bulb(g, 14, 26, Pulse(t));
    });
}
