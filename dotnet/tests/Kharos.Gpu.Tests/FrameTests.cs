using Kharos.Engine.Terrain;

namespace Kharos.Gpu.Tests;

/// <summary>Проходы кадра по отдельности: спрайты с палитрой, тени, свет и лучи, туман, следы, батчи, осадки.</summary>
public sealed class FrameTests(GpuFixture fixture)
{
    // Цвет пустого экрана: синий 0,1 × 255 = 25,5 — адаптеры округляют по-разному, отсюда допуск в единицу.
    private static readonly (byte R, byte G, byte B) Ground = (41, 33, 25);

    private Renderer New(int width, int height)
    {
        var renderer = new Renderer(fixture.Require());
        renderer.Resize(width, height);
        renderer.SetAtlas(Scene.Atlas(), Scene.AtlasSize, Scene.AtlasSize);
        return renderer;
    }

    private static Image Draw(Renderer renderer, Frame frame)
    {
        renderer.Render(frame);
        return Image.Of(renderer);
    }

    private static void Near((byte R, byte G, byte B) expected, (byte R, byte G, byte B) actual, int tolerance = 1, string what = "")
    {
        bool close = Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance;
        Assert.True(close, $"{what}: ждали {expected}, а {actual}");
    }

    [Fact]
    public void Sprites_TeamPaletteAndShadows()
    {
        using var renderer = New(320, 192);
        // 32 пикселя на тайл: тексель атласа — 2 × 2 пикселя; спрайт 1 × 1 тайл с углом в (48, 49) — пиксели от (96, 64).
        var frame = Scene.Frame(50, 50, 32, 320, 192);
        Scene.Sprite(frame, 1, 48, 49, 1, 1, Scene.Buggy, team: 0, shadowShift: 0.25f, shadow: 0.5f);
        Scene.Sprite(frame, 1, 50, 49, 1, 1, Scene.Buggy, team: 1, grey: true);
        Scene.Sprite(frame, 0, 52, 49, 1, 1, Scene.Buggy, team: 2, shadowShift: 0.25f, shadow: 0.5f);
        Scene.Sprite(frame, 3, 48, 51, 1, 1, Scene.Building, team: 0);
        var image = Draw(renderer, frame);
        image.Save("sprites");
        (byte, byte, byte) At(int sx, int sy, int tx, int ty) => image[sx + tx * 2 + 1, sy + ty * 2 + 1];

        Near(Scene.PaletteColor(0, 3), At(96, 64, 5, 4), 1, "тон 3");
        Near(Scene.PaletteColor(0, 2), At(96, 64, 5, 8), 1, "тон 2");
        Near(Scene.PaletteColor(0, 1), At(96, 64, 5, 11), 1, "тон 1");
        Near(Scene.Outline, At(96, 64, 2, 5), 0, "контур");
        Near(Scene.Red, At(96, 64, 7, 7), 0, "фара");
        Near(Ground, At(96, 64, 0, 0), 1, "прозрачное");
        // Тень слоя: силуэт со сдвигом в четверть тайла, полупрозрачный чёрный.
        Near(((byte)20, (byte)16, (byte)12), At(96, 64, 15, 15), 1, "тень");
        // Серость ничьего: 0,3 R + 0,59 G + 0,11 B основного цвета чужой стороны (210, 40, 30).
        Near(((byte)90, (byte)90, (byte)90), At(160, 64, 5, 8), 1, "серый");
        Near(Scene.PaletteColor(2, 2), At(224, 64, 5, 8), 1, "союзный");
        // Слой 0 (на земле) без тени.
        Near(Ground, At(224, 64, 15, 15), 1, "без тени");
        // Здание: полоса тона 2 и стена.
        Near(Scene.PaletteColor(0, 2), image[96 + 4, 128 + 5 * 2 / 2 + 1], 1, "здание, тон");
        Near(Scene.Wall, image[96 + 10, 128 + 20], 0, "здание, стена");
    }

