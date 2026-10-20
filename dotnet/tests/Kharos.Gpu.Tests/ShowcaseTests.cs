using System.Diagnostics;
using System.Text;

namespace Kharos.Gpu.Tests;

/// <summary>Кадры целиком — для глаза (dotnet/.cache/gpu) — и замер времени кадра 1920 × 1080.</summary>
public sealed class ShowcaseTests(GpuFixture fixture)
{
    /// <summary>Сцена: машинки сторон с фарами и лучами, здания с огнями, следы, туман вокруг центра, интерфейс.</summary>
    private static Frame Build(float x, float y, float zoom, int width, int height, int units, float light, bool fog, float precipitation = 0)
    {
        var frame = Scene.Frame(x, y, zoom, width, height);
        frame.Light = light;
        frame.Time = 2.5f;
        frame.Precipitation = precipitation;
        frame.Wind = (1.5f, 0.5f);
        frame.Drift = (3, 1);
        var random = new Random(1);
        var (left, top, right, bottom) = frame.View.Bounds(-1);
        for (int i = 0; i < units; i++)
        {
            float ux = left + random.NextSingle() * (right - left), uy = top + random.NextSingle() * (bottom - top);
            if (i % 10 == 0)
            {
                Scene.Sprite(frame, 3, MathF.Floor(ux), MathF.Floor(uy), 2, 2, Scene.Building, 0, 0.1f, 0.35f);
                frame.Spots.AddRange([MathF.Floor(ux) + 1, MathF.Floor(uy) + 0.4f, 32, 0.9f]);
                frame.Blooms.AddRange([MathF.Floor(ux) + 1, MathF.Floor(uy) + 0.4f, 5, 0.9f]);
                continue;
            }
            int team = i % 7 == 0 ? 3 : i % 3 == 0 ? 1 : 0;
            Scene.Sprite(frame, 1, ux - 0.5f, uy - 0.5f, 1, 1, Scene.Buggy, team, 0.08f, 0.35f, team == 3);
            var angle = random.NextSingle() * MathF.Tau;
            float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
            frame.Beams.AddRange([ux + cos * 0.4f, uy + sin * 0.4f, 96, 0.8f, cos, sin, 1.5f, 0.3f]);
            frame.Spots.AddRange([ux, uy, 6, 1]);
            frame.Blooms.AddRange([ux, uy, 3, 1]);
            // Колея за машинкой.
            float tx = ux - cos * 2, ty = uy - sin * 2;
            frame.Decals.AddRange([tx, ty, ux - cos * 0.5f, uy - sin * 0.5f, 0.12f, 0, 0, 0, 0, 0.35f, 0.27f, 0.18f, 0.6f, 1, 0.8f, i]);
        }
        if (fog)
        {
            // Обзор: круг в 14 тайлов виден, кольцо до 22 — разведано, дальше — не разведано; рамка в тайл.
            int w = (int)(right - left) + 4, h = (int)(bottom - top) + 4;
            var alpha = new byte[w * h];
            float ox = MathF.Floor(left) - 1, oy = MathF.Floor(top) - 1;
            for (int ty = 0; ty < h; ty++)
            {
                for (int tx = 0; tx < w; tx++)
                {
                    var d = MathF.Sqrt(MathF.Pow(ox + tx + 0.5f - x, 2) + MathF.Pow(oy + ty + 0.5f - y, 2));
                    alpha[ty * w + tx] = d < 14 ? (byte)0 : d < 22 ? (byte)128 : (byte)240;
                }
            }
            frame.Fog = (ox, oy);
            frame.FogUpdate = (alpha, w, h);
        }
        // Интерфейс: панель скином, «текст» — блоками глифа, значок стороны, мини-карта с туманом.
        var ui = frame.Interface;
        float screenW = frame.View.ScreenWidth, screenH = frame.View.ScreenHeight;
        ui.Rect(0, screenH - 120, screenW, screenH, Batch.Rgba(20, 24, 30, 230));
        ui.Quad(12, screenH - 108, 108, screenH - 12, (0, 0, 1, 1), Batch.Rgba(255, 255, 255), Paint.Skin);
        for (int i = 0; i < 12; i++)
            ui.Quad(130 + i * 12, screenH - 100, 140 + i * 12, screenH - 88, (100f / Batch.AtlasSize, 100f / Batch.AtlasSize, 110f / Batch.AtlasSize, 110f / Batch.AtlasSize), Batch.Rgba(230, 220, 160), Paint.Font);
        ui.Quad(130, screenH - 76, 194, screenH - 12, Scene.Buggy, Batch.Rgba(255, 255, 255), new Paint(PaintKind.Sprite, 1));
        ui.Quad(screenW - 108, screenH - 108, screenW - 12, screenH - 12, (0, 0, 1, 1), Batch.Rgba(255, 255, 255), new Paint(PaintKind.Minimap));
        if (fog) ui.Quad(screenW - 108, screenH - 108, screenW - 12, screenH - 12, (0, 0, 1, 1), Batch.Rgba(255, 255, 255), new Paint(PaintKind.Fog));
        // Выделение поверх мира.
        frame.World.Circle(x, y, 1.2f, 0.08f, Batch.Rgba(120, 255, 120, 200), 32);
        return frame;
    }

