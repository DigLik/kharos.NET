using System.Buffers.Binary;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Кадры эффектов, значки над зданиями, кольцо выделения, дропы и следы на земле — перенос combatPass.ts (взрыв, клубы дыма),
/// powerPass.ts (молнии, значки тревог), selectionPass.ts, dropsPass.ts и decalsPass.ts (гарь, щербинки, остовы) до байта.
/// Кадр рисуется в пикселях с началом в левом верхнем углу (Pixmap TS), цвета без метки тона. Имена обликов:
/// effects/blast (8 кадров 48²), effects/puff (3 клуба 16²), power/bolt (6 молний 32²), power/icon, power/off,
/// power/broke, power/blocked, power/lack (16 кадров по грузам Icons.Goods), power/white, selection/ring, selection/white,
/// drops/&lt;груз&gt; (1–4 ящика), decals/white, decals/scorch/&lt;8..48&gt; (3 варианта), decals/pock (3), decals/wreck/&lt;юнит&gt;
/// (16 направлений).
/// </summary>
internal static class Effects
{
    // --- Взрыв и дым (combatPass.ts) ---
    private const int BlastFrame = 48, BlastFrames = 8, PuffFrame = 16, PuffKinds = 3;
    private static readonly uint[] BlastColors = [0xfff6d0, 0xffe07a, 0xffb347, 0xff7a2a, 0xd9481c, 0x8a3a24, 0x55443e, 0x3a3432];

    private static double Noise(double a, double b) => ArtMath.Noise(a, b);

    /// <summary>Кадр взрыва: огненный шар из клубов растёт, темнеет и расходится кольцом дыма.</summary>
    public static void DrawBlast(Canvas g, int frame)
    {
        double center = BlastFrame / 2;
        var progress = frame / (double)(BlastFrames - 1);
        var radius = (center - 2) * (0.3 + 0.7 * Math.Sqrt(progress));
        const int clouds = 7;
        void Clouds(double share, double size, uint color)
        {
            for (var i = 0; i < clouds; i++)
            {
                var angle = i / (double)clouds * Math.PI * 2 + Noise(i, 3) * 0.8;
                var reach = radius * share * (0.75 + Noise(i, 5) * 0.25);
                g.Circle(ArtMath.Round(center + DetMath.Cos(angle) * reach), ArtMath.Round(center + DetMath.Sin(angle) * reach), radius * size, color);
            }
        }
        if (progress < 0.6)
        {
            // Огненный шар: тёмная кайма, огонь, светлая сердцевина.
            g.Circle(center, center, radius * 0.8, BlastColors[Math.Min(frame + 2, BlastFrames - 1)]);
            Clouds(0.6, 0.42, BlastColors[Math.Min(frame + 2, BlastFrames - 1)]);
            g.Circle(center, center, radius * 0.68, BlastColors[frame + 1]);
            Clouds(0.45, 0.32, BlastColors[frame + 1]);
            g.Circle(center, center, radius * 0.45, BlastColors[frame]);
        }
        else
        {
            // Дым: клубы расходятся, середина пустеет.
            Clouds(0.75, 0.34 * (1.4 - progress), BlastColors[frame]);
            Clouds(0.55, 0.2 * (1.4 - progress), BlastColors[frame - 1]);
        }
    }

    /// <summary>Клуб дыма: несколько кругов вразброс, светлые сверху и тёмные снизу; цвет задаёт оттенок спрайта.</summary>
    public static void DrawPuff(Canvas g, int kind)
    {
        double center = PuffFrame / 2;
        const int lobes = 5;
        foreach (var (shift, color) in ((double, uint)[])[(1, 0xa8a8ae), (0, 0xe6e6ea)])
        {
            g.Circle(center, center + shift, 3.5, color);
            for (var i = 0; i < lobes; i++)
            {
                var angle = i / (double)lobes * Math.PI * 2 + Noise(i, kind) * 1.2;
                var reach = 2.2 + Noise(i, kind + 7) * 1.4;
                g.Circle(ArtMath.Round(center + DetMath.Cos(angle) * reach), ArtMath.Round(center + DetMath.Sin(angle) * reach) + shift, 2 + Noise(i, kind + 13) * 1.2, color);
            }
        }
    }

