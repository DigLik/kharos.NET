using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Покрытие и его значки — перенос pavingPass.ts и portraits.ts (paveCanvas, levelCanvas) до байта: плитки фундамента, дороги
/// и моста на каждый из 16 наборов соседей и портреты команд (покрытие, снятие, гора, обрыв; три уровня модернизации).
/// Облики плиток — paving/foundation, paving/road, paving/bridge: 16 вариантов, вариант — маска соседей с тем же покрытием
/// (1 север, 2 восток, 4 юг, 8 запад — как LookSpec.Variants клетки слоя с connect). Портреты — portraits/pave/&lt;значок&gt;
/// и portraits/level/&lt;1..3&gt;: кадр 32 × 32 и тот же рисунок портретом (обрезается по закрашенному).
/// </summary>
internal static class Paving
{
    private const int Tile = 16, Up = 1, Right = 2, Down = 4, Left = 8;

    /// <summary>Виды плиток по порядку LOOKS в pavingPass.ts.</summary>
    public static readonly string[] Kinds = ["foundation", "road", "bridge"];

    /// <summary>Значки покрытия по порядку PaveIcon.</summary>
    public static readonly string[] IconNames = ["foundation", "road", "bridge", "remove", "peak", "cliff"];

    private static double Noise(double a, double b) => ArtMath.Noise(a, b);

    /// <summary>Фундамент: бетонная плита со швами по краю и болтами по углам.</summary>
    private static void Foundation(Canvas g, int mask)
    {
        g.Rect(0, 0, Tile, Tile, 0x8d8a82);
        for (var i = 0; i < 18; i++)
            g.Rect(Math.Floor(Noise(i, 1) * 16), Math.Floor(Noise(i, 2) * 16), 1, 1, Noise(i, 3) < 0.5 ? 0x7d7a73u : 0x9c998fu);
        // Шов между плитами — тонкий, край площадки — тёмный бордюр.
        static uint Edge(bool open) => open ? 0x4f4c47u : 0x77746du;
        g.Rect(0, 0, Tile, 1, Edge((mask & Up) == 0));
        g.Rect(0, Tile - 1, Tile, 1, Edge((mask & Down) == 0));
        g.Rect(0, 0, 1, Tile, Edge((mask & Left) == 0));
        g.Rect(Tile - 1, 0, 1, Tile, Edge((mask & Right) == 0));
        foreach (var (x, y) in ((int, int)[])[(3, 3), (12, 3), (3, 12), (12, 12)])
            g.Rect(x, y, 1, 1, 0x5d5a54);
    }

    /// <summary>Дорога: укатанное покрытие с зернистостью, по открытым сторонам — бордюр.</summary>
    private static void Road(Canvas g, int mask)
    {
        g.Rect(0, 0, Tile, Tile, 0x45403b);
        for (var i = 0; i < 24; i++)
            g.Rect(Math.Floor(Noise(i, 7) * 16), Math.Floor(Noise(i, 8) * 16), 1, 1, Noise(i, 9) < 0.5 ? 0x3a3632u : 0x524c46u);
        const uint curb = 0x8c8273;
        if ((mask & Up) == 0) g.Rect(0, 0, Tile, 2, curb);
        if ((mask & Down) == 0) g.Rect(0, Tile - 2, Tile, 2, curb);
        if ((mask & Left) == 0) g.Rect(0, 0, 2, Tile, curb);
        if ((mask & Right) == 0) g.Rect(Tile - 2, 0, 2, Tile, curb);
    }

