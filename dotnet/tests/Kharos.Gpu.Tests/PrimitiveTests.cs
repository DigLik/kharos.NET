namespace Kharos.Gpu.Tests;

/// <summary>
/// Примитивы рендера Д1 (docs/dotnet-n6.md, Н6.2) без модов: множитель цвета спрайта, силуэты прозрачного, клетки слоёв
/// под следами и спрайтами, огни, пунктир, зерно земли как у SwiftShader. Кадры — без окна; адаптера нет — пропуск.
/// </summary>
public sealed class PrimitiveTests(GpuFixture fixture)
{
    private const int Width = 160, Height = 100;

    private Renderer New()
    {
        var renderer = new Renderer(fixture.Require());
        renderer.Resize(Width, Height);
        renderer.SetAtlas(Scene.Atlas(), Scene.AtlasSize, Scene.AtlasSize);
        var font = new byte[Batch.AtlasSize * Batch.AtlasSize];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++) font[y * Batch.AtlasSize + x] = 255;
        renderer.SetFontAtlas(font, 1);
        return renderer;
    }

    /// <summary>Кадр без земли: камера в (10, 10), 32 пикселя на тайл — экран 5 × 3,125 тайла.</summary>
    private static Frame Empty(float light = 1) { var frame = Scene.Frame(10, 10, 32, Width, Height); frame.Light = light; return frame; }

    private static (int X, int Y) Pixel(Frame frame, float x, float y)
    {
        var (sx, sy) = frame.View.ToScreen(x, y);
        return ((int)sx, (int)sy);
    }

    [Fact]
    public void Sprite_TintIsPremultipliedMultiplier()
    {
        using var renderer = New();
        var plain = Empty();
        Scene.Sprite(plain, 3, 9, 9, 2, 2, Scene.Building, 0);
        renderer.Render(plain);
        var before = Image.Of(renderer);
        // Красный множитель с альфой 0,5: картинка × (1, 0, 0) поверх половины фона.
        var tinted = Empty();
        tinted.AddSprite(3, -1, -1, 2, 2, Scene.Building, 0, 0, 0, false, (0.5f, 0, 0, 0.5f));
        renderer.Render(tinted);
        var after = Image.Of(renderer);
        renderer.Render(Empty());
        var background = Image.Of(renderer);
        var (x, y) = Pixel(plain, 9.6f, 9.6f);
        var (sprite, under, mixed) = (before[x, y], background[x, y], after[x, y]);
        Assert.NotEqual(sprite, under);
        Assert.InRange(mixed.R, (sprite.R + under.R) / 2 - 2, (sprite.R + under.R) / 2 + 2);
        Assert.InRange(mixed.G, under.G / 2 - 2, under.G / 2 + 2);
        Assert.InRange(mixed.B, under.B / 2 - 2, under.B / 2 + 2);
    }

    [Fact]
    public void Sprite_TransparentDoesNotBlockBeams_OpaqueDoes()
    {
        using var renderer = New();
        float Behind(float alpha)
        {
            var frame = Empty(0);
            // Луч вправо из (8, 10) длиной 160 текселей; стена 1 × 1 на его пути в (9.5..10.5).
            frame.Beams.AddRange([8, 10, 160, 1, 1, 0, 2, 0.1f]);
            frame.AddSprite(3, 9.5f - frame.View.X, 9.5f - frame.View.Y, 1, 1, Scene.Building, 0, 0, 0, false, (alpha, alpha, alpha, alpha));
            renderer.Render(frame);
            var (x, y) = Pixel(frame, 11.5f, 10);
            return Image.Of(renderer).Mean(x - 3, y - 3, x + 3, y + 3);
        }
        float open = Behind(0.4f), solid = Behind(1);
        Assert.True(open > solid + 5, $"за чертежом (альфа 0,4) {open:F1}, за стеной {solid:F1}");
    }

    [Fact]
    public void Cells_LieUnderDecalsAndSprites()
    {
        using var renderer = New();
        var frame = Empty();
        // Клетка на всём экране, поверх — спрайт: спрайт виден, клетка — вокруг него.
        frame.Cells.AddRange([-3, -2, 6, 4, Scene.Building.U0, Scene.Building.V0, Scene.Building.U1, Scene.Building.V1, 0, 0, 0, 0, 1, 1, 1, 1]);
        renderer.Render(frame);
        var cells = Image.Of(renderer);
        renderer.Render(Empty());
        var empty = Image.Of(renderer);
        var (x, y) = Pixel(frame, 7.5f, 8.5f);
        Assert.NotEqual(empty[x, y], cells[x, y]);
        Scene.Sprite(frame, 0, 9, 9, 1, 1, Scene.Buggy, 1);
        renderer.Render(frame);
        var over = Image.Of(renderer);
        var (sx, sy) = Pixel(frame, 9.5f, 9.5f);
        Assert.NotEqual(cells[sx, sy], over[sx, sy]);
        Assert.Equal(cells[x, y], over[x, y]);
    }

    [Fact]
    public void Lights_SpotBrightensNight_BloomShowsByDay()
    {
        using var renderer = New();
        var (x, y) = Pixel(Empty(), 10, 10);
        float Mean(Frame frame)
        {
            renderer.Render(frame);
            return Image.Of(renderer).Mean(x - 4, y - 4, x + 4, y + 4);
        }
        var dark = Mean(Empty(0.1f));
        var lit = Empty(0.1f);
        lit.Spots.AddRange([10, 10, 32, 1]);
        Assert.True(Mean(lit) > dark + 10, "пятно огня ночью");
        var day = Mean(Empty());
        var bloom = Empty();
        bloom.Blooms.AddRange([10, 10, 8, 1]);
        Assert.True(Mean(bloom) > day + 5, "ореол днём");
    }

    [Fact]
    public void Dashes_AreDotsFromTheEnd()
    {
        var batch = new Batch();
        // 1 тайл, шаг 0,25 — точки на 0, 0,25, 0,5, 0,75 от конца.
        Assert.Equal(4, batch.Dashes(0, 0, 1, 0, 0.25f, 0.1f, Batch.Rgba(255, 255, 255)));
        Assert.Equal(16, batch.Vertices.Count);
        Assert.Equal(0.95f, batch.Vertices[0].X, 4);
        Assert.Equal(1.05f, batch.Vertices[1].X, 4);
        // Ломаная: шаг сквозной — на изломе (1, 0) точки нет, следующая — в (1, 0,2).
        var path = new Batch();
        Assert.Equal(5, path.Dashes([(0f, 0f), (1f, 0f), (1f, 0.5f)], 0.3f, 0.1f, Batch.Rgba(255, 255, 255)));
        Assert.Equal(0.95f, path.Vertices[16].X, 4);
        Assert.Equal(0.15f, path.Vertices[16].Y, 4);
        // Граница зоны: точки, что внутри соседней зоны, пропускаются.
        var zone = new Batch();
        Assert.Equal(12, zone.DashedCircle(0, 0, 2, 12, 0.1f, Batch.Rgba(255, 255, 255)));
        Assert.Equal(7, new Batch().DashedCircle(0, 0, 2, 12, 0.1f, Batch.Rgba(255, 255, 255), (x, _) => x > 0.01f));
    }

    [Fact]
    public void Dashes_DrawInWorldBatch()
    {
        using var renderer = New();
        var frame = Empty();
        frame.World.Dashes(8, 10, 12, 10, 0.5f, 0.125f, Batch.Rgba(255, 0, 0));
        renderer.Render(frame);
        var image = Image.Of(renderer);
        var (x, y) = Pixel(frame, 12, 10);
        var (gx, gy) = Pixel(frame, 11.75f, 10);
        Assert.Equal((byte)255, image[x, y].R);
        Assert.Equal((byte)0, image[x, y].G);
        Assert.NotEqual((byte)255, image[gx, gy].R);
    }

    [Fact]
    public void Grain_MatchesSwiftShaderSin()
    {
        // Числа — эмуляция Sin у SwiftShader (ShaderCore.cpp) в numpy: f32, fma в синусе, скалярное умножение без слияния.
        Assert.Equal(0f, Grain.At(0, 0));
        Assert.Equal(0.240234375f, Grain.At(1, 0));
        Assert.Equal(0.001953125f, Grain.At(5, 7));
        Assert.Equal(0.2109375f, Grain.At(1000, 3));
        Assert.Equal(0.263916015625f, Grain.At(-1, -1));
        Assert.Equal(0.9794921875f, Grain.At(-1536, 2047));
        Assert.Equal(Grain.Size * Grain.Size * 4, Grain.Bytes.Length);
        Assert.Equal(Grain.At(5, 7), BitConverter.ToSingle(Grain.Bytes[((7 * Grain.Size + 5) * 4)..]));
        // Синус SwiftShader — многочлен: в пределах 1e-3 от настоящего.
        for (var x = -10f; x < 10; x += 0.37f) Assert.InRange(Grain.Sin(x) - MathF.Sin(x), -1e-3f, 1e-3f);
    }
}