    // --- Молнии и значки тревог (powerPass.ts) ---
    private const int BoltSize = 32, Bolts = 6, IconSize = 16;
    private const uint BoltGlow = 0x4f9dff, BoltCore = 0xf2fbff;

    /// <summary>Молния: ломаная от края к краю картинки с короткой веткой, светлая сердцевина в синем ореоле.</summary>
    public static void DrawBolt(Canvas g, int index)
    {
        const int joints = 6;
        var points = new double[(joints + 1) * 2];
        // Молнии идут то сверху вниз, то наискось: начало и конец на разных краях.
        var x = 4 + Noise(index, 1) * (BoltSize - 8);
        var endX = 4 + Noise(index, 2) * (BoltSize - 8);
        for (var i = 0; i <= joints; i++)
        {
            var along = i / (double)joints;
            x += (endX - x) * along * 0.5 + (Noise(index, 10 + i) - 0.5) * 12;
            x = Math.Max(2, Math.Min(BoltSize - 3, x));
            points[i * 2] = ArtMath.Round(x);
            points[i * 2 + 1] = ArtMath.Round(2 + along * (BoltSize - 5));
        }
        // Ветка от середины в сторону.
        var from = 2 * (1 + (int)Math.Floor(Noise(index, 3) * (joints - 2)));
        var side = Noise(index, 4) < 0.5 ? -1 : 1;
        double[] branch =
        [
            points[from], points[from + 1],
            points[from] + side * 5, points[from + 1] + 3,
            points[from] + side * 7, points[from + 1] + 8,
        ];
        foreach (var (line, width, color) in ((double[], double, uint)[])[(points, 3, BoltGlow), (branch, 2, BoltGlow), (points, 1, BoltCore), (branch, 1, BoltCore)])
            for (var i = 0; i + 3 < line.Length; i += 2)
                g.Line(line[i], line[i + 1], line[i + 2], line[i + 3], width, color);
    }

    /// <summary>Значок нехватки энергии: жёлтая молния на красном круге.</summary>
    public static void DrawIcon(Canvas g)
    {
        g.Circle(8, 8, 8, 0x0b111b).Circle(8, 8, 7, 0xc8321f).Circle(7.5, 7.5, 5.5, 0xe5492f);
        foreach (var (width, color) in ((double, uint)[])[(3, 0x0b111b), (1, 0xffe066)])
        {
            g.Line(10, 3, 6, 8, width, color);
            g.Line(6, 8, 10, 8, width, color);
            g.Line(10, 8, 6, 13, width, color);
        }
    }

    /// <summary>Значок «Выключено»: знак питания на сером круге. Не тревога — не мигает.</summary>
    public static void DrawOff(Canvas g)
    {
        g.Circle(8, 8, 8, 0x0b111b).Circle(8, 8, 7, 0x46505f).Circle(7.5, 7.5, 5.5, 0x5d6878);
        // Разомкнутое сверху кольцо и черта в разрыве.
        g.Ring(8, 8.5, 3.5, 1.4, 0xdfe6ee);
        g.Rect(6, 3, 4, 4, 0x5d6878);
        g.Rect(7, 3, 2, 6, 0xdfe6ee);
    }

    /// <summary>Значок «Не хватает груза»: мини-иконка груза на тёмном круге в красном кольце.</summary>
    public static void DrawLack(Canvas g, string good)
    {
        g.Circle(8, 8, 8, 0x0b111b).Circle(8, 8, 7, 0xe5492f).Circle(8, 8, 6, 0x121c2a);
        var shift = (IconSize - Icons.Size) / 2;
        Icons.Paint(good, (x, y, color) => g.Rect(shift + x, shift + y, 1, 1, color));
    }

    /// <summary>Значок «Не хватает кредитов»: золотая монета на тёмном круге в красном кольце.</summary>
    public static void DrawBroke(Canvas g)
    {
        g.Circle(8, 8, 8, 0x0b111b).Circle(8, 8, 7, 0xe5492f).Circle(8, 8, 6, 0x121c2a);
        g.Circle(8, 8, 4, 0x8a5a14).Circle(8, 8, 3.2, 0xf0c95a).Rect(7, 6, 2, 4, 0x8a5a14);
    }