    /// <summary>Мост: настил из досок поперёк хода на сваях, по открытым сторонам — перила.</summary>
    private static void Bridge(Canvas g, int mask)
    {
        var across = (mask & (Left | Right)) == 0 && (mask & (Up | Down)) != 0;
        for (var i = 0; i < Tile; i += 4)
        {
            var tone = i % 8 != 0 ? 0x6e4b2cu : 0x7d5733u;
            if (across) g.Rect(0, i, Tile, 3, tone);
            else g.Rect(i, 0, 3, Tile, tone);
        }
        // Щели между досками.
        for (var i = 3; i < Tile; i += 4)
        {
            if (across) g.Rect(0, i, Tile, 1, 0x3b2a1a);
            else g.Rect(i, 0, 1, Tile, 0x3b2a1a);
        }
        const uint rail = 0x2f2a26, post = 0x8f8a80;
        if ((mask & Up) == 0)
        {
            g.Rect(0, 0, Tile, 2, rail);
            g.Rect(1, 0, 2, 2, post);
            g.Rect(Tile - 3, 0, 2, 2, post);
        }
        if ((mask & Down) == 0)
        {
            g.Rect(0, Tile - 2, Tile, 2, rail);
            g.Rect(1, Tile - 2, 2, 2, post);
            g.Rect(Tile - 3, Tile - 2, 2, 2, post);
        }
        if ((mask & Left) == 0)
        {
            g.Rect(0, 0, 2, Tile, rail);
            g.Rect(0, 1, 2, 2, post);
            g.Rect(0, Tile - 3, 2, 2, post);
        }
        if ((mask & Right) == 0)
        {
            g.Rect(Tile - 2, 0, 2, Tile, rail);
            g.Rect(Tile - 2, 1, 2, 2, post);
            g.Rect(Tile - 2, Tile - 3, 2, 2, post);
        }
    }

    /// <summary>Плитка вида kind (foundation, road, bridge) с набором соседей mask.</summary>
    public static void DrawTile(Canvas g, string kind, int mask)
    {
        switch (kind)
        {
            case "foundation": Foundation(g, mask); break;
            case "road": Road(g, mask); break;
            default: Bridge(g, mask); break;
        }
    }

    // --- Портреты (portraits.ts) ---
    private const uint Outline = 0x1a1410;

    /// <summary>Плитки значка покрытия: вид и набор соседей каждой, рядами сверху вниз (PAVE_TILES).</summary>
    private static (string Kind, int Mask)[][] Rows(string icon) => icon switch
    {
        // Площадка 2×2: у каждой плитки соседи — две другие.
        "foundation" or "remove" => [[("foundation", 6), ("foundation", 12)], [("foundation", 3), ("foundation", 9)]],
        "road" => [[("road", 2), ("road", 8)]],
        "bridge" => [[("bridge", 2), ("bridge", 8)]],
        _ => [],
    };

    /// <summary>Гора с киркой: треугольник скалы со снежной шапкой и кирка поперёк.</summary>
    private static void PeakIcon(Canvas g)
    {
        for (var row = 0; row < 20; row++)
        {
            var half = (int)ArtMath.Round(row * 0.7) + 1;
            var y = 6 + row;
            g.Rect(16 - half - 1, y, half * 2 + 2, 1, Outline);
            g.Rect(16 - half, y, half, 1, 0x9a8a74);
            g.Rect(16, y, half, 1, 0x6e604f);
            if (row < 5) g.Rect(16 - half, y, half * 2, 1, 0xdcd4c6);
        }
        g.Rect(2, 26, 28, 2, Outline);
        g.Line(9, 27, 23, 11, 4, Outline);
        g.Line(9, 27, 23, 11, 2, 0x8a5426);
        g.Line(17, 7, 28, 15, 5, Outline);
        g.Line(17, 7, 28, 15, 3, 0xc8ccd0);
    }

    /// <summary>Обрыв со спуском: уступ плато слева, земля справа, между ними — пологий пандус.</summary>
    private static void CliffIcon(Canvas g)
    {
        g.Rect(2, 9, 15, 19, Outline);
        g.Rect(3, 10, 13, 17, 0x9a8a74);
        g.Rect(3, 10, 13, 3, 0xb4a48c);
        g.Rect(2, 22, 28, 6, Outline);
        g.Rect(3, 23, 26, 4, 0x7e705e);
        for (var step = 0; step < 12; step++)
            g.Rect(16 + step, 11 + step, 2, 12 - step, step % 2 != 0 ? 0xc0ae92u : 0xb4a48cu);
        g.Line(16, 10, 29, 23, 2, Outline);
        g.Line(18, 9, 25, 16, 2, 0xe0902c);
        g.Line(25, 16, 25, 11, 2, 0xe0902c);
        g.Line(25, 16, 20, 16, 2, 0xe0902c);
    }

