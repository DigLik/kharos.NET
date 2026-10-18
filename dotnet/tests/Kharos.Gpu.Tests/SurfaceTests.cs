using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Kharos.Gpu.Tests;

/// <summary>Поверхность окна: скрытое окно GLFW без контекста GL — его не видно, но цепочка показа настоящая.</summary>
public sealed class SurfaceTests
{
    [Fact]
    public void HiddenWindow_PresentsAndReads()
    {
        // GLFW на macOS работает только в главном потоке процесса, а xUnit гоняет тесты в пуле — окно там не создать.
        if (OperatingSystem.IsMacOS()) Assert.Skip("GLFW на macOS — только из главного потока");
        IWindow window;
        try
        {
            window = Window.Create(WindowOptions.Default with { IsVisible = false, API = GraphicsAPI.None, Size = new Vector2D<int>(320, 200), Title = "kharos-gpu-test" });
            window.Initialize();
        }
        catch (Exception error)
        {
            Assert.Skip($"окна нет: {error.Message}");
            return;
        }
        try
        {
            GpuContext gpu;
            try
            {
                gpu = GpuContext.CreateForWindow(window, vsync: false);
            }
            catch (Exception error) when (error is GpuException or DllNotFoundException or FileNotFoundException)
            {
                Assert.Skip($"нет адаптера WebGPU: {error.Message}");
                return;
            }
            using var _ = gpu;
            Assert.True(gpu.HasSurface);
            using var renderer = new Renderer(gpu);
            Assert.True(renderer.HasPass("present"));
            var size = window.FramebufferSize;
            renderer.Resize(size.X, size.Y);
            var frame = Scene.Frame(0, 0, 32, size.X, size.Y);
            frame.Interface.Rect(10, 10, 50, 50, Batch.Rgba(200, 100, 50));
            renderer.SetFontAtlas(new byte[Batch.AtlasSize * Batch.AtlasSize].Select((_, i) => i % Batch.AtlasSize < 4 && i / Batch.AtlasSize < 4 ? (byte)255 : (byte)0).ToArray(), 1);
            for (int i = 0; i < 3; i++) renderer.Render(frame);
            var image = Image.Of(renderer);
            Assert.Equal(((byte)200, (byte)100, (byte)50), image[30, 30]);
            TestContext.Current.SendDiagnosticMessage($"поверхность: {gpu.SurfaceFormat}, {size.X}×{size.Y}");
        }
        finally
        {
            window.Dispose();
        }
    }
}
