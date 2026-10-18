using Kharos.Engine.Terrain;

[assembly: AssemblyFixture(typeof(Kharos.Gpu.Tests.GpuFixture))]
// Одно устройство на все тесты, ошибки WebGPU собираются на нём — тесты по очереди.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Kharos.Gpu.Tests;

/// <summary>Устройство без окна на всю сборку тестов; нет адаптера (CI без видеокарты) — тесты пропускаются.</summary>
public sealed class GpuFixture : IDisposable
{
    public GpuContext? Gpu { get; }
    public string Reason { get; }

    public GpuFixture()
    {
        Gpu = GpuContext.TryCreateHeadless(out var reason);
        Reason = reason;
    }

    /// <summary>Устройство или пропуск теста с причиной.</summary>
    public GpuContext Require()
    {
        if (Gpu is null) Assert.Skip($"нет адаптера WebGPU: {Reason}");
        return Gpu!;
    }

    public void Dispose() => Gpu?.Dispose();
}

/// <summary>Картинка кадра: пиксели, яркость, сохранение в dotnet/.cache/gpu.</summary>
public sealed class Image(byte[] pixels, int width, int height)
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;

    public static Image Of(Renderer renderer) => new(renderer.ReadPixels(), renderer.Width, renderer.Height);

    public (byte R, byte G, byte B) this[int x, int y]
    {
        get
        {
            int at = (y * Width + x) * 4;
            return (Pixels[at], Pixels[at + 1], Pixels[at + 2]);
        }
    }

    public static float Luma((byte R, byte G, byte B) c) => 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;

    /// <summary>Средняя яркость прямоугольника [x0, x1) × [y0, y1).</summary>
    public float Mean(int x0, int y0, int x1, int y1)
    {
        double sum = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) sum += Luma(this[x, y]);
        return (float)(sum / ((x1 - x0) * (y1 - y0)));
    }

    public float Mean() => Mean(0, 0, Width, Height);

    public int DistinctColors()
    {
        var set = new HashSet<int>();
        for (int i = 0; i < Pixels.Length; i += 4) set.Add(Pixels[i] | Pixels[i + 1] << 8 | Pixels[i + 2] << 16);
        return set.Count;
    }

    public string Save(string name)
    {
        var path = Path.Combine(Shots.Directory, name + ".png");
        Png.Write(path, Pixels, Width, Height);
        return path;
    }
}

public static class Shots
{
    /// <summary>dotnet/.cache/gpu — рядом с Kharos.slnx.</summary>
    public static string Directory { get; } = Find();

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Kharos.slnx"))) return Path.Combine(dir.FullName, ".cache", "gpu");
        return Path.Combine(AppContext.BaseDirectory, "gpu");
    }
}

/// <summary>Сцена для кадров: атлас, палитра, земля.</summary>
public static class Scene
{
    public const int AtlasSize = 64;

    /// <summary>Палитра сторон: своя синяя, чужая красная, союзная зелёная, ничья серая — тёмный, основной, светлый.</summary>
    public static readonly byte[] PaletteBytes =
    [
        0, 0, 120, 30, 60, 210, 110, 150, 255,
        120, 0, 0, 210, 40, 30, 255, 140, 110,
        0, 90, 0, 40, 180, 50, 140, 240, 120,
        60, 60, 60, 120, 120, 120, 200, 200, 200,
    ];

    public static void Palette(Frame frame)
    {
        for (int i = 0; i < PaletteBytes.Length; i++) frame.Palette[i] = PaletteBytes[i] / 255f;
    }

    public static (byte R, byte G, byte B) PaletteColor(int team, int tone) =>
        (PaletteBytes[(team * 3 + tone - 1) * 3], PaletteBytes[(team * 3 + tone - 1) * 3 + 1], PaletteBytes[(team * 3 + tone - 1) * 3 + 2]);

    public static readonly (byte R, byte G, byte B) Outline = (20, 20, 20);
    public static readonly (byte R, byte G, byte B) Red = (200, 30, 30);
    public static readonly (byte R, byte G, byte B) Wall = (90, 90, 100);

    /// <summary>
    /// Атлас 64²: клетка (0, 0) 16² — машинка: контур, светлый тон 3 сверху, основной 2, тёмный 1 снизу, красная фара
    /// в (7, 7); клетка (16, 0) 32² — здание. Тон — в альфе: 254 − (тон − 1).
    /// </summary>
    public static byte[] Atlas()
    {
        var pixels = new byte[AtlasSize * AtlasSize * 4];
        void Set(int x, int y, (byte R, byte G, byte B) c, byte a)
        {
            int at = (y * AtlasSize + x) * 4;
            (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = (c.R, c.G, c.B, a);
        }
        for (int y = 2; y < 14; y++)
        {
            for (int x = 2; x < 14; x++)
            {
                if (x == 2 || x == 13 || y == 2 || y == 13) Set(x, y, Outline, 255);
                else if (y < 6) Set(x, y, (128, 128, 128), 252);
                else if (y < 10) Set(x, y, (128, 128, 128), 253);
                else Set(x, y, (128, 128, 128), 254);
            }
        }
        Set(7, 7, Red, 255);
        for (int y = 0; y < 32; y++)
            for (int x = 16; x < 48; x++) Set(x, y, y is >= 4 and < 8 ? ((byte)128, (byte)128, (byte)128) : Wall, y is >= 4 and < 8 ? (byte)253 : (byte)255);
        return pixels;
    }

    public static (float U0, float V0, float U1, float V1) Buggy => (0, 0, 16f / AtlasSize, 16f / AtlasSize);
    public static (float U0, float V0, float U1, float V1) Building => (16f / AtlasSize, 0, 48f / AtlasSize, 32f / AtlasSize);

    /// <summary>Спрайт в тайлах мира: x, y — левый верхний угол.</summary>
    public static void Sprite(Frame frame, int layer, float x, float y, float w, float h, (float U0, float V0, float U1, float V1) uv,
        int team, float shadowShift = 0, float shadow = 0, bool grey = false)
    {
        var view = frame.View;
        frame.SpriteLayers[layer].AddRange([x - view.X, y - view.Y, w, h, uv.U0, uv.V0, uv.U1, uv.V1, team, shadowShift, shadow, grey ? 1 : 0]);
    }

    public static Frame Frame(float x, float y, float zoom, int width, int height, float scale = 1)
    {
        var frame = new Frame { View = new View(x, y, zoom, width / scale, height / scale, scale) };
        Palette(frame);
        return frame;
    }

    public static Land DefaultLand() => new(GeneratorConfig.Default);

    public static void Use(Renderer renderer, Land land) => renderer.SetLand((x, y) => land.GetChunk(x, y), land.ChunkRevision);

    /// <summary>Место с горами, скалами, болотом и песком рядом — для обзорных кадров.</summary>
    public static (float X, float Y) Interesting(Land land)
    {
        (float, float) best = (0, 0);
        int bestScore = -1;
        Span<int> counts = stackalloc int[4];
        for (int cy = -8; cy <= 8; cy++)
        {
            for (int cx = -8; cx <= 8; cx++)
            {
                counts.Clear();
                for (int y = -10; y < 10; y += 2)
                    for (int x = -18; x < 18; x += 2) counts[(int)land.TerrainAt(cx * 48 + x, cy * 48 + y)]++;
                int score = 0;
                foreach (var count in counts) score += Math.Min(count, 25);
                if (counts[3] > 0) score += 20;
                if (score > bestScore) (bestScore, best) = (score, (cx * 48 + 0.5f, cy * 48 + 0.5f));
            }
        }
        return best;
    }
}
