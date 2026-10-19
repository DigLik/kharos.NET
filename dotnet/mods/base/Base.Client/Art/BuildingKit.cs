using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Чертежи зданий — перенос lib/buildings.luau и buildings/factory/look.luau (buildingArt.ts): ART_TILE пикселей на
/// тайл, как у местности; начало координат — левый верхний угол основания, высокие части уходят в y &lt; 0. Кадров в цикле
/// анимации — Frames, чертёж получает фазу цикла t от 0 до 1 и обязан замыкаться при t = 1. Огни кадра — Canvas.Light.
/// Здание стоит по левому верхнему тайлу (как в TS): точка привязки облика — левый верхний угол основания. Общий набор
/// чертежей зданий (пакет Б-А, docs/dotnet-n6.md Н6.5): сами виды — Art/Buildings/&lt;Вид&gt;.cs, их перечень —
/// <see cref="BuildingArt"/>. Полный чертёж пока есть у завода; остальным видам — заглушка по размеру основания и роли
/// (<see cref="Placeholder(BuildingInfo)"/>).
/// </summary>
internal static class BuildingKit
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

    /// <summary>Роль здания для заглушки: от неё тона плиты.</summary>
    public enum Role { Storage, Production, Defense, Power, Processing }

    // Тона заглушек по ролям (пять оттенков, как у Steel и Iron).
    private static readonly uint[] Brown = [0x2e1a14, 0x5e2f24, 0x7e4332, 0x9a523a, 0xc47a57];
    private static readonly uint[] Gold = [0x2a2208, 0x5a4a10, 0x8a7218, 0xb08e1c, 0xd9b021];
    private static readonly uint[] Green = [0x10261a, 0x1f4a33, 0x2e6a4a, 0x3f8a62, 0x6fb890];

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

    /// <summary>Чертёж с вариантом (соседство, ступень пуска, число ракет): холст, фаза цикла t, вариант, есть ли работа.</summary>
    public delegate void ArtVariant(Canvas g, double t, int variant, bool working);

    /// <summary>Облик здания width × height тайлов; working — есть ли у здания вид работы (тогда кадров вдвое больше).</summary>
    public static LookSpec Look(int width, int height, Art draw, bool working = false) =>
        Look(width, height, (g, t, _, w) => draw(g, t, w), 1, working);

    /// <summary>
    /// Облик здания с вариантами (LookVariants) и видом работы (LookWorking): ячейка — вариант × фазы, затем те же фазы
    /// «в работе» (framesOf в buildingsPass.ts). Портрет — вариант 0, первый кадр работы, с полем в 2 пикселя;
    /// portraitMounts дорисовывает на портрете башни креплений (drawMounts в portraits.ts); начало — угол основания.
    /// </summary>
    public static LookSpec Look(int width, int height, ArtVariant draw, int variants = 1, bool working = false, Action<Canvas>? portraitMounts = null) =>
        new((canvas, _, phase) => draw(canvas, phase % Frames / (double)Frames, 0, phase >= Frames))
        {
            Width = width * Tile + Pad * 2,
            Height = height * Tile + Pad * 2,
            Origin = (Pad, Pad),
            // Место сущности — левый верхний тайл основания.
            Anchor = (0, 0),
            Directions = 1,
            Phases = Frames,
            Working = working,
            Variants = variants,
            DrawCell = (canvas, variant, _, phase) => draw(canvas, phase % Frames / (double)Frames, variant, phase >= Frames),
            Fps = 8,
            Layer = LookLayer.Building,
            Shadow = (3, 0.4f),
            // Портрет: первый кадр работы с полем в 2 пикселя (buildingCanvas в portraits.ts).
            Portrait = new PortraitSpec(width * Tile + 4, height * Tile + 4, canvas =>
            {
                draw(canvas, 0, 0, true);
                portraitMounts?.Invoke(canvas);
            }) { Origin = (2, 2) },
        };

    // ---- Общие помощники чертежей (Б-А; Б-Б берёт их отсюда же). Только дополняются, не меняются. ----

    /// <summary>Math.round из JS: к ближайшему целому, половина — вверх (для отрицательных тоже, −1,5 → −1).</summary>
    public static double Round(double value) => Math.Floor(value + 0.5);

    /// <summary>Тона от тёмного к светлому (пять оттенков).</summary>
    public static readonly uint[] White = [0x6d7680, 0x9aa4ae, 0xc3cbd3, 0xe1e7ec, 0xffffff];

    /// <summary>Огонь печи: от тёмно-красного к белому.</summary>
    public static readonly uint[] Fire = [0x6a1a10, 0xc2401a, 0xf08a2a, 0xffd36a, 0xfff4d0];

    public static uint FireColor(double level) => Fire[(int)Math.Min(Fire.Length - 1, Math.Max(0, Math.Floor(level * Fire.Length)))];

    /// <summary>Огнеупорный кирпич печей.</summary>
    public static readonly uint[] Brick = [0x2a1a16, Rust[0], Rust[1], Rust[2], 0xe0a080];

    /// <summary>Электрическая дуга: от синего к белому.</summary>
    public static readonly uint[] Arc = [0x2f5fd0, 0x8fc0ff, 0xf2f8ff];

    /// <summary>Материя, которую выдаёт генератор: слитки цвета кредитов.</summary>
    public static readonly uint[] Gold3 = [0xc9962b, 0xf0c95a, 0xfff0b0];

    /// <summary>Бетон реактора, шахты и перехватчика.</summary>
    public static readonly uint[] Concrete = [0x4f4c47, 0x77736b, 0x9d988e, 0xbfb9ae, 0xe0dbd1];

    /// <summary>Цвет груза (GOOD_COLORS в resourceColors.ts): для окраски частей здания.</summary>
    public static class GoodColor
    {
        public const uint Metal = 0xb8c4d0, Silicon = 0x7f9cff, Fuel = 0xffc93c, Kharite = 0xc06bff, Lithium = 0xf59ac0,
            Uranium = 0xb4e84a, Blocks = 0xd8b98a, Ammo = 0xe0803a, Parts = 0x4fd6a2, Rods = 0x7ef0b0,
            MetalOre = 0x8d7a5f, SiliconOre = 0x5a6a8a, FuelOre = 0x8a7a2a, KhariteOre = 0x7a4a9a, LithiumOre = 0x9a6a7e,
            UraniumOre = 0x6a8a3a;
    }

    /// <summary>Три тона цвета: тёмный, основной, светлый — для окраски частей здания цветом его груза (accent в buildingArt.ts).</summary>
    public static uint[] Accent(uint color)
    {
        uint Mix(double amount)
        {
            uint Channel(int shift)
            {
                var value = (double)((color >> shift) & 255);
                return (uint)Round(amount > 0 ? value + (255 - value) * amount : value * (1 + amount)) << shift;
            }
            return Channel(16) | Channel(8) | Channel(0);
        }
        return [Mix(-0.55), Mix(-0.15), Mix(0.3)];
    }

    /// <summary>Воспроизводимое случайное число от 0 до 1 по двум целым (hash в buildingArt.ts).</summary>
    public static double Hash(double a, double b)
    {
        var value = Math.Sin(a * 127.1 + b * 311.7) * 43758.5453;
        return value - Math.Floor(value);
    }

    /// <summary>Труба с обводкой: горизонтальная, длиной w, осью по строке y (pipe в buildingArt.ts).</summary>
    public static void Conduit(Canvas g, double x, double y, double w)
    {
        g.Rect(x, y - 1, w, 3, Ink);
        g.Rect(x, y, w, 1, Rust[1]);
    }

    /// <summary>Окна цвета команды в ряд: каждое 3×2 с бликом.</summary>
    public static void Windows(Canvas g, ReadOnlySpan<double> xs, double y)
    {
        foreach (var x in xs)
        {
            g.Rect(x, y, 3, 2, Team[1]);
            g.Rect(x, y, 1, 1, Team[2]);
        }
    }

    /// <summary>Заглушка по основанию и роли из данных (<see cref="BuildingInfo"/>).</summary>
    public static LookSpec Placeholder(BuildingInfo info) => Placeholder(info.Width, info.Height, info.Role);

    /// <summary>
    /// Заглушка здания width × height тайлов: плита тонов роли (производство — сталь, оборона — бурый, энергия — золотой,
    /// переработка — зелёный, склады и прочее — серый) с огнями цвета стороны по углам. Один кадр, без анимации.
    /// </summary>
    public static LookSpec Placeholder(int width, int height, Role role)
    {
        var tones = role switch { Role.Production => Steel, Role.Defense => Brown, Role.Power => Gold, Role.Processing => Green, _ => Iron };
        int w = width * Tile, h = height * Tile;
        void Draw(Canvas g)
        {
            Slab(g, 0, 0, w, h, Math.Min(4, h / 4), tones);
            // Огни стороны: перекрашиваются палитрой (тон основной); по размеру — два или четыре.
            var lamp = Canvas.Toned(Team[2], 1);
            g.Rect(3, 3, 2, 2, lamp);
            g.Rect(w - 5, 3, 2, 2, lamp);
            if (height > 1)
            {
                g.Rect(3, h - 9, 2, 2, lamp);
                g.Rect(w - 5, h - 9, 2, 2, lamp);
            }
        }
        return new LookSpec((canvas, _, _) => Draw(canvas))
        {
            Width = w + Pad * 2,
            Height = h + Pad * 2,
            Origin = (Pad, Pad),
            Anchor = (0, 0),
            Layer = LookLayer.Building,
            Shadow = (3, 0.4f),
            Portrait = new PortraitSpec(w + 4, h + 4, Draw) { Origin = (2, 2) },
        };
    }
}
