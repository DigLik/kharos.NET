using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;

namespace Kharos.Gpu.Tests;

/// <summary>Допуск сверки кадра с TS (docs/dotnet-n6.md, Н6.4, уровень 2).</summary>
/// <param name="OverShare">Доля пикселей вне земли, где какой-либо канал отличается больше чем на <paramref name="ChannelDelta"/>.</param>
/// <param name="ChannelDelta">Порог разницы канала (0..255).</param>
/// <param name="MeanDelta">Средняя |Δ| по каналам RGB для пикселей вне земли.</param>
/// <param name="TerrainDistance">Земля, помеченная маской, — не по пикселям, а по долям палитры: полное расстояние вариации
/// между распределениями цветов (0 — одинаковые, 1 — ничего общего); цвета огрублены до <paramref name="PaletteBits"/> старших бит канала.</param>
public sealed record ParityLimits(double OverShare = 0.005, int ChannelDelta = 24, double MeanDelta = 2.0, double TerrainDistance = 0.10, int PaletteBits = 6)
{
    public static ParityLimits Default { get; } = new();

    /// <summary>Ослабленный допуск для macOS в CI (программного адаптера там нет, кадры идут на видеокарте): сверка идёт, ворота шире.</summary>
    public static ParityLimits Relaxed { get; } = new(OverShare: 0.03, ChannelDelta: 24, MeanDelta: 6.0, TerrainDistance: 0.25, PaletteBits: 5);
}

/// <summary>Итог сверки двух кадров.</summary>
public sealed record ParityResult(int Width, int Height, int Compared, int Over, double OverShare, double MeanDelta, int TerrainPixels, double TerrainDistance, string[] Files)
{
    public bool Passed(ParityLimits limits) =>
        OverShare <= limits.OverShare && MeanDelta <= limits.MeanDelta && (TerrainPixels == 0 || TerrainDistance <= limits.TerrainDistance);

    public string Describe(ParityLimits limits) =>
        $"{Width}x{Height}: пикселей вне земли {Compared}, с Δ канала > {limits.ChannelDelta}: {Over} ({OverShare:P2}, допуск {limits.OverShare:P2}), " +
        $"средняя |Δ| {MeanDelta:F2} (допуск {limits.MeanDelta:F2}), земля {TerrainPixels} px, расстояние палитры {TerrainDistance:F3} (допуск {limits.TerrainDistance:F3}); картинки: {string.Join(", ", Files)}";
}

/// <summary>Описание сцены из Frames/golden/scenes.json (его пишет dotnet/tools/golden/frames.ts). Поля — как в frames/scenes.ts.</summary>
public sealed class ParityScene
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Seed { get; set; }
    public int Size { get; set; }
    public bool Fog { get; set; }
    public int Player { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public ParityCamera Camera { get; set; } = new();
    public ParityWeather Weather { get; set; } = new();
    public double Time { get; set; }
    public List<ParityEntity> Entities { get; set; } = [];
    public List<int[]> Allies { get; set; } = [];
    public List<JsonElement> Commands { get; set; } = [];
    public int AdvanceTicks { get; set; }
    public int[] Selected { get; set; } = [];
    public int[][] TerrainRegions { get; set; } = [];
    public bool Hud { get; set; }
    public int Biome { get; set; }
}

public sealed class ParityCamera
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Zoom { get; set; }
}

public sealed class ParityWeather
{
    public double Hour { get; set; }
    public double Light { get; set; }
    public double WindX { get; set; }
    public double WindY { get; set; }
    public double Precipitation { get; set; }
}

public sealed class ParityEntity
{
    public string Kind { get; set; } = "";
    public string Type { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Owner { get; set; }
}

/// <summary>
/// Сверка кадра .NET с кадром TS: пиксели вне земли — с допуском по разнице каналов и средней разнице, земля (прямоугольники
/// terrainRegions сцены, где нет ничего, кроме местности) — по долям палитры, как TerrainFidelityTests. Картинки пишутся в
/// dotnet/.cache/frames: &lt;сцена&gt;-ts.png, &lt;сцена&gt;-net.png, &lt;сцена&gt;-diff.png (разница, усиленная вчетверо; красным — пиксели вне допуска).
/// </summary>
public static class Parity
{
    /// <summary>Папка dotnet рядом с Kharos.slnx.</summary>
    public static string DotnetDirectory { get; } = FindDotnet();

    /// <summary>dotnet/tests/Kharos.Client.Tests/Frames/golden — эталоны кадров TS.</summary>
    public static string GoldenDirectory { get; } = Path.Combine(DotnetDirectory, "tests", "Kharos.Client.Tests", "Frames", "golden");

    /// <summary>dotnet/.cache/frames — кадры .NET и разница для просмотра.</summary>
    public static string CacheDirectory { get; } = Path.Combine(DotnetDirectory, ".cache", "frames");

