using Kharos.Mod.Sdk.Client;
using static Base.Client.BuildingKit;

namespace Base.Client.Buildings;

/// <summary>Плавильня (smelter, 3×3) — перенос smelter из buildingArt.ts.</summary>
internal static class Smelter
{
    private const double Turn = Math.PI * 2;

    /// <summary>
    /// Открытая ванна жидкого металла — сверху видно раскалённое зеркало с коркой шлака, — жёлоб из неё на конвейер
    /// изложниц, где слитки остывают, и штабель готовых слитков.
    /// </summary>
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Look(3, 3, (g, t, _, working) =>
    {
        var tint = Accent(GoodColor.Metal);
        var frame = (int)Math.Floor(t * Frames);
        Slab(g, 0, 2, 48, 46, 3, Iron);
        g.Rect(2, 4, 44, 1, Rust[0]);

        // Ванна: кирпичное кольцо, внутри металл. В работе он ярко светится и по нему плывёт шлак; в простое — остывшая корка.
        const double cx = 16, cy = 18;
        g.Circle(cx, cy, 14, Ink);
        g.Circle(cx, cy, 13, Brick[1]);
        g.Circle(cx - 1, cy - 1, 12, Brick[2]);
        g.Circle(cx, cy, 10, Ink);
        g.Circle(cx, cy, 9, working ? FireColor(0.55) : FireColor(0.05));
        g.Circle(cx - 1, cy - 1, 7, working ? FireColor(0.75) : 0x3a2a26);
        g.Circle(cx - 2, cy - 2, 4, working ? FireColor(0.95) : 0x4a3530);
        for (var i = 0; i < 5; i++)
        {
            // Корка шлака: в работе кружит по зеркалу, в простое лежит и чуть тлеет трещинами.
            var angle = Turn * ((working ? t : 0) + i / 5.0);
            var reach = 4 + Hash(i, 1) * 4;
            var x = Round(cx + Math.Cos(angle) * reach);
            var y = Round(cy + Math.Sin(angle) * reach);
            g.Rect(x, y, 2, 1, working ? Fire[1] : FireColor(0.25 + 0.15 * Pulse(t, i / 5.0)));
        }
        if (working)
        {
            // Пузырь лопается в случайном месте каждые пару кадров.
            var bubble = Math.Floor(frame / 2.0);
            g.Rect(Round(cx - 5 + Hash(bubble, 7) * 10), Round(cy - 5 + Hash(bubble, 9) * 10), 1, 1, Fire[4]);
        }
        g.Light(cx, cy, 8, working ? 0.8 + 0.2 * Pulse(t * 2) : 0.15);

        // Жёлоб из ванны вправо, на конвейер.
        g.Rect(29, 15, 7, 4, Ink);
        g.Rect(29, 16, 7, 2, working ? FireColor(0.7 + 0.25 * Pulse(t * 4)) : Brick[0]);

        // Конвейер изложниц вдоль правого края: формы едут вниз, металл в них остывает от огня к цвету металла.
        g.Rect(35, 5, 10, 38, Ink);
        g.Rect(36, 6, 8, 36, Iron[0]);
        var shift = working ? frame % 6 : 0;
        for (var k = 0; k < 7; k++)
        {
            var y = 6 + k * 6 + shift - 6;
            if (y < 6 || y > 38)
                continue;
            g.Rect(37, y, 6, 4, Iron[2]);
            g.Rect(38, y + 1, 4, 2, k < 2 && working ? FireColor(0.9 - k * 0.3) : k < 4 && working ? Fire[1] : tint[1]);
            g.Rect(38, y + 1, 4, 1, k < 2 && working ? Fire[4] : tint[2]);
        }

        // Штабель готовых слитков внизу слева.
        foreach (var (x, y) in (ReadOnlySpan<(int, int)>)[(4, 38), (11, 38), (18, 38), (7, 35), (14, 35), (10, 32)])
        {
            g.Rect(x, y, 7, 4, Ink);
            g.Rect(x + 1, y + 1, 5, 2, tint[1]);
            g.Rect(x + 1, y + 1, 5, 1, tint[2]);
        }
        Lamp(g, 29, 40, 2, Pulse(t));
    }, 1, true);
}
