using System.Diagnostics;
using System.Globalization;
using Kharos.Client.Looks;
using Kharos.Client.Ui;
using Kharos.Gpu;

namespace Kharos.Client.Tests.Ui;

/// <summary>Панель игрока целиком (как hud.luau): картинки для просмотра и время кадра.</summary>
public sealed class HudTests : IDisposable
{
    private readonly Gui _ui = new(Fonts.Load());
    private readonly (Look[] Looks, LookAtlas Atlas) _looks = Demo.Atlas();

    public void Dispose() => _ui.Fonts.Dispose();

    private void Frame(UiInput input, bool selected = true, float progress = 0.4f)
    {
        _ui.Begin(input);
        Demo.Hud(_ui, _looks.Looks, selected, progress);
        _ui.Finish();
    }

    private byte[] Render(float scale, int width, int height)
    {
        var raster = new SoftRaster((int)(width * scale), (int)(height * scale), scale) { FontAtlas = _ui.Fonts.Atlas.Pixels, Sprites = _looks.Atlas };
        raster.Fill(Color.Hex(0x3b4a2c));
        raster.Draw(_ui.Batch);
        return raster.Pixels;
    }

    [Theory]
    [InlineData(1f, "hud.png")]
    [InlineData(2f, "hud-2x.png")]
    [InlineData(1.5f, "hud-1.5x.png")]
    public void RendersWithCardTip(float scale, string name)
    {
        const int width = 1280, height = 720;
        UiInput At(double time, float x, float y) => Frames.At(time, x, y, width, height) with { Scale = scale };
        Frame(Frames.Off(0, width, height) with { Scale = scale });
        var cell = _ui.RectOf("cell:0")!.Value;
        var (x, y) = (cell.X + cell.W / 2, cell.Y + cell.H / 2);
        Frame(At(1, x, y));
        Frame(At(2, x, y));
        Assert.True(_ui.Hovered("cell:0"));
        // Подсказка-карточка — последней корневой панелью, над сеткой.
        var tip = _ui.Roots[^1];
        Assert.Equal(Skin.Panel, tip.Style.Skin);
        Assert.Equal(width - 8, tip.Rect.X + tip.Rect.W);
        Assert.Equal(height - (140 + 28), tip.Rect.Y + tip.Rect.H);
        var kinds = _ui.Batch.Segments.Select(segment => segment.Paint.Kind).ToHashSet();
        Assert.Superset(new HashSet<PaintKind> { PaintKind.Font, PaintKind.Skin, PaintKind.Sprite, PaintKind.Minimap, PaintKind.Fog }, kinds);
        Repo.Png(name, Render(scale, width, height), (int)(width * scale), (int)(height * scale));
        // Ничего не выбрано — подсказка по управлению с переносом.
        Frame(Frames.Off(3, width, height) with { Scale = scale }, selected: false);
        Repo.Png("nothing-" + name, Render(scale, width, height), (int)(width * scale), (int)(height * scale));
    }

    [Fact]
    public void FrameTime()
    {
        const int width = 1280, height = 720, warmup = 30, frames = 300;
        var times = new List<double>();
        long allocated = 0;
        for (var i = 0; i < warmup + frames; i++)
        {
            var input = Frames.At(i / 60.0, 200 + i % 600, 600 + i % 100, width, height);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            Frame(input, progress: i % 100 / 100f);
            watch.Stop();
            if (i < warmup) continue;
            times.Add(watch.Elapsed.TotalMilliseconds);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        times.Sort();
        var nodes = CountNodes(_ui.Roots);
        var report = string.Create(CultureInfo.InvariantCulture,
            $"HUD 1280×720: {nodes} узлов, {_ui.Batch.Indices.Count / 6} четырёхугольников, {_ui.Batch.Segments.Count} отрезков; " +
            $"кадр: медиана {times[frames / 2]:F3} мс, 95% {times[frames * 95 / 100]:F3} мс, макс. {times[^1]:F3} мс; " +
            $"выделено {allocated / frames / 1024.0:F1} КБ на кадр");
        File.WriteAllText(Repo.Cache("hud-perf.txt"), report + Environment.NewLine);
        TestContext.Current.SendDiagnosticMessage(report);
        Assert.True(times[frames / 2] < 5, report);
    }

    private static int CountNodes(IEnumerable<Node> nodes) => nodes.Sum(node => 1 + CountNodes(node.Children));
}