    /// <summary>Значок «Не подъехать»: белая черта на красном круге.</summary>
    public static void DrawBlocked(Canvas g)
    {
        g.Circle(8, 8, 8, 0x0b111b).Circle(8, 8, 7, 0xc8321f).Circle(7.5, 7.5, 5.5, 0xe5492f);
        g.Rect(3, 6, 10, 4, 0x0b111b).Rect(4, 7, 8, 2, 0xf2f5f8);
    }

    // --- Дропы (dropsPass.ts) ---
    private const int Crates = 4;
    private static readonly (int X, int Y, int Side)[] Spots = [(4, 7, 7), (9, 3, 6), (1, 2, 5), (10, 10, 5)];

    private static uint Dim(uint color, double level) =>
        ((uint)ArtMath.Round(((color >> 16) & 255) * level) << 16) | ((uint)ArtMath.Round(((color >> 8) & 255) * level) << 8) | (uint)ArtMath.Round((color & 255) * level);

    /// <summary>Кучка ящиков: деревянные короба с полосой цвета груза, count штук.</summary>
    public static void DrawPile(Canvas g, string good, int count)
    {
        var band = Icons.ColorOf(good);
        for (var i = 0; i < count; i++)
        {
            var (x, y, side) = Spots[i];
            g.Rect(x, y, side, side, 0x1c1712);
            g.Rect(x + 1, y + 1, side - 2, side - 2, 0x7a5a36);
            g.Rect(x + 1, y + 1, side - 2, 1, 0x9c7646);
            // Полоса груза поперёк ящика и её тень.
            var middle = y + side / 2;
            g.Rect(x + 1, middle - 1, side - 2, 2, band);
            g.Rect(x + 1, middle + 1, side - 2, 1, Dim(band, 0.6));
        }
    }

    // --- Следы (decalsPass.ts) ---
    private static readonly int[] ScorchSizes = [8, 16, 24, 32, 40, 48];
    private const int Variants = 3, WreckDirections = 16, UnitFrame = 32;
    private static readonly uint[] Foe = [0xb3261e, 0xff6b5a, 0xffe9e4];

    /// <summary>Виды юнитов по порядку UNIT_TYPES: так лежат остовы в эталоне.</summary>
    public static readonly string[] UnitTypes =
    [
        "mcv", "builder", "infantry", "truck", "flamer", "rocketeer", "buggy", "flak", "lancer", "tank", "artillery", "tesla",
        "carrier", "supplier", "airTruck", "drone", "gunship", "bomber",
    ];

