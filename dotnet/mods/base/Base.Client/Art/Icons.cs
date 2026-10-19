using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Значки и картинки Б-З одним списком: мини-значки грузов, значки тревог, эффекты, следы, дропы, покрытие и портреты
/// покрытия и уровней — перенос goodIcons.ts, powerPass.ts, combatPass.ts, decalsPass.ts, dropsPass.ts, selectionPass.ts,
/// pavingPass.ts и portraits.ts до байта. Имена обликов — «раздел/имя», как в эталоне Looks/golden/art-more.json.
/// </summary>
internal static class ArtExtras
{
    /// <summary>Все облики Б-З; mounts — турели юнита по виду (data.mounts): остов несёт их на своих местах.</summary>
    public static IEnumerable<(string Name, LookSpec Spec)> All(Func<string, IReadOnlyList<Mount>> mounts) =>
        [.. Icons.Looks(), .. Effects.Looks(mounts), .. Paving.Looks()];

    /// <summary>Объявляет облики Б-З и клетки слоя покрытия (CellLook).</summary>
    public static void Setup(ClientBuilder client, Func<string, IReadOnlyList<Mount>> mounts)
    {
        foreach (var (name, spec) in All(mounts))
            client.Look(name, spec);
        Paving.Cells(client);
    }
}

/// <summary>Простые функции чертежей TS: шум, округление JS, оттенок.</summary>
internal static class ArtMath
{
    /// <summary>Простое воспроизводимое число от 0 до 1 по трём целым (noise в decalsPass.ts; c = 0 — по двум).</summary>
    public static double Noise(double a, double b, double c = 0)
    {
        var value = Kharos.Mod.Sdk.DetMath.Sin(a * 127.1 + b * 311.7 + c * 74.7) * 43758.5453;
        return value - Math.Floor(value);
    }

    /// <summary>Math.round: половинки — вверх.</summary>
    public static double Round(double value) => Math.Floor(value + 0.5);
}

/// <summary>Мини-иконки грузов 10 × 10 — перенос goodIcons.ts.</summary>
internal static class Icons
{
    /// <summary>Грузы по порядку GOODS: ресурсы, изделия, руды.</summary>
    public static readonly string[] Goods =
    [
        "metal", "silicon", "fuel", "kharite", "lithium", "uranium", "blocks", "ammo", "parts", "rods",
        "metalOre", "siliconOre", "fuelOre", "khariteOre", "lithiumOre", "uraniumOre",
    ];

    /// <summary>Сторона мини-иконки в пикселях (GOOD_ICON).</summary>
    public const int Size = 10;

    /// <summary>Цвета грузов (GOOD_COLORS).</summary>
    public static uint ColorOf(string good) => good switch
    {
        "metal" => 0xb8c4d0,
        "silicon" => 0x7f9cff,
        "fuel" => 0xffc93c,
        "kharite" => 0xc06bff,
        "lithium" => 0xf59ac0,
        "uranium" => 0xb4e84a,
        "blocks" => 0xd8b98a,
        "ammo" => 0xe0803a,
        "parts" => 0x4fd6a2,
        "rods" => 0x7ef0b0,
        "metalOre" => 0x8d7a5f,
        "siliconOre" => 0x5a6a8a,
        "fuelOre" => 0x8a7a2a,
        "khariteOre" => 0x7a4a9a,
        "lithiumOre" => 0x9a6a7e,
        "uraniumOre" => 0x6a8a3a,
        _ => 0xffffff,
    };

    private static bool IsOre(string good) => good.EndsWith("Ore", StringComparison.Ordinal);

    /// <summary>Ресурс руды (resourceOf).</summary>
    private static string ResourceOf(string good) => IsOre(good) ? good[..^3] : good;

    /// <summary>Камень руды: тона серо-бурого кома.</summary>
    private const uint Rock = 0x7a6f62;