    private Renderer New(int width, int height)
    {
        var renderer = new Renderer(fixture.Require());
        renderer.Resize(width, height);
        renderer.SetAtlas(Scene.Atlas(), Scene.AtlasSize, Scene.AtlasSize);
        var font = new byte[Batch.AtlasSize * Batch.AtlasSize];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++) font[y * Batch.AtlasSize + x] = 255;
        for (int y = 100; y < 110; y++)
            for (int x = 100; x < 110; x++) font[y * Batch.AtlasSize + x] = 255;
        renderer.SetFontAtlas(font, 1);
        var skin = new byte[16 * 16 * 4];
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int at = (y * 16 + x) * 4;
                bool edge = x == 0 || y == 0 || x == 15 || y == 15;
                (skin[at], skin[at + 1], skin[at + 2], skin[at + 3]) = edge ? ((byte)90, (byte)100, (byte)110, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)253);
            }
        }
        renderer.SetSkin(skin, 16, 16);
        var minimap = new byte[64 * 64 * 4];
        for (int i = 0; i < minimap.Length; i += 4) (minimap[i], minimap[i + 1], minimap[i + 2], minimap[i + 3]) = ((byte)(i / 4 % 64 * 4), (byte)(i / 256 * 4), 90, 255);
        renderer.SetMinimap(minimap, 64, 64);
        return renderer;
    }

    [Fact]
    public void Showcase_DayDuskNight()
    {
        const int width = 1280, height = 720;
        using var renderer = New(width, height);
        var land = Scene.DefaultLand();
        Scene.Use(renderer, land);
        var (x, y) = Scene.Interesting(land);
        var day = renderer;
        day.Render(Build(x, y, 32, width, height, 120, 1, true));
        var dayImage = Image.Of(renderer);
        dayImage.Save("showcase-day");
        renderer.Render(Build(x, y, 32, width, height, 120, 0.5f, true));
        Image.Of(renderer).Save("showcase-dusk");
        renderer.Render(Build(x, y, 32, width, height, 120, 0.15f, true));
        var night = Image.Of(renderer);
        night.Save("showcase-night");
        renderer.Render(Build(x, y, 32, width, height, 120, 0.6f, false, 0.8f));
        Image.Of(renderer).Save("showcase-storm");
        renderer.Render(Build(x, y, 8, width, height, 400, 0.3f, false));
        Image.Of(renderer).Save("showcase-far-night");
        // Программный адаптер тоже: «на WARP ночь не темнее дня» (CI, e69841f) на WARP Windows 11 не повторяется — день
        // 52,0, сумерки 40,1, ночь 28,1 и на HEAD волны 0, и после Д1. Если вернётся, числа — в сообщении.
        var gpu = fixture.Require();
        Assert.True(night.Mean() < dayImage.Mean() * 0.8f, $"{gpu.AdapterName} ({gpu.Backend}, программный: {gpu.IsSoftware}): день {dayImage.Mean():F1}, ночь {night.Mean():F1}");
    }

    // Замер (кадр 1080p по часам, только печать): не в обычном прогоне.
    [Fact(Explicit = true)]
    [Trait("Category", "Bench")]
    public void Performance_1080p()
    {
        const int width = 1920, height = 1080;
        var gpu = fixture.Require();
        using var renderer = New(width, height);
        var land = Scene.DefaultLand();
        Scene.Use(renderer, land);
        var (x, y) = Scene.Interesting(land);
        var report = new StringBuilder($"WebGPU: {gpu.AdapterName}, {gpu.Backend}, сборка Debug; кадр {width}×{height}, ~1000 спрайтов, среднее по 40 кадрам\n");
        foreach (var (name, file, zoom, light, precipitation) in new[] { ("день", "day", 32f, 1f, 0f), ("ночь", "night", 32f, 0.2f, 0f), ("ночь с осадками", "night-storm", 32f, 0.2f, 0.8f), ("день издалека (4 точки на пиксель)", "day-far", 8f, 1f, 0f) })
        {
            var frame = Build(x, y, zoom, width, height, 1000, light, true, precipitation);
            int sprites = frame.SpriteLayers.Sum(layer => layer.Count) / Frame.Sprite;
            for (int i = 0; i < 5; i++) renderer.Render(frame);
            gpu.WaitIdle();
            frame.FogUpdate = null;
            const int frames = 40;
            var total = Stopwatch.StartNew();
            double cpu = 0;
            for (int i = 0; i < frames; i++)
            {
                var start = Stopwatch.GetTimestamp();
                renderer.Render(frame);
                cpu += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                gpu.WaitIdle();
            }
            total.Stop();
            Image.Of(renderer).Save("perf-" + file);
            report.AppendLine($"{name}: {total.Elapsed.TotalMilliseconds / frames:F2} мс на кадр (запись команд {cpu / frames:F2} мс), спрайтов {sprites}, " +
                $"лучей {frame.Beams.Count / 8}, огней {frame.Spots.Count / 4}");
        }
        Directory.CreateDirectory(Shots.Directory);
        File.WriteAllText(Path.Combine(Shots.Directory, "perf.txt"), report.ToString());
        TestContext.Current.SendDiagnosticMessage(report.ToString());
    }
}
