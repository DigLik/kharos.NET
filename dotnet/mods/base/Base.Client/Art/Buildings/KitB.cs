using Kharos.Mod.Sdk.Client;

namespace Base.Client.Buildings;

/// <summary>
/// Общее для чертежей пакета Б-Б (BuildingKit.cs — Б-А): сборка облика с вариантами и турелью на портрете, цвета грузов
/// (GOOD_COLORS из resourceColors.ts), три тона цвета груза (accent), палитры золота, огня и дуги из buildingArt.ts.
/// </summary>
internal static class KitB
{
    public const double Turn = Math.PI * 2;

    /// <summary>Чертёж: холст, фаза цикла t, работа, вариант.</summary>
    public delegate void Art(Canvas g, double t, bool working, int variant);

    public static readonly uint[] Gold = [0xc9962b, 0xf0c95a, 0xfff0b0];
    public static readonly uint[] Fire = [0x6a1a10, 0xc2401a, 0xf08a2a, 0xffd36a, 0xfff4d0];
    public static readonly uint[] Arc = [0x2f5fd0, 0x8fc0ff, 0xf2f8ff];

    public const uint Metal = 0xb8c4d0, Silicon = 0x7f9cff, Fuel = 0xffc93c, Kharite = 0xc06bff, Lithium = 0xf59ac0,
        Uranium = 0xb4e84a, Blocks = 0xd8b98a, Ammo = 0xe0803a, Parts = 0x4fd6a2, Rods = 0x7ef0b0;

    /// <summary>Цвета руд в порядке ores.json (metal, silicon, fuel, kharite, lithium, uranium).</summary>
    public static readonly uint[] OreColors = [0x8d7a5f, 0x5a6a8a, 0x8a7a2a, 0x7a4a9a, 0x9a6a7e, 0x6a8a3a];

    /// <summary>Math.round из JS: половинки — к плюс бесконечности.</summary>
    public static double Round(double value) => Math.Floor(value + 0.5);

    /// <summary>Три тона цвета: тёмный, основной, светлый — для окраски частей здания цветом его груза.</summary>
    public static uint[] Accent(uint color)
    {
        uint Mix(double amount)
        {
            uint Channel(int shift)
            {
                var value = (int)((color >> shift) & 255);
                var mixed = amount > 0 ? value + (255 - value) * amount : value * (1 + amount);
                return (uint)Round(mixed) << shift;
            }
            return Channel(16) | Channel(8) | Channel(0);
        }
        return [Mix(-0.55), Mix(-0.15), Mix(0.3)];
    }

    /// <summary>
    /// Облик здания width × height тайлов: ART_FRAMES кадров цикла (с working — ещё столько же «в работе»), variants
    /// вариантов; mount — вид турели на портрете (для зданий с data.mounts). Портрет — как buildingCanvas: вариант 0, работа.
    /// </summary>
    public static LookSpec Look(int width, int height, Art draw, bool working = false, int variants = 1, string? mount = null) =>
        new((canvas, _, phase) => draw(canvas, phase % BuildingKit.Frames / (double)BuildingKit.Frames, phase >= BuildingKit.Frames, 0))
        {
            Width = width * BuildingKit.Tile + BuildingKit.Pad * 2,
            Height = height * BuildingKit.Tile + BuildingKit.Pad * 2,
            Origin = (BuildingKit.Pad, BuildingKit.Pad),
            Anchor = (0, 0),
            Directions = 1,
            Phases = BuildingKit.Frames,
            Working = working,
            Variants = variants,
            DrawCell = (canvas, variant, _, phase) => draw(canvas, phase % BuildingKit.Frames / (double)BuildingKit.Frames, phase >= BuildingKit.Frames, variant),
            Fps = 8,
            Layer = LookLayer.Building,
            Shadow = (3, 0.4f),
            Portrait = new PortraitSpec(width * BuildingKit.Tile + 4, height * BuildingKit.Tile + 4, canvas =>
            {
                draw(canvas, 0, true, 0);
                if (mount is null)
                    return;
                // Поле портрета — 2 пикселя (PAD в portraits.ts). Башня стоит в центре основания, угол портрета — вправо вверх (drawMounts в portraits.ts).
                var (dx, dy) = (width * BuildingKit.Tile / 2.0, height * BuildingKit.Tile / 2.0);
                canvas.Translate(dx, dy);
                UnitArt.Turret(mount)(canvas, -Math.PI / 4, [0x1f63d8, 0x5aa9ff, 0xe4f4ff], 0);
                canvas.Translate(-dx, -dy);
            })
            { Origin = (2, 2) },
        };

    /// <summary>Основание оборонительной турели; само вращающееся оружие рисуется поверх отдельной сущностью.</summary>
    public static void Emplacement(Canvas g, double t, uint band)
    {
        BuildingKit.Slab(g, 0, 1, 16, 15, 2, BuildingKit.Steel);
        g.Circle(8, 8, 6, BuildingKit.Ink);
        g.Circle(8, 8, 5, BuildingKit.Iron[0]);
        g.Ring(8, 8, 4, 1, band);
        g.Circle(8, 8, 2, BuildingKit.Iron[2]);
        BuildingKit.Bulb(g, 1, 2, BuildingKit.Pulse(t));
        BuildingKit.Bulb(g, 13, 2, BuildingKit.Pulse(t, 0.5));
    }
}