    [Fact]
    public void Night_DarkerWithLightsAndBeamShadows()
    {
        const int width = 640, height = 384;
        using var renderer = New(width, height);
        var land = new Land(GeneratorConfig.Default with { Blank = true });
        Scene.Use(renderer, land);
        Frame Build(float light, bool wall, bool lights)
        {
            var frame = Scene.Frame(100, 100, 32, width, height);
            frame.Light = light;
            Scene.Sprite(frame, 1, 95.5f, 99.5f, 1, 1, Scene.Buggy, 0);
            if (wall) Scene.Sprite(frame, 3, 99, 99, 1, 2, Scene.Building, 0);
            if (lights)
            {
                // Луч фары вправо на 12 тайлов; огонь здания — пятно в 3 тайла.
                frame.Beams.AddRange([96.5f, 100, 192, 1, 1, 0, 2, 0.35f]);
                frame.Spots.AddRange([104, 96, 48, 1]);
                frame.Blooms.AddRange([104, 96, 8, 1]);
            }
            return frame;
        }
        var day = Draw(renderer, Build(1, true, false));
        var dark = Draw(renderer, Build(0.1f, true, false));
        var lit = Draw(renderer, Build(0.1f, true, true));
        var open = Draw(renderer, Build(0.1f, false, true));
        day.Save("light-day");
        dark.Save("light-night");
        lit.Save("light-night-lights");
        open.Save("light-night-open");

        Assert.True(dark.Mean() < day.Mean() * 0.5f, $"ночь {dark.Mean()} против дня {day.Mean()}");
        // Без огней ночь — день, умноженный на оттенок lightTint (цель RGBA8).
        var tint = Renderer.LightTint(0.1f);
        var (r, g, b) = day[20, 20];
        Near(((byte)MathF.Round(r * MathF.Round(tint.X * 255) / 255), (byte)MathF.Round(g * MathF.Round(tint.Y * 255) / 255), (byte)MathF.Round(b * MathF.Round(tint.Z * 255) / 255)),
            dark[20, 20], 1, "оттенок ночи");
        // Пятно огня светлее тьмы вокруг.
        int spotX = (104 - 100) * 32 + width / 2, spotY = (96 - 100) * 32 + height / 2;
        Assert.True(Image.Luma(lit[spotX, spotY]) > Image.Luma(dark[spotX, spotY]) + 20, "пятно");
        // Луч светит до стены; за стеной (x = 103) — тень, без стены — свет.
        int beforeX = (98 - 100) * 32 + width / 2, behindX = (103 - 100) * 32 + width / 2, axisY = height / 2;
        Assert.True(Image.Luma(lit[beforeX, axisY]) > Image.Luma(dark[beforeX, axisY]) + 15, "луч до стены");
        Assert.True(Image.Luma(open[behindX, axisY]) > Image.Luma(lit[behindX, axisY]) + 15,
            $"тень стены: {Image.Luma(open[behindX, axisY])} без стены, {Image.Luma(lit[behindX, axisY])} со стеной");
        Near(dark[behindX, axisY], lit[behindX, axisY], 2, "за стеной темно");
    }

    [Fact]
    public void Fog_DarkensUnexplored()
    {
        const int width = 640, height = 384;
        using var renderer = New(width, height);
        Scene.Use(renderer, new Land(GeneratorConfig.Default with { Blank = true }));
        var clear = Draw(renderer, Scene.Frame(100, 100, 32, width, height));
        var frame = Scene.Frame(100, 100, 32, width, height);
        // Картинка обзора 20 × 12 тайлов от (90, 94): левая половина не разведана (240), правая видна (0).
        var alpha = new byte[20 * 12];
        for (int y = 0; y < 12; y++)
            for (int x = 0; x < 10; x++) alpha[y * 20 + x] = 240;
        frame.Fog = (90, 94);
        frame.FogUpdate = (alpha, 20, 12);
        var fogged = Draw(renderer, frame);
        fogged.Save("fog");
        float left = fogged.Mean(0, 0, 200, height), right = fogged.Mean(440, 0, width, height);
        Assert.True(left < clear.Mean(0, 0, 200, height) * 0.1f, $"туман слева: {left}");
        Assert.Equal(clear.Mean(440, 0, width, height), right, 0.01f);
        // Тот же кадр без новой картинки — туман прежний.
        frame.FogUpdate = null;
        Assert.Equal(fogged.Pixels, Draw(renderer, frame).Pixels);
    }

    [Fact]
    public void Decals_PremultipliedOnGround()
    {
        using var renderer = New(320, 192);
        var frame = Scene.Frame(50, 50, 32, 320, 192);
        // Белая полоса шириной в четверть тайла, оранжевый 0,8.
        frame.Decals.AddRange([49, 50.1f, 51, 50.1f, 0.25f, 0, 0, 0, 0, 1, 0.5f, 0, 0.8f, 1, 1, 0]);
        var image = Draw(renderer, frame);
        image.Save("decals");
        Near(((byte)212, (byte)109, (byte)5), image[160, 99], 2, "след");
        Near(Ground, image[160, 120], 1, "мимо следа");
        Near(Ground, image[100, 99], 1, "за концом");
    }

    [Fact]
    public void Batch_InterfaceAndWorld()
    {
        const int width = 320, height = 192;
        using var renderer = New(width, height);
        var font = new byte[Batch.AtlasSize * Batch.AtlasSize];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++) font[y * Batch.AtlasSize + x] = 255;
        for (int y = 100; y < 110; y++)
            for (int x = 100; x < 110; x++) font[y * Batch.AtlasSize + x] = 255;
        renderer.SetFontAtlas(font, 1);
        var skin = new byte[8 * 8 * 4];
        for (int i = 0; i < skin.Length; i += 4) (skin[i], skin[i + 1], skin[i + 2], skin[i + 3]) = (128, 128, 128, 253);
        renderer.SetSkin(skin, 8, 8);
        renderer.SetMinimap([10, 200, 30, 255, 10, 200, 30, 255, 10, 200, 30, 255, 10, 200, 30, 255], 2, 2);