    /// <summary>
    /// Маски 10×10: o — контур, l — светлый тон, b — основной, d — тёмный, x — вкрапление цвета ресурса (у руды), точка — пусто.
    /// </summary>
    private static string[] Mask(string good) => good switch
    {
        // Слиток.
        "metal" => ["..........", "..........", "..oooooo..", "..ollllo..", ".olbbbbdo.", ".obbbbbdo.", "obbbbbbddo", "oooooooooo", "..........", ".........."],
        // Кристалл.
        "silicon" => ["....oo....", "...olbo...", "..olbbdo..", "..olbbdo..", "..olbbdo..", "..olbbdo..", "..olbbdo..", "...obdo...", "....oo....", ".........."],
        // Канистра.
        "fuel" => ["..........", "...ooo....", "..oo.oooo.", ".olllllbo.", ".olbbbbdo.", ".olbddbdo.", ".olbbbbdo.", ".olbbbbdo.", ".oddddddo.", ".oooooooo."],
        // Гранёный камень.
        "kharite" => ["..........", "..oooooo..", ".olllbbbo.", "oolllbbddo", ".olbbbbdo.", "..olbbdo..", "...obdo...", "....oo....", "..........", ".........."],
        // Столбик лепёшек.
        "lithium" => ["..........", "...oooo...", "..olllbo..", "..oddddo..", "..olllbo..", "..oddddo..", "..olllbo..", "..obbbdo..", "..oddddo..", "...oooo..."],
        // Таблетка со знаком радиации.
        "uranium" => ["...oooo...", "..olddlo..", ".ollddllo.", "obllddllbo", "obbbllbbbo", "oddbllbddo", "odddbbdddo", ".oddbbddo.", "..obbbbo..", "...oooo..."],
        // Три стержня связкой, торцы светятся.
        "rods" => [".oo.oo.oo.", ".ol.ol.ol.", ".ob.ob.ob.", ".ob.ob.ob.", ".ob.ob.ob.", ".ob.ob.ob.", "oooooooooo", "odddddddd.", ".ob.ob.ob.", ".od.od.od."],
        // Кладка.
        "blocks" => ["..........", "..........", "oooooooooo", "olbbdolbbd", "oddddodddd", "oooooooooo", "olbdolbbdo", "oddoddddoo", "oooooooooo", ".........."],
        // Два патрона.
        "ammo" => ["..o....o..", ".olo..olo.", ".olo..olo.", ".ooo..ooo.", ".olo..olo.", ".obo..obo.", ".obo..obo.", ".odo..odo.", ".ooo..ooo.", ".........."],
        // Микросхема.
        "parts" => ["..........", "..o.o.o...", ".ooooooo..", "oolllllboo", ".olbbbbdo.", "oolbbbbdoo", ".oddddddo.", ".ooooooo..", "..o.o.o...", ".........."],
        // Ком руды.
        _ => ["..........", "...oooo...", "..odbbdo..", ".odbxbbdo.", ".obbbbxbo.", "odbxbbbbdo", "obbbbxbbdo", "oddbbbddo.", ".oooooooo.", ".........."],
    };

    /// <summary>Цвет, сдвинутый к белому (amount &gt; 0) или к чёрному (amount &lt; 0): shade в goodIcons.ts.</summary>
    private static uint Shade(uint color, double amount)
    {
        uint Channel(int shift)
        {
            var value = (int)((color >> shift) & 255);
            return (uint)ArtMath.Round(amount > 0 ? value + (255 - value) * amount : value * (1 + amount));
        }
        return (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    /// <summary>Рисует мини-иконку груза: put получает место и цвет каждого закрашенного пикселя (paintGood).</summary>
    public static void Paint(string good, Action<int, int, uint> put)
    {
        var ore = IsOre(good);
        var baseColor = ore ? Rock : ColorOf(good);
        uint Tone(char sign) => sign switch
        {
            'o' => Shade(baseColor, -0.7),
            'l' => Shade(baseColor, 0.4),
            'b' => Shade(baseColor, 0),
            'd' => Shade(baseColor, -0.4),
            _ => Shade(ore ? ColorOf(ResourceOf(good)) : baseColor, 0.1),
        };
        var rows = Mask(good);
        for (var y = 0; y < rows.Length; y++)
            for (var x = 0; x < Size; x++)
                if (rows[y][x] != '.')
                    put(x, y, Tone(rows[y][x]));
    }

    /// <summary>goods/&lt;груз&gt; — один кадр 10 × 10, начало координат в левом верхнем углу.</summary>
    public static IEnumerable<(string Name, LookSpec Spec)> Looks()
    {
        foreach (var good in Goods)
        {
            var name = good;
            yield return ("goods/" + name, new LookSpec((canvas, _, _) => Paint(name, (x, y, color) => canvas.Pixel(x, y, color)))
            {
                Width = Size,
                Height = Size,
                Origin = (0, 0),
                Shadow = null,
                Layer = LookLayer.Floor,
            });
        }
    }
}
