using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Чертежи зданий — перенос lib/buildings.luau и buildings/factory/look.luau (buildingArt.ts): ART_TILE пикселей на
/// тайл, как у местности; начало координат — левый верхний угол основания, высокие части уходят в y &lt; 0. Кадров в цикле
/// анимации — Frames, чертёж получает фазу цикла t от 0 до 1 и обязан замыкаться при t = 1. Огни кадра — Canvas.Light.
/// </summary>
internal static class BuildingArt
{
    public const int Tile = 16;
    public const int Frames = 16;

    /// <summary>Поля вокруг основания в пикселях спрайта: место для высоких частей.</summary>
    public const int Pad = 16;

    // Тона от тёмного к светлому. Свет падает слева сверху.
    public const uint Ink = 0x0b111b;
    public static readonly uint[] Steel = [0x1c2b3e, 0x2d4560, 0x41617f, 0x6184a3, 0x9bb9d1];
    public static readonly uint[] Iron = [0x1e2024, 0x3a3d43, 0x5b5f66, 0x8b9097, 0xc3c7cc];
    public static readonly uint[] Rust = [0x5e2f24, 0x9a523a, 0xc47a57];

    /// <summary>Цвет команды: от погашенного огня к горящему.</summary>
    public static readonly uint[] Team = [0x10358f, 0x1f63d8, 0x5aa9ff, 0xe4f4ff];

    public static readonly uint[] Hazard = [0x1a1608, 0xd9b021];
    public const uint Dark = 0x04070b;

    private const double Turn = Math.PI * 2;

    /// <summary>Чертёж здания: холст, фаза цикла t от 0 до 1, есть ли работа.</summary>
    public delegate void Art(Canvas g, double t, bool working);

    /// <summary>Плавное мигание: 1 при t = offset, 0 через полцикла.</summary>
    public static double Pulse(double t, double offset = 0) => 0.5 + 0.5 * Math.Cos(Turn * (t - offset));

    /// <summary>Бегущий огонь: вспыхивает при t = offset и быстро гаснет.</summary>
    public static double Chase(double t, double offset)
    {
        var phase = ((t - offset) % 1 + 1) % 1;
        return Math.Max(0, 1 - phase * 3);
    }

    public static uint TeamColor(double level) => Team[(int)Math.Min(3, Math.Floor(level * 4))];

    /// <summary>Плита с обводкой, фаской и передней стенкой высотой wall. Размер h — вместе со стенкой.</summary>
    public static void Slab(Canvas g, double x, double y, double w, double h, double wall, uint[] tones)
    {
        var top = h - wall - 2;
        g.Rect(x, y, w, h, Ink);
        g.Rect(x + 1, y + 1, w - 2, top, tones[1]);
        g.Rect(x + 1, y + 1, w - 2, 1, tones[3]);
        g.Rect(x + 1, y + 2, 1, top - 1, tones[2]);
        g.Rect(x + w - 2, y + 2, 1, top - 1, tones[0]);
        g.Rect(x + 1, y + 1 + top, w - 2, wall, tones[0]);
    }

    /// <summary>Купол: тон светлеет к левому верхнему краю.</summary>
    public static void Dome(Canvas g, double cx, double cy, double r, uint[] tones)
    {
        g.Circle(cx, cy, r + 1, Ink);
        g.Circle(cx, cy, r, tones[0]);
        g.Circle(cx - r * 0.12, cy - r * 0.12, r * 0.82, tones[1]);
        g.Circle(cx - r * 0.28, cy - r * 0.28, r * 0.52, tones[2]);
        if (r >= 4)
            g.Circle(cx - r * 0.42, cy - r * 0.42, r * 0.2, tones[3]);
    }

    /// <summary>Круглая башня: основание в (cx, cy), купол поднят на height.</summary>
    public static void Tower(Canvas g, double cx, double cy, double r, double height, uint[] tones)
    {
        g.Circle(cx, cy, r + 1, Ink);
        g.Rect(cx - r - 1, cy - height, r * 2 + 2, height, Ink);
        g.Circle(cx, cy, r, tones[0]);
        g.Rect(cx - r, cy - height, r * 2, height, tones[0]);
        g.Rect(cx - r + 1, cy - height, Math.Max(1, Math.Floor(r / 2)), height + 1, tones[1]);
        Dome(g, cx, cy - height, r, tones);
    }

    /// <summary>Круглая лампа цвета команды; светит на землю.</summary>
    public static void Lamp(Canvas g, double cx, double cy, double r, double level)
    {
        g.Light(cx, cy, r, level);
        g.Circle(cx, cy, r + 1, Ink);
        g.Circle(cx, cy, r, TeamColor(level * 0.74));
        g.Circle(cx - r * 0.3, cy - r * 0.3, r * 0.5, TeamColor(0.26 + level * 0.74));
    }