        var frame = Scene.Frame(50, 50, 32, width, height);
        var ui = frame.Interface;
        ui.Rect(10, 10, 60, 40, Batch.Rgba(200, 100, 50));
        ui.Quad(100, 60, 120, 80, (100f / Batch.AtlasSize, 100f / Batch.AtlasSize, 110f / Batch.AtlasSize, 110f / Batch.AtlasSize), Batch.Rgba(255, 255, 0), Paint.Font);
        ui.Quad(150, 10, 190, 50, (0, 0, 1, 1), Batch.Rgba(255, 255, 255), Paint.Skin);
        ui.Quad(150, 60, 182, 92, Scene.Buggy, Batch.Rgba(255, 255, 255), new Paint(PaintKind.Sprite, 1));
        ui.QuadClipped(200, 10, 260, 50, (Batch.WhiteU, Batch.WhiteV, Batch.WhiteU, Batch.WhiteV), Batch.Rgba(255, 0, 0), Paint.Font, new Clip(200, 10, 30, 40));
        ui.Quad(270, 10, 300, 40, (0, 0, 1, 1), Batch.Rgba(255, 255, 255), new Paint(PaintKind.Minimap));
        ui.Rect(10, 100, 60, 140, Batch.Rgba(255, 255, 255, 128));
        // Слой поверх мира — в тайлах.
        frame.World.Rect(51, 51, 52, 52, Batch.Rgba(0, 255, 0));
        var image = Draw(renderer, frame);
        image.Save("batch");

        Near(((byte)200, (byte)100, (byte)50), image[30, 25], 0, "заливка");
        Near(((byte)255, (byte)255, (byte)0), image[110, 70], 0, "глиф");
        Near(Ground, image[130, 70], 1, "мимо глифа");
        Near(Scene.PaletteColor(0, 2), image[170, 30], 1, "скин с палитрой");
        Near(Scene.PaletteColor(1, 2), image[150 + 11, 60 + 17], 1, "значок стороны");
        Near(((byte)255, (byte)0, (byte)0), image[210, 30], 0, "в обрезке");
        Near(Ground, image[245, 30], 1, "за обрезкой");
        Near(((byte)10, (byte)200, (byte)30), image[285, 25], 0, "мини-карта");
        Near(((byte)148, (byte)144, (byte)140), image[30, 120], 1, "полупрозрачное");
        Near(((byte)0, (byte)255, (byte)0), image[width / 2 + 48, height / 2 + 48], 0, "слой мира");
    }

    [Fact]
    public void Batch_ScaledInterface()
    {
        using var renderer = New(320, 192);
        renderer.SetFontAtlas(FontWithWhiteCorner(), 7);
        var frame = Scene.Frame(50, 50, 32, 320, 192, scale: 2);
        frame.Interface.Rect(10, 10, 60, 40, Batch.Rgba(200, 100, 50));
        frame.Interface.QuadClipped(70, 10, 130, 40, (Batch.WhiteU, Batch.WhiteV, Batch.WhiteU, Batch.WhiteV), Batch.Rgba(0, 0, 255), Paint.Font, new Clip(70, 20, 60, 10.3f));
        var image = Draw(renderer, frame);
        image.Save("batch-scale2");
        Near(((byte)200, (byte)100, (byte)50), image[60, 50], 0, "внутри");
        Near(Ground, image[15, 15], 1, "снаружи");
        Near(Ground, image[120, 50], 1, "кромка — логический пиксель 60 = физический 120");
        Near(((byte)0, (byte)0, (byte)255), image[200, 45], 0, "обрезка в физических пикселях");
        Near(Ground, image[200, 35], 1, "над обрезкой");
    }

    private static byte[] FontWithWhiteCorner()
    {
        var font = new byte[Batch.AtlasSize * Batch.AtlasSize];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++) font[y * Batch.AtlasSize + x] = 255;
        return font;
    }

    [Fact]
    public void Weather_ChangesFrame()
    {
        const int width = 960, height = 540;
        using var renderer = New(width, height);
        var land = Scene.DefaultLand();
        Scene.Use(renderer, land);
        var (x, y) = Scene.Interesting(land);
        Frame Build(float precipitation, float zoom)
        {
            var frame = Scene.Frame(x, y, zoom, width, height);
            frame.Precipitation = precipitation;
            frame.Time = 3.3f;
            frame.Wind = (2, 1);
            frame.Drift = (5, 3);
            return frame;
        }
        var clear = Draw(renderer, Build(0, 32));
        var storm = Draw(renderer, Build(1, 32));
        storm.Save("weather-near");
        Draw(renderer, Build(1, 6)).Save("weather-far");
        int changed = 0;
        for (int i = 0; i < clear.Pixels.Length; i += 4)
            if (clear.Pixels[i] != storm.Pixels[i] || clear.Pixels[i + 1] != storm.Pixels[i + 1]) changed++;
        Assert.True(changed > width * height / 10, $"изменилось {changed} пикселей");
    }
}