    private static string FindDotnet()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Kharos.slnx"))) return dir.FullName;
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Рисует сцену кадром .NET без окна: RGBA8 сверху вниз, размер сцены. Ставит Viewer и сцены (П0.2, Д1); пока null — сцен нет,
    /// сверка пропускается.
    /// </summary>
    public static Func<GpuContext, ParityScene, byte[]>? NetFrame { get; set; }

    /// <summary>Описания всех сцен или null, если эталоны не сделаны.</summary>
    public static List<ParityScene>? LoadScenes()
    {
        var path = Path.Combine(GoldenDirectory, "scenes.json");
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return JsonSerializer.Deserialize<List<ParityScene>>(document.RootElement.GetProperty("scenes").GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    /// <summary>Эталон кадра TS сцены name.</summary>
    public static (byte[] Rgba, int Width, int Height) LoadGolden(string name) => ReadPng(Path.Combine(GoldenDirectory, name + ".png"));

    /// <summary>
    /// Сравнивает кадры ts и net (RGBA8, один размер), пишет картинки в <see cref="CacheDirectory"/> под именем scene.
    /// terrain — прямоугольники (x, y, ширина, высота) земли; пусто — вся картинка сверяется по пикселям.
    /// </summary>
    public static ParityResult Compare(string scene, byte[] ts, byte[] net, int width, int height, IReadOnlyList<int[]> terrain, ParityLimits? limits = null)
    {
        limits ??= ParityLimits.Default;
        if (ts.Length < width * height * 4 || net.Length < width * height * 4) throw new ArgumentException("мало пикселей");
        var mask = new bool[width * height];
        foreach (var rect in terrain)
            for (var y = Math.Max(0, rect[1]); y < Math.Min(height, rect[1] + rect[3]); y++)
                for (var x = Math.Max(0, rect[0]); x < Math.Min(width, rect[0] + rect[2]); x++)
                    mask[y * width + x] = true;

        var diff = new byte[width * height * 4];
        int compared = 0, over = 0, terrainPixels = 0;
        long sum = 0;
        var tsPalette = new Dictionary<int, int>();
        var netPalette = new Dictionary<int, int>();
        var shift = 8 - limits.PaletteBits;
        for (var i = 0; i < width * height; i++)
        {
            int a = i * 4;
            int dr = Math.Abs(ts[a] - net[a]), dg = Math.Abs(ts[a + 1] - net[a + 1]), db = Math.Abs(ts[a + 2] - net[a + 2]);
            var worst = Math.Max(dr, Math.Max(dg, db));
            if (mask[i])
            {
                terrainPixels++;
                Count(tsPalette, ((ts[a] >> shift) << 16) | ((ts[a + 1] >> shift) << 8) | (ts[a + 2] >> shift));
                Count(netPalette, ((net[a] >> shift) << 16) | ((net[a + 1] >> shift) << 8) | (net[a + 2] >> shift));
                diff[a] = diff[a + 1] = diff[a + 2] = 40;
                diff[a + 3] = 255;
                continue;
            }
            compared++;
            sum += dr + dg + db;
            var isOver = worst > limits.ChannelDelta;
            if (isOver) over++;
            diff[a] = isOver ? (byte)255 : (byte)Math.Min(255, dr * 4);
            diff[a + 1] = isOver ? (byte)0 : (byte)Math.Min(255, dg * 4);
            diff[a + 2] = isOver ? (byte)0 : (byte)Math.Min(255, db * 4);
            diff[a + 3] = 255;
        }
        var distance = 0.0;
        if (terrainPixels > 0)
        {
            foreach (var key in tsPalette.Keys.Union(netPalette.Keys))
                distance += Math.Abs((tsPalette.GetValueOrDefault(key) - netPalette.GetValueOrDefault(key)) / (double)terrainPixels);
            distance /= 2;
        }
        var files = new[] { Save(scene + "-ts", ts, width, height), Save(scene + "-net", net, width, height), Save(scene + "-diff", diff, width, height) };
        return new ParityResult(width, height, compared, over, compared == 0 ? 0 : over / (double)compared, compared == 0 ? 0 : sum / (3.0 * compared), terrainPixels, distance, files);
    }

    private static void Count(Dictionary<int, int> palette, int key) => palette[key] = palette.GetValueOrDefault(key) + 1;

    private static string Save(string name, byte[] rgba, int width, int height)
    {
        var path = Path.Combine(CacheDirectory, name + ".png");
        Png.Write(path, rgba, width, height);
        return path;
    }

    /// <summary>PNG 8 бит, RGB или RGBA, без чересстрочности (так пишет Chrome) — в RGBA8.</summary>
    public static (byte[] Rgba, int Width, int Height) ReadPng(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 8 || BinaryPrimitives.ReadUInt64BigEndian(bytes) != 0x89504E470D0A1A0AUL) throw new InvalidDataException($"{path}: не PNG");
        int width = 0, height = 0, colorType = 0;
        using var data = new MemoryStream();
        for (var at = 8; at + 8 <= bytes.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
            var type = System.Text.Encoding.ASCII.GetString(bytes, at + 4, 4);
            var body = bytes.AsSpan(at + 8, length);
            if (type == "IHDR")
            {
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                if (body[8] != 8 || body[12] != 0 || (body[9] != 2 && body[9] != 6)) throw new NotSupportedException($"{path}: нужен PNG 8 бит RGB/RGBA без чересстрочности");
                colorType = body[9];
            }
            else if (type == "IDAT") data.Write(body);
            else if (type == "IEND") break;
            at += 12 + length;
        }
        data.Position = 0;
        using var inflate = new ZLibStream(data, CompressionMode.Decompress);
        var channels = colorType == 6 ? 4 : 3;
        var stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        inflate.ReadExactly(raw);
        var rgba = new byte[width * height * 4];
        var previous = new byte[stride];
        var line = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            raw.AsSpan(y * (stride + 1) + 1, stride).CopyTo(line);
            for (var i = 0; i < stride; i++)
            {
                int left = i >= channels ? line[i - channels] : 0, up = previous[i], upLeft = i >= channels ? previous[i - channels] : 0;
                line[i] = (byte)(line[i] + filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException($"{path}: фильтр {filter}"),
                });
            }
            for (var x = 0; x < width; x++)
            {
                var to = (y * width + x) * 4;
                rgba[to] = line[x * channels];
                rgba[to + 1] = line[x * channels + 1];
                rgba[to + 2] = line[x * channels + 2];
                rgba[to + 3] = channels == 4 ? line[x * channels + 3] : (byte)255;
            }
            (previous, line) = (line, previous);
        }
        return (rgba, width, height);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}

/// <summary>Тесты сверки: сама сверка на синтетических кадрах, чтение эталонов TS, программный адаптер и сцены целиком.</summary>
public sealed class ParityTests
{
    [Fact]
    public void Compare_IdenticalFramesPass_AndWritesThreeImages()
    {
        var frame = Gradient(64, 40, 0);
        var result = Parity.Compare("parity-self", frame, frame, 64, 40, [[0, 0, 16, 10]]);
        Assert.True(result.Passed(ParityLimits.Default), result.Describe(ParityLimits.Default));
        Assert.Equal((0, 0.0, 0.0), (result.Over, result.MeanDelta, result.TerrainDistance));
        Assert.Equal(16 * 10, result.TerrainPixels);
        foreach (var file in result.Files) Assert.True(File.Exists(file), file);
        Assert.Equal(3, result.Files.Select(Path.GetFileName).Distinct().Count());
    }