    /// <summary>Огонёк 2×2 с левым верхним углом в (x, y).</summary>
    public static void Bulb(Canvas g, double x, double y, double level)
    {
        g.Rect(x, y, 2, 2, TeamColor(level));
        g.Light(x + 1, y + 1, 1, level);
    }

    /// <summary>Облик здания width × height тайлов; working — есть ли у здания вид работы (тогда кадров вдвое больше).</summary>
    public static LookSpec Look(int width, int height, Art draw, bool working = false) =>
        new((canvas, _, phase) => draw(canvas, phase % Frames / (double)Frames, phase >= Frames))
        {
            Width = width * Tile + Pad * 2,
            Height = height * Tile + Pad * 2,
            Origin = (Pad, Pad),
            // Место сущности — центр основания.
            Anchor = (width * Tile / 2.0, height * Tile / 2.0),
            Directions = 1,
            Phases = working ? Frames * 2 : Frames,
            Fps = 8,
            Layer = LookLayer.Building,
            Shadow = (3, 0.4f),
            // Портрет: первый кадр работы с полем в 2 пикселя (buildingCanvas в portraits.ts).
            Portrait = new PortraitSpec(width * Tile + 4, height * Tile + 4, canvas => draw(canvas, 0, true)) { Origin = (2, 2) },
        };

    /// <summary>
    /// Машинный завод, 3×3 — перенос factory из buildingArt.ts: сборочный цех под ребристой крышей с мостовым краном,
    /// ворота на юг и двор с пандусом, где стоит выкаченная машина. Вентилятор на крыше крутится, огни над воротами бегут.
    /// </summary>
    public static LookSpec Factory() => Look(3, 3, (g, t, _) =>
    {
        Slab(g, 0, 2, 48, 46, 3, Steel);

        // Двор: бетон с разметкой и пандус от ворот.
        g.Rect(3, 30, 42, 14, Iron[1]);
        g.Rect(3, 30, 42, 1, Iron[0]);
        for (var x = 5; x <= 43; x += 6)
            g.Rect(x, 42, 3, 1, Hazard[1]);
        g.Rect(16, 30, 16, 12, Iron[0]);
        for (var y = 32; y <= 41; y += 3)
            g.Rect(17, y, 14, 1, Iron[2]);
        // Выкаченная машина на пандусе: корпус, гусеницы, башня.
        g.Rect(19, 33, 10, 8, Ink);
        g.Rect(19, 33, 2, 8, Iron[0]);
        g.Rect(27, 33, 2, 8, Iron[0]);
        g.Rect(21, 34, 6, 6, Team[1]);
        g.Rect(21, 34, 6, 1, Team[2]);
        g.Circle(24, 37, 2, Steel[3]);
        g.Rect(24, 31, 1, 4, Ink);

        // Цех: длинный корпус с рёбрами крыши, по которой ездит кран.
        Slab(g, 2, 0, 44, 30, 4, Iron);
        for (var x = 5; x <= 41; x += 4)
        {
            g.Rect(x, 2, 1, 22, Iron[0]);
            g.Rect(x + 1, 2, 1, 22, Iron[2]);
        }
        // Мостовой кран: балка поперёк цеха ходит вдоль него туда и обратно.
        var crane = Math.Round(8 + 28 * (0.5 - 0.5 * Math.Cos(Turn * t)), MidpointRounding.AwayFromZero);
        g.Rect(crane - 1, 2, 4, 22, Ink);
        g.Rect(crane, 2, 2, 22, Hazard[1]);
        g.Rect(crane - 1, 11, 4, 4, Iron[3]);
        // Ворота в торце цеха.
        g.Rect(16, 23, 16, 7, Ink);
        g.Rect(17, 24, 14, 6, Dark);
        for (var i = 0; i <= 6; i++)
            g.Rect(17 + i * 2, 22, 2, 1, TeamColor(Chase(t, i / 7.0)));

        // Вентилятор на крыше: у крыльчатки четыре лопасти, четверть оборота замыкает цикл.
        g.Circle(39, 8, 5, Ink);
        g.Circle(39, 8, 4, Iron[0]);
        var angle = t * Turn * 0.25;
        foreach (var turn in (ReadOnlySpan<double>)[angle, angle + Turn / 4])
        {
            double dx = Math.Cos(turn) * 3.5, dy = Math.Sin(turn) * 3.5;
            g.Line(39 - dx, 8 - dy, 39 + dx, 8 + dy, 1.2, Iron[3]);
        }
        Lamp(g, 7, 36, 2, Pulse(t));
        Lamp(g, 41, 36, 2, Pulse(t, 0.5));
    });
}