    /// <summary>Значок покрытия для сетки команд, 32 × 32 (paveCanvas).</summary>
    public static void DrawIcon(Canvas g, string icon)
    {
        var rows = Rows(icon);
        // Полоса дороги — посередине по высоте.
        var top = (32 - rows.Length * Tile) / 2;
        for (var y = 0; y < rows.Length; y++)
            for (var x = 0; x < rows[y].Length; x++)
            {
                g.Translate(x * Tile, top + y * Tile);
                DrawTile(g, rows[y][x].Kind, rows[y][x].Mask);
                g.Translate(-x * Tile, -(top + y * Tile));
            }
        if (icon == "peak") PeakIcon(g);
        if (icon == "cliff") CliffIcon(g);
        if (icon == "remove")
        {
            g.Line(7, 7, 25, 25, 4, 0x1a0806);
            g.Line(25, 7, 7, 25, 4, 0x1a0806);
            g.Line(7, 7, 25, 25, 2, 0xe0402c);
            g.Line(25, 7, 7, 25, 2, 0xe0402c);
        }
    }

    /// <summary>Значок модернизации: корпус здания и над ним шевроны цвета команды, по одному на уровень (levelCanvas).</summary>
    public static void DrawLevel(Canvas g, int level)
    {
        const uint ink = 0x0b111b;
        // Корпус с окнами.
        g.Rect(5, 17, 22, 14, ink);
        g.Rect(6, 18, 20, 12, 0x2d4560);
        g.Rect(6, 18, 20, 1, 0x6184a3);
        g.Rect(6, 27, 20, 3, 0x1c2b3e);
        foreach (var x in (int[])[8, 14, 20])
            g.Rect(x, 21, 4, 3, 0x5aa9ff);
        // Шевроны вверх: нижний — тусклее, верхний — ярче.
        uint[] colors = [0x1f63d8, 0x5aa9ff, 0xe4f4ff];
        for (var i = 0; i < level; i++)
        {
            var y = 14 - i * 5;
            var color = colors[Math.Min(colors.Length - 1, i + 3 - level)];
            g.Line(9, y + 4, 16, y - 2, 4, ink);
            g.Line(16, y - 2, 23, y + 4, 4, ink);
            g.Line(9, y + 4, 16, y - 2, 2, color);
            g.Line(16, y - 2, 23, y + 4, 2, color);
        }
    }

    /// <summary>Облики плиток (16 вариантов) и портреты (кадр 32 × 32 и он же портретом).</summary>
    public static IEnumerable<(string Name, LookSpec Spec)> Looks()
    {
        foreach (var kind in Kinds)
        {
            var tile = kind;
            yield return ("paving/" + tile, new LookSpec((canvas, _, _) => DrawTile(canvas, tile, 0))
            {
                Width = Tile,
                Height = Tile,
                Origin = (0, 0),
                Shadow = null,
                Layer = LookLayer.Floor,
                Variants = 16,
                DrawCell = (canvas, variant, _, _) => DrawTile(canvas, tile, variant),
            });
        }
        foreach (var icon in IconNames)
        {
            var name = icon;
            yield return ("portraits/pave/" + name, Portrait32(canvas => DrawIcon(canvas, name)));
        }
        for (var level = 1; level <= 3; level++)
        {
            var steps = level;
            yield return ("portraits/level/" + steps, Portrait32(canvas => DrawLevel(canvas, steps)));
        }
    }

    private static LookSpec Portrait32(Action<Canvas> draw) =>
        new((canvas, _, _) => draw(canvas))
        {
            Width = 32,
            Height = 32,
            Origin = (0, 0),
            Shadow = null,
            Layer = LookLayer.Floor,
            Portrait = new PortraitSpec(32, 32, draw) { Origin = (0, 0) },
        };

    /// <summary>Сколько хозяев покрытия учтено в клетках слоя: код = хозяин × 2 + вид + 2 (PaveRules.Code).</summary>
    private const int Owners = 64;

    /// <summary>
    /// Клетки слоя paving: канал 0 хранит код покрытия (PaveRules.Code: хозяин × 2 + вид + 2, 0 — нет), по коду — облик плитки,
    /// фундамент и дорога любого хозяина. Плитки стыкуются с соседями (connect). Мост (дорога по болоту) в канале не виден:
    /// нужна земля под плиткой, он рисуется записью Sprite из эффектов, облик — paving/bridge.
    /// </summary>
    public static void Cells(ClientBuilder client)
    {
        var looks = new string[2 + Owners * 2];
        Array.Fill(looks, "");
        for (var owner = 0; owner < Owners; owner++)
        {
            looks[owner * 2 + 2] = "paving/foundation";
            looks[owner * 2 + 3] = "paving/road";
        }
        client.CellLook("paving", 0, true, looks);
    }
}