    [Fact]
    public void Compare_RespectsTolerance()
    {
        var ts = Gradient(100, 100, 0);
        // Сдвиг на 3 во всех каналах: ниже порога 24, средняя |Δ| = 3 > 2 — не проходит; сдвиг на 1 — проходит.
        var near = Gradient(100, 100, 1);
        var far = Gradient(100, 100, 3);
        Assert.True(Parity.Compare("parity-near", ts, near, 100, 100, []).Passed(ParityLimits.Default));
        Assert.False(Parity.Compare("parity-far", ts, far, 100, 100, []).Passed(ParityLimits.Default));
        // 1 % пикселей с Δ 100 — выше доли 0,5 %.
        var spots = (byte[])ts.Clone();
        for (var i = 0; i < 100; i++) spots[i * 4 * 100] = (byte)(spots[i * 4 * 100] ^ 0x80);
        var result = Parity.Compare("parity-spots", ts, spots, 100, 100, []);
        Assert.False(result.Passed(ParityLimits.Default));
        Assert.InRange(result.OverShare, 0.009, 0.011);
    }

    [Fact]
    public void Compare_TerrainIsJudgedByPaletteShares_NotByPixels()
    {
        // Земля из двух цветов 50/50: в одном кадре шахматка, в другом — полосы. Пиксели расходятся, доли палитры — нет.
        const int size = 64;
        byte[] Fill(Func<int, int, bool> pick)
        {
            var frame = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var i = (y * size + x) * 4;
                    var light = pick(x, y);
                    (frame[i], frame[i + 1], frame[i + 2], frame[i + 3]) = light ? ((byte)200, (byte)160, (byte)80, (byte)255) : ((byte)120, (byte)90, (byte)40, (byte)255);
                }
            return frame;
        }
        var checker = Fill((x, y) => (x + y) % 2 == 0);
        var stripes = Fill((x, _) => x % 2 == 0);
        var all = new[] { new[] { 0, 0, size, size } };
        Assert.True(Parity.Compare("parity-terrain-same", checker, stripes, size, size, all).Passed(ParityLimits.Default));
        Assert.False(Parity.Compare("parity-terrain-pixels", checker, stripes, size, size, []).Passed(ParityLimits.Default));
    }

