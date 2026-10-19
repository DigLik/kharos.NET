using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Бункер боеприпасов (ammoBunker) — перенос из buildingArt.ts: заглублённый бетонный каземат с полосами опасности и бронедверью.</summary>
internal static class AmmoBunker
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) =>
    {
        var tint = KitB.Accent(KitB.Ammo);
        Slab(g, 0, 1, 32, 15, 2, Iron);
        // Обвалование: земляной вал вокруг крыши.
        g.Rect(2, 3, 28, 9, 0x4d4030);
        g.Rect(4, 4, 24, 7, Iron[2]);
        g.Rect(4, 4, 24, 1, Iron[3]);
        // Полосы опасности по краю крыши.
        for (var x = 4; x < 28; x += 4)
            g.Rect(x, 10, 2, 1, Hazard[1]);
        // Ящики у входа и бронедверь.
        foreach (var x in (ReadOnlySpan<int>)[6, 10])
        {
            g.Rect(x, 6, 3, 3, Ink);
            g.Rect(x, 6, 2, 2, tint[1]);
            g.Rect(x, 6, 2, 1, tint[2]);
        }
        g.Rect(18, 5, 8, 5, Ink);
        g.Rect(19, 6, 6, 3, Iron[1]);
        g.Rect(21, 7, 2, 1, Hazard[1]);
        // Красная лампа взрывоопасности мигает.
        var level = Pulse(t * 2);
        g.Rect(28, 5, 2, 2, level > 0.5 ? 0xff6b5au : 0x6a1a10u);
        g.Light(29, 6, 1, level * 0.5);
    });
}
