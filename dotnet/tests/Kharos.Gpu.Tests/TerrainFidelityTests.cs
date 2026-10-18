using Kharos.Engine.Terrain;

namespace Kharos.Gpu.Tests;

/// <summary>
/// Сверка terrain.wgsl с вычислением на процессоре: чистый эрг (песок без скал, болот и гор). Цвет текселя там —
/// гребень бархана (индексы палитры 0, 1, 4: шум, fbm, hash, mod — без зерна) или ровный песок (2 или 3 по зерну
/// fract(sin(·)·43758) — его точность у видеокарт разная, сверяется только доля).
/// </summary>
public sealed class TerrainFidelityTests(GpuFixture fixture)
{
    private static readonly (float R, float G, float B)[] Erg =
        [(0.40f, 0.27f, 0.08f), (0.60f, 0.44f, 0.16f), (0.76f, 0.60f, 0.27f), (0.80f, 0.65f, 0.31f), (0.93f, 0.82f, 0.52f)];

    [Fact]
    public void BlankErg_MatchesCpu()
    {
        const int size = 512;
        const float zoom = 32, cameraX = 100, cameraY = 60;
        using var renderer = new Renderer(fixture.Require());
        renderer.Resize(size, size);
        var land = new Land(GeneratorConfig.Default with { Blank = true });
        Scene.Use(renderer, land);
        renderer.Render(Scene.Frame(cameraX, cameraY, zoom, size, size));
        var image = Image.Of(renderer);
        image.Save("terrain-erg");

        var dunes = renderer.Dunes;
        int texels = 0, ridgeMatches = 0, ridges = 0, plain = 0, light = 0, offPalette = 0, blocky = 0;
        for (int py = 0; py < size; py += 2)
        {
            for (int px = 0; px < size; px += 2)
            {
                // Тексель под пикселем — та же формула, что в шейдере (uv — центр пикселя).
                float sx = cameraX + ((px + 0.5f) / size - 0.5f) * size / zoom;
                float sy = cameraY + ((py + 0.5f) / size - 0.5f) * size / zoom;
                int tx = (int)MathF.Floor(sx * 16), ty = (int)MathF.Floor(sy * 16);
                var c = image[px, py];
                if (image[px + 1, py] != c || image[px, py + 1] != c || image[px + 1, py + 1] != c) blocky++;
                int index = Nearest(c, out var distance);
                if (distance > 2) offPalette++;
                bool gpuRidge = index is 0 or 1 or 4;
                int cpu = Cpu.Ridge((tx + 0.5f) / 16, (ty + 0.5f) / 16, dunes.X, dunes.Z);
                texels++;
                if (gpuRidge == cpu >= 0 && (!gpuRidge || cpu == index)) ridgeMatches++;
                if (cpu >= 0) ridges++;
                else if (!gpuRidge)
                {
                    plain++;
                    if (index == 3) light++;
                }
            }
        }
        var share = (float)light / plain;
        TestContext.Current.SendDiagnosticMessage($"эрг: текселей {texels}, гребней {ridges}, совпало {ridgeMatches}, светлых {share:P1}, вне палитры {offPalette}, нечётких {blocky}");
        Assert.Equal(0, offPalette);
        Assert.Equal(0, blocky);
        Assert.True(ridges > texels / 50, $"гребней мало: {ridges}");
        Assert.True(ridgeMatches >= texels * 0.995, $"совпало {ridgeMatches} из {texels}");
        Assert.InRange(share, 0.25f, 0.35f);
    }

    private static int Nearest((byte R, byte G, byte B) c, out int distance)
    {
        int best = 0;
        distance = int.MaxValue;
        for (int i = 0; i < Erg.Length; i++)
        {
            int d = Math.Max(Math.Abs(c.R - (int)MathF.Round(Erg[i].R * 255)), Math.Max(Math.Abs(c.G - (int)MathF.Round(Erg[i].G * 255)), Math.Abs(c.B - (int)MathF.Round(Erg[i].B * 255))));
            if (d < distance) (distance, best) = (d, i);
        }
        return best;
    }

    /// <summary>Часть terrain.frag на процессоре, в f32: hash, noise, fbm, duneRidge.</summary>
    private static class Cpu
    {
        private const float Period = 512;

        private static float Fract(float x) => x - MathF.Floor(x);
        private static float Mod(float x, float y) => x - y * MathF.Floor(x / y);
        private static float Mix(float a, float b, float t) => a * (1 - t) + b * t;

        private static float Smoothstep(float e0, float e1, float x)
        {
            var t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
            return t * t * (3 - 2 * t);
        }

        private static float Hash(float x, float y)
        {
            x = Fract(x * 123.34f);
            y = Fract(y * 456.21f);
            var d = x * (x + 45.32f) + y * (y + 45.32f);
            x += d;
            y += d;
            return Fract(x * y);
        }

        private static float Noise(float x, float y, float sx, float sy)
        {
            float periodX = Period * sx, periodY = Period * sy;
            x *= sx;
            y *= sy;
            float ix = MathF.Floor(x), iy = MathF.Floor(y);
            float fx = Fract(x), fy = Fract(y);
            fx = fx * fx * (3 - 2 * fx);
            fy = fy * fy * (3 - 2 * fy);
            return Mix(
                Mix(Hash(Mod(ix, periodX), Mod(iy, periodY)), Hash(Mod(ix + 1, periodX), Mod(iy, periodY)), fx),
                Mix(Hash(Mod(ix, periodX), Mod(iy + 1, periodY)), Hash(Mod(ix + 1, periodX), Mod(iy + 1, periodY)), fx),
                fy);
        }

        private static float Fbm(float x, float y, float scale)
        {
            float sum = 0, amplitude = 0.5f;
            for (int i = 0; i < 4; i++)
            {
                sum += amplitude * Noise(x + i * 17f, y + i * 17f, scale, scale);
                scale *= 2;
                amplitude *= 0.5f;
            }
            return sum;
        }

        /// <summary>duneRidge в глубине песков (глубина 1, правок кистью нет): индекс палитры или −1.</summary>
        public static int Ridge(float x, float y, float threshold, float margin)
        {
            var coarse = Fbm(x, y, 1);
            var shift = (coarse - 0.5f) * 8;
            var field = Noise(x + 53 + shift, y + 53 + shift, 0.125f, 0.125f) * 0.75f + Noise(x + 11, y + 11, 0.5f, 0.5f) * 0.25f;
            field -= 1 - Smoothstep(margin, margin + 0.3f, 1);
            if (field < threshold) return -1;
            var phase = y * 1.5f + x * 0.375f + Noise(x, y, 0.75f, 0.75f) * 1.2f;
            var ridge = MathF.Floor(phase);
            var across = phase - ridge;
            var along = Noise(x + ridge * 8, Mod(ridge, 64), 0.5f, 1);
            var width = Smoothstep(0.3f, 0.55f, along) * Smoothstep(threshold, threshold + 0.06f, field);
            if (across < 0.2f * width) return 4;
            if (across < 0.55f * width) return 0;
            if (across < 0.7f * width) return 1;
            return -1;
        }
    }
}