    [Fact]
    public void GoldenPng_IsReadableAndMatchesSceneSize()
    {
        var scenes = Parity.LoadScenes();
        Assert.SkipWhen(scenes is null, "эталонов кадров TS нет: CHROME=... bun dotnet/tools/golden/frames.ts");
        foreach (var scene in scenes!)
        {
            var (rgba, width, height) = Parity.LoadGolden(scene.Name);
            Assert.Equal((scene.Width, scene.Height), (width, height));
            Assert.Equal(width * height * 4, rgba.Length);
            Assert.Contains(rgba.Where((_, i) => i % 4 == 3), a => a == 255);
        }
    }

    [Fact]
    public void SoftwareAdapter_IsRequestedByEnvironment()
    {
        var previous = Environment.GetEnvironmentVariable("KHAROS_GPU_ADAPTER");
        Environment.SetEnvironmentVariable("KHAROS_GPU_ADAPTER", "software");
        try
        {
            using var gpu = GpuContext.TryCreateHeadless(out var reason);
            Assert.SkipWhen(gpu is null, $"программного адаптера нет: {reason}");
            TestContext.Current.SendDiagnosticMessage($"программный адаптер: {gpu!.AdapterName}, {gpu.Backend}, Cpu={gpu.IsSoftware}");
            Directory.CreateDirectory(Parity.CacheDirectory);
            File.WriteAllText(Path.Combine(Parity.CacheDirectory, "adapter.txt"), $"{gpu.AdapterName}, {gpu.Backend}, программный: {gpu.IsSoftware}, wgpu {gpu.NativeVersion}{Environment.NewLine}");
            Assert.True(gpu.IsSoftware, $"запрошен программный адаптер, получен {gpu.AdapterName} ({gpu.Backend})");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KHAROS_GPU_ADAPTER", previous);
        }
    }

    [Fact]
    public void ScenesMatchTsFrames()
    {
        var scenes = Parity.LoadScenes();
        Assert.SkipWhen(scenes is null, "эталонов кадров TS нет: CHROME=... bun dotnet/tools/golden/frames.ts");
        Assert.SkipWhen(Parity.NetFrame is null, "нет кадров .NET: мод base не собран (Kharos.Base.Scenario, ViewerScenes)");
        var limits = OperatingSystem.IsMacOS() ? ParityLimits.Relaxed : ParityLimits.Default;
        var previous = Environment.GetEnvironmentVariable("KHAROS_GPU_ADAPTER");
        // Программный адаптер: кадры повторяемы. На macOS его нет — тогда видеокарта и ослабленный допуск.
        if (!OperatingSystem.IsMacOS()) Environment.SetEnvironmentVariable("KHAROS_GPU_ADAPTER", "software");
        try
        {
            using var gpu = GpuContext.TryCreateHeadless(out var reason);
            Assert.SkipWhen(gpu is null, $"нет адаптера WebGPU: {reason}");
            var failures = new List<string>();
            var summary = new List<string> { "сцена\tдопуск\tвне допуска\tсредняя |Δ|\tпалитра земли" };
            foreach (var scene in scenes!)
            {
                var (ts, width, height) = Parity.LoadGolden(scene.Name);
                var net = Parity.NetFrame!(gpu!, scene);
                var result = Parity.Compare(scene.Name, ts, net, width, height, scene.TerrainRegions, limits);
                TestContext.Current.SendDiagnosticMessage($"{scene.Name}: {result.Describe(limits)}");
                summary.Add($"{scene.Name}\t{(result.Passed(limits) ? "да" : "нет")}\t{result.OverShare:P2}\t{result.MeanDelta:F2}\t{result.TerrainDistance:F3}");
                if (!result.Passed(limits)) failures.Add($"{scene.Name}: {result.Describe(limits)}");
            }
            File.WriteAllLines(Path.Combine(Parity.CacheDirectory, "summary.tsv"), summary);
            // Рисунок .NET догоняет TS в Д1: до него сверка идёт и пишет картинки, а расхождение — пропуск с числами.
            Assert.SkipWhen(failures.Count > 0, "ждёт Д1:\n" + string.Join("\n", failures));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KHAROS_GPU_ADAPTER", previous);
        }
    }

    private static byte[] Gradient(int width, int height, int shift)
    {
        var frame = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                (frame[i], frame[i + 1], frame[i + 2], frame[i + 3]) = ((byte)(x + shift), (byte)(y + shift), (byte)(x + y + shift), (byte)255);
            }
        return frame;
    }
}
