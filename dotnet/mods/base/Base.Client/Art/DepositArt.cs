using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Облики месторождений — перенос lib/deposits.luau (внешний вид; раскладку месторождений по земле делает хост) и
/// depositsPass.ts: угловатые глыбы породы с жилами и крошка вокруг, топливо — лужей. Лежат на земле без тени и видны
/// там, где разведано.
/// </summary>
internal static class DepositArt
{
    /// <summary>Сторона месторождения в тайлах.</summary>
    public const int Size = 2;

    /// <summary>Виды по порядку DEPOSIT_TYPES.</summary>
    public static readonly string[] Types = ["metal", "silicon", "fuel", "kharite", "lithium", "uranium"];

    // Тона породы каждого вида от тёмного к блику.
    private static readonly Dictionary<string, uint[]> Tones = new()
    {
        ["metal"] = [0x6b2f1e, 0xb5562e, 0xe58a4a, 0xffd9a0],
        ["silicon"] = [0x6e6856, 0xa9a184, 0xe0d9bc, 0xffffff],
        ["fuel"] = [0x0d0b10, 0x221d29, 0x3a3340, 0x8f86a8],
        ["kharite"] = [0x2e1247, 0x6a2fa8, 0xc06bff, 0xf3dcff],
        ["lithium"] = [0x5a2a3e, 0xb0607e, 0xf0a0c0, 0xffe6f0],
        ["uranium"] = [0x1f2a0a, 0x4a6a1a, 0x9bd040, 0xe8ffb0],
    };

    private const uint Ink = 0x2a1410;

    // Глыбы породы: левый верхний угол, ширина и высота; рудная крошка между ними.
    private static readonly int[][] Chunks =
    [
        [3, 5, 7, 5], [13, 2, 6, 4], [22, 6, 7, 6], [9, 11, 9, 7], [1, 15, 6, 5], [21, 16, 8, 6], [5, 23, 7, 5], [15, 22, 6, 6], [25, 25, 5, 4],
    ];

    private static readonly int[][] Crumbs =
    [
        [11, 8], [20, 3], [29, 14], [2, 11], [18, 19], [8, 20], [13, 29], [23, 23], [30, 21], [1, 27], [19, 13], [27, 2],
    ];

    /// <summary>Топливо: тёмная лужа с радужным бликом и брызгами вокруг.</summary>
    private static void DrawOil(Canvas g)
    {
        var tones = Tones["fuel"];
        foreach (var crumb in Crumbs)
            g.Rect(crumb[0], crumb[1], 2, 1, tones[1]);
        g.Circle(15, 16, 11, Ink);
        g.Circle(15, 16, 10, tones[0]);
        g.Circle(20, 21, 6, tones[0]);
        g.Circle(13, 14, 7, tones[1]);
        g.Rect(9, 11, 6, 1, tones[2]);
        g.Rect(10, 12, 3, 1, tones[3]);
        g.Rect(19, 19, 3, 1, 0x4a6a8a);
    }

    /// <summary>Месторождение: угловатые глыбы породы с жилами и крошка вокруг; топливо — лужей.</summary>
    public static void Draw(Canvas g, string kind)
    {
        if (kind == "fuel")
        {
            DrawOil(g);
            return;
        }
        var ore = Tones[kind];
        foreach (var crumb in Crumbs)
            g.Rect(crumb[0], crumb[1], 2, 1, ore[1]);
        for (var i = 0; i < Chunks.Length; i++)
        {
            int x = Chunks[i][0], y = Chunks[i][1], w = Chunks[i][2], h = Chunks[i][3];
            // Срезанные углы делают глыбу гранёной.
            g.Rect(x + 1, y, w - 2, h, Ink);
            g.Rect(x, y + 1, w, h - 2, Ink);
            g.Rect(x + 1, y + 1, w - 2, h - 2, ore[0]);
            g.Rect(x + 1, y + 1, w - 3, h - 3, ore[1]);
            // Жила: светлая грань сверху слева и блик на каждой второй глыбе.
            g.Rect(x + 1, y + 1, Math.Ceiling(w / 2.0), 1, ore[2]);
            g.Rect(x + 1, y + 2, 1, Math.Max(1, h - 4), ore[2]);
            if (i % 2 == 0)
                g.Rect(x + 2, y + 2, 1, 1, ore[3]);
        }
    }

    /// <summary>Облик месторождения вида kind: на земле, без тени, виден там, где разведано.</summary>
    public static LookSpec Look(string kind)
    {
        const int side = Size * 16;
        return new LookSpec((canvas, _, _) => Draw(canvas, kind))
        {
            Width = side,
            Height = side,
            Origin = (0, 0),
            Anchor = (side / 2.0, side / 2.0),
            Layer = LookLayer.Floor,
            Shadow = null,
            Memory = true,
            Portrait = new PortraitSpec(side, side, canvas => Draw(canvas, kind)) { Origin = (0, 0) },
        };
    }
}