    /// <summary>Гарь: тёмная сердцевина и рваный край, который к краю редеет.</summary>
    public static void DrawScorch(Canvas g, int size, int variant)
    {
        double center = size / 2.0;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = x + 0.5 - center;
                var dy = y + 0.5 - center;
                // Край неровный: радиус гуляет по углу.
                var angle = DetMath.Atan2(dy, dx);
                var wobble = 0.86 + 0.07 * DetMath.Sin(angle * 3 + variant * 2.1) + 0.05 * DetMath.Sin(angle * 5 + variant * 4.3) + 0.04 * DetMath.Sin(angle * 11 + variant);
                var reach = DetMath.Hypot(dx, dy) / (center * wobble);
                var grain = ArtMath.Noise(x, y, variant + size);
                if (reach > 1 || grain > (1 - reach) * 2.2)
                    continue;
                var color = reach < 0.45 ? (grain < 0.3 ? 0x0c0a09u : 0x15110eu) : grain < 0.5 ? 0x231b14u : 0x30251bu;
                g.Rect(x, y, 1, 1, color);
            }
    }

    /// <summary>Щербинки от пуль: несколько тёмных пикселей вразброс.</summary>
    public static void DrawPock(Canvas g, int variant)
    {
        for (var i = 0; i < 4; i++)
        {
            var x = Math.Floor(1 + ArtMath.Noise(i, variant, 3) * 4);
            var y = Math.Floor(1 + ArtMath.Noise(i, variant, 5) * 4);
            g.Rect(x, y, 1, 1, i != 0 ? 0x241c15u : 0x120e0bu);
        }
    }

    /// <summary>
    /// Остов: юнит, нарисованный как обычно (цвета чужой стороны), обгорает до угля с ржавчиной и копотью, а края его
    /// обломаны. Турели лежат на своих местах, повёрнутые как попало. Пиксели читаются из записанного потока чертежа
    /// (<see cref="Stamp"/>) и ложатся обратно по одному.
    /// </summary>
    public static void DrawWreck(Canvas g, string type, int direction, int variant, IReadOnlyList<Mount> mounts)
    {
        var angle = (double)direction / WreckDirections * Math.PI * 2;
        var drawing = new LookSpec((canvas, _, _) =>
        {
            UnitArt.Unit(type).Draw(canvas, angle, Foe, 0);
            var (sin, cos) = ArtTrig.Of(angle);
            for (var i = 0; i < mounts.Count; i++)
            {
                var mount = mounts[i];
                // Стрелок багги не обгорает в остов.
                if (mount.Turret == "gunner")
                    continue;
                var x = (cos * mount.Along - sin * mount.Across) * 16;
                var y = (sin * mount.Along + cos * mount.Across) * 16;
                canvas.Translate(x, y);
                UnitArt.Turret(mount.Turret)(canvas, angle + (ArtMath.Noise(i, variant, 9) - 0.5) * 2.5, Foe, 0);
                canvas.Translate(-x, -y);
            }
        })
        { Width = UnitFrame, Height = UnitFrame, Shadow = null };
        var image = Stamp.Of(drawing.Encode(), UnitFrame, UnitFrame);
        var data = image.Data;
        const int width = UnitFrame, height = UnitFrame;
        bool Solid(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && data[(y * width + x) * 4 + 3] > 0;
        var edge = new bool[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                edge[y * width + x] = Solid(x, y) && (!Solid(x - 1, y) || !Solid(x + 1, y) || !Solid(x, y - 1) || !Solid(x, y + 1));
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = (y * width + x) * 4;
                if (data[at + 3] == 0)
                    continue;
                var grain = ArtMath.Noise(x, y, variant * 31 + direction);
                if (edge[y * width + x] && grain < 0.25)
                {
                    // Обломанный край: альфа 0, а цвет остаётся прежним (TS не стирает RGB) — метка тона 255 даёт альфу 0.
                    g.Pixel(x, y, 0xff000000u | ((uint)data[at] << 16) | ((uint)data[at + 1] << 8) | data[at + 2]);
                    continue;
                }
                // Светлое обгорает до серого угля, тёмное — до чёрного; кое-где ржавчина и копоть.
                var light = (data[at] + data[at + 1] + data[at + 2]) / (3.0 * 255);
                double r = 0x22 + light * 0x58, gr = 0x1e + light * 0x4c, b = 0x1a + light * 0x40;
                // Пятна ржавчины и копоти — крупные, по три пикселя: мелкая рябь съела бы силуэт.
                var spot = ArtMath.Noise(Math.Floor(x / 3.0), Math.Floor(y / 3.0), variant + direction * 7);
                if (spot > 0.9)
                    (r, gr, b) = (0x6a, 0x3c, 0x22);
                else if (spot < 0.1)
                    (r, gr, b) = (0x10, 0x0d, 0x0b);
                g.Pixel(x, y, ((uint)(int)r << 16) | ((uint)(int)gr << 8) | (uint)(int)b);
            }
    }

    /// <summary>Облики эффектов, значков, дропов и следов; mounts — турели юнита по виду для остовов.</summary>
    public static IEnumerable<(string Name, LookSpec Spec)> Looks(Func<string, IReadOnlyList<Mount>> mounts)
    {
        LookSpec Flat(int width, int height, int phases, Action<Canvas, int> draw, int directions = 1) =>
            new((canvas, angle, phase) => draw(canvas, directions > 1 ? (int)Math.Round(angle / Canvas.Turn * directions) : phase))
            {
                Width = width,
                Height = height,
                Origin = (0, 0),
                Directions = directions,
                Phases = phases,
                Shadow = null,
                Layer = LookLayer.Floor,
            };

        yield return ("effects/blast", Flat(BlastFrame, BlastFrame, BlastFrames, DrawBlast));
        yield return ("effects/puff", Flat(PuffFrame, PuffFrame, PuffKinds, DrawPuff));

        yield return ("decals/white", Flat(1, 1, 1, (g, _) => g.Rect(0, 0, 1, 1, 0xffffff)));
        foreach (var size in ScorchSizes)
        {
            var side = size;
            yield return ("decals/scorch/" + side, Flat(side, side, Variants, (g, variant) => DrawScorch(g, side, variant)));
        }
        yield return ("decals/pock", Flat(6, 6, Variants, DrawPock));
        foreach (var type in UnitTypes)
        {
            var unit = type;
            yield return ("decals/wreck/" + unit, Flat(UnitFrame, UnitFrame, 1, (g, direction) => DrawWreck(g, unit, direction, direction % Variants, mounts(unit)), WreckDirections));
        }

        yield return ("power/bolt", Flat(BoltSize, BoltSize, Bolts, DrawBolt));
        yield return ("power/icon", Flat(IconSize, IconSize, 1, (g, _) => DrawIcon(g)));
        yield return ("power/white", Flat(4, 4, 1, (g, _) => g.Rect(0, 0, 4, 4, 0xffffff)));
        yield return ("power/off", Flat(IconSize, IconSize, 1, (g, _) => DrawOff(g)));
        yield return ("power/lack", Flat(IconSize, IconSize, Icons.Goods.Length, (g, good) => DrawLack(g, Icons.Goods[good])));
        yield return ("power/broke", Flat(IconSize, IconSize, 1, (g, _) => DrawBroke(g)));
        yield return ("power/blocked", Flat(IconSize, IconSize, 1, (g, _) => DrawBlocked(g)));

        yield return ("selection/ring", Flat(64, 64, 1, (g, _) => g.Ring(32, 32, 29, 4, 0xffffff)));
        yield return ("selection/white", Flat(4, 4, 1, (g, _) => g.Rect(0, 0, 4, 4, 0xffffff)));

        foreach (var good in Icons.Goods)
        {
            var cargo = good;
            yield return ("drops/" + cargo, Flat(16, 16, Crates, (g, crates) => DrawPile(g, cargo, crates + 1)));
        }
    }

    /// <summary>
    /// Растр записанного потока чертежа: тот же Pixmap TS, что у клиента (Kharos.Client.Looks.Canvas), с началом в центре
    /// кадра. Остову нужны пиксели готового юнита, а холст мода только записывает команды.
    /// </summary>
    private sealed class Stamp
    {
        private readonly int _width, _height;
        private double _originX, _originY;
        public byte[] Data { get; }

        private Stamp(int width, int height)
        {
            (_width, _height) = (width, height);
            Data = new byte[width * height * 4];
            (_originX, _originY) = (width / 2.0, height / 2.0);
        }

        /// <summary>Кадр из потока облика (<see cref="LookSpec.Encode"/>) одного кадра без огней, теней и вариантов.</summary>
        public static Stamp Of(byte[] stream, int width, int height)
        {
            // Заголовок без начала, шага, теней, ламп, луча и портрета: ширина, высота, флаги, привязка, направления, фазы, кадры в секунду, слой, значок, лампы.
            const int header = 2 + 2 + 1 + 16 + 2 + 2 + 4 + 1 + 2 + 2;
            var stamp = new Stamp(width, height);
            var r = stream;
            var at = header;
            double F()
            {
                var v = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(r.AsSpan(at)));
                at += 8;
                return v;
            }
            uint U()
            {
                var v = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(at));
                at += 4;
                return v;
            }
            while (true)
            {
                var op = r[at++];
                switch (op)
                {
                    case 0:
                        return stamp;
                    case 1:
                    {
                        double x = F(), y = F(), w = F(), h = F();
                        stamp.Fill(x, y, x + w, y + h, U(), (_, _) => true);
                        break;
                    }
                    case 2:
                    {
                        double cx = F(), cy = F(), radius = F();
                        stamp.Fill(cx - radius, cy - radius, cx + radius, cy + radius, U(), (x, y) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius);
                        break;
                    }
                    case 3:
                    {
                        double cx = F(), cy = F(), radius = F(), w = F();
                        var outer = radius + w / 2;
                        stamp.Fill(cx - outer, cy - outer, cx + outer, cy + outer, U(), (x, y) => Math.Abs(DetMath.Hypot(x - cx, y - cy) - radius) <= w / 2);
                        break;
                    }
                    case 4:
                    {
                        double fx = F(), fy = F(), tx = F(), ty = F(), w = F();
                        var color = U();
                        double dx = tx - fx, dy = ty - fy;
                        var length = DetMath.Hypot(dx, dy);
                        if (length == 0 || double.IsNaN(length))
                            break;
                        var half = w / 2;
                        stamp.Fill(Math.Min(fx, tx) - half, Math.Min(fy, ty) - half, Math.Max(fx, tx) + half, Math.Max(fy, ty) + half, color, (x, y) =>
                        {
                            var along = ((x - fx) * dx + (y - fy) * dy) / length;
                            var across = ((x - fx) * dy - (y - fy) * dx) / length;
                            return along >= 0 && along <= length && Math.Abs(across) <= half;
                        });
                        break;
                    }
                    case 5:
                    {
                        var count = BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(at));
                        at += 2;
                        var points = new double[count * 2];
                        for (var i = 0; i < points.Length; i++)
                            points[i] = F();
                        var color = U();
                        if (count < 3)
                            break;
                        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
                        for (var i = 0; i < count; i++)
                        {
                            left = Math.Min(left, points[i * 2]);
                            right = Math.Max(right, points[i * 2]);
                            top = Math.Min(top, points[i * 2 + 1]);
                            bottom = Math.Max(bottom, points[i * 2 + 1]);
                        }
                        stamp.Fill(left, top, right, bottom, color, (x, y) =>
                        {
                            var inside = false;
                            for (int i = 0, j = count - 1; i < count; j = i++)
                            {
                                double xi = points[i * 2], yi = points[i * 2 + 1], xj = points[j * 2], yj = points[j * 2 + 1];
                                if (yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                                    inside = !inside;
                            }
                            return inside;
                        });
                        break;
                    }
                    case 6:
                    {
                        double x = F(), y = F();
                        stamp.Fill(Math.Floor(x), Math.Floor(y), Math.Floor(x) + 1, Math.Floor(y) + 1, U(), (_, _) => true);
                        break;
                    }
                    case 7:
                        stamp._originX += F();
                        stamp._originY += F();
                        break;
                    case 8:
                        at += 32;
                        break;
                    default:
                        throw new FormatException("остов: команда холста " + op);
                }
            }
        }

        private void Fill(double left, double top, double right, double bottom, uint color, Func<double, double, bool> inside)
        {
            byte red = (byte)(color >> 16), green = (byte)(color >> 8), blue = (byte)color, alpha = (byte)(255 - (color >> 24));
            var fromX = (int)Math.Max(0, Math.Ceiling(left + _originX - 0.5));
            var toX = (int)Math.Min(_width - 1, Math.Ceiling(right + _originX - 0.5) - 1);
            var fromY = (int)Math.Max(0, Math.Ceiling(top + _originY - 0.5));
            var toY = (int)Math.Min(_height - 1, Math.Ceiling(bottom + _originY - 0.5) - 1);
            for (var pixelY = fromY; pixelY <= toY; pixelY++)
                for (var pixelX = fromX; pixelX <= toX; pixelX++)
                {
                    if (!inside(pixelX + 0.5 - _originX, pixelY + 0.5 - _originY))
                        continue;
                    var offset = (pixelY * _width + pixelX) * 4;
                    Data[offset] = red;
                    Data[offset + 1] = green;
                    Data[offset + 2] = blue;
                    Data[offset + 3] = alpha;
                }
        }
    }
}
