using Kharos.Engine.Terrain;

namespace Kharos.Gpu.Tests;

public sealed class RendererTests(GpuFixture fixture)
{
    private const int Width = 960, Height = 540;

    private Renderer New(int width = Width, int height = Height)
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

    [Fact]
    public void Device_Created()
    {
        var gpu = fixture.Require();
        TestContext.Current.SendDiagnosticMessage($"WebGPU: {gpu.AdapterName}, {gpu.Backend}, wgpu-native {gpu.NativeVersion}");
        Assert.False(string.IsNullOrEmpty(gpu.AdapterName));
        Assert.False(gpu.HasSurface);
    }

    [Fact]
    public void Passes_Compile()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var renderer = New();
        TestContext.Current.SendDiagnosticMessage($"конвейеры: {started.Elapsed.TotalMilliseconds:F0} мс");
        foreach (var pass in Renderer.Passes) Assert.True(renderer.HasPass(pass), pass);
    }

    [Fact]
    public void Empty_ClearsToGround()
    {
        using var renderer = New(64, 32);
        var image = Draw(renderer, Scene.Frame(0, 0, 32, 64, 32));
        var (r, g, b) = image[10, 10];
        Assert.Equal(41, r);
        Assert.Equal(33, g);
        Assert.InRange(b, 25, 26);
    }

    [Fact]
    public void Terrain_RealLand()
    {
        using var renderer = New(1280, 720);
        var land = Scene.DefaultLand();
        Scene.Use(renderer, land);
        var (x, y) = Scene.Interesting(land);
        var near = Draw(renderer, Scene.Frame(x, y, 32, 1280, 720));
        near.Save("terrain-near");
        Assert.True(near.DistinctColors() > 12, $"цветов: {near.DistinctColors()}");
        // Издалека — сглаживание четырьмя точками на пиксель.
        var far = Draw(renderer, Scene.Frame(x, y, 4, 1280, 720));
        far.Save("terrain-far");
        Assert.True(far.DistinctColors() > 100, $"цветов: {far.DistinctColors()}");
        // Окно земли: тайл (x, y) — в текселе (x mod 512, y mod 512); камера за краем окна рисует то же.
        var shifted = Draw(renderer, Scene.Frame(x + Renderer.Window * 3, y, 32, 1280, 720));
        Assert.Equal(near.Pixels.Length, shifted.Pixels.Length);
        Assert.NotEqual(near.Pixels, shifted.Pixels);
    }

    [Fact]
    public void LandWindow_LoadsChunksOnceAndRereadsEdited()
    {
        using var renderer = New(640, 360);
        var land = Scene.DefaultLand();
        var reads = new List<(int X, int Y)>();
        var edited = (X: int.MinValue, Y: int.MinValue);
        renderer.SetLand((x, y) =>
        {
            reads.Add((x, y));
            return land.GetChunk(x, y);
        }, (x, y) => (x, y) == edited ? 1 : 0);
        var frame = Scene.Frame(100, 100, 32, 640, 360);
        renderer.Render(frame);
        var (fromX, fromY, toX, toY) = Renderer.LandChunksAround(frame.View);
        Assert.Equal((toX - fromX + 1) * (toY - fromY + 1), reads.Count);
        Assert.True(renderer.HasLandChunk(3, 3));
        reads.Clear();
        renderer.Render(frame);
        Assert.Empty(reads);
        // Правленый чанк перечитывается, остальные — нет.
        edited = (3, 3);
        renderer.Render(frame);
        Assert.Equal([(3, 3)], reads);
        Assert.True(renderer.HasLandChunk(3, 3, 1));
        // Через окно (512 тайлов) — те же слоты, другие чанки.
        reads.Clear();
        frame.View = frame.View with { X = 100 + Renderer.Window };
        renderer.Render(frame);
        Assert.Equal((toX - fromX + 1) * (toY - fromY + 1), reads.Count);
        Assert.False(renderer.HasLandChunk(3, 3, 1));
        // Чанк прямо, без источника.
        renderer.ClearLand();
        renderer.SetLandChunk(-1, -1, land.GetChunk(-1, -1));
        Assert.True(renderer.HasLandChunk(-1, -1));
        Assert.Throws<ArgumentException>(() => renderer.SetLandChunk(0, 0, new byte[16]));
    }
}
