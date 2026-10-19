using Base.Shared.Orders;
using Kharos.Client.Game;
using Kharos.Gpu;
using Kharos.Plugins.Game;
using RenderFrame = Kharos.Gpu.Frame;

namespace Kharos.Base.Tests;

/// <summary>
/// Обвязка Viewer (docs/dotnet-n6.md, Н6.3): клиент base в режиме CLR на реплике хоста сценария — приём настоящим
/// протоколом, кадры, рисунки, звук, интерфейс, приказы; сверка CLR и client.wasm на одной сцене.
/// </summary>
public sealed class ViewerTests
{
    private static void SkipUnless(ModMode mode) => Assert.SkipWhen(Viewer.Unavailable(mode) is not null, Viewer.Unavailable(mode) ?? "");

    /// <summary>Маленькая сцена: база и отряд игрока 1, чужой юнит; камера — на базу.</summary>
    private static (Sim Sim, Viewer Viewer) Scene(ModMode mode)
    {
        var sim = Sim.Create(new SimOptions { Size = 64, Fog = false, Land = new { blank = true }, Mode = ModMode.Clr });
        sim.SetCredits(1, 5000);
        sim.SetCredits(2, 5000);
        ViewerScenes.Place(sim, "command", 4, 4, 1);
        ViewerScenes.Place(sim, "generator", 9, 4, 1);
        var units = new[] { sim.PutUnit("buggy", 1, 6.5, 10.5), sim.PutUnit("tank", 1, 8.5, 10.5), sim.PutUnit("infantry", 1, 10.5, 10.5) };
        sim.PutUnit("buggy", 2, 14.5, 12.5);
        sim.Send(1, new MoveOrder { Units = units, X = 12, Y = 14 });
        var viewer = sim.Viewer(1, new ViewerOptions { Mode = mode, Width = 640, Height = 400 });
        viewer.Camera = new CameraState(9, 9, 32);
        viewer.Atmosphere = new AtmosphereState(1, 1.5f, 0.4f, 0);
        return (sim, viewer);
    }

    [Fact]
    public void ViewerRunsClientModInClr()
    {
        SkipUnless(ModMode.Clr);
        var (sim, viewer) = Scene(ModMode.Clr);
        using var _ = sim;
        Assert.Equal(ModMode.Clr, viewer.Mode);
        var mod = Assert.Single(viewer.Game.Mods);
        Assert.Equal(ModMode.Clr, mod.Mode);
        Assert.Equal(2, mod.Module.LoadInfo.Abi);
        var frame = viewer.Run(1);
        Assert.Equal(60, viewer.Frames);
        // Реплика дошла настоящим путём: здания и юниты своего игрока видны клиенту и нарисованы.
        Assert.True(viewer.World.Alive(sim.With("Building").First()));
        Assert.True(frame.SpriteLayers.Sum(layer => layer.Count) > 0, "в кадре нет спрайтов");
        Assert.Equal((9f, 9f, 32f), (frame.View.X, frame.View.Y, frame.View.Zoom));
        Assert.Equal(1f, frame.Light);
        // Приказ хоста (move до зрителя) — юниты поехали, у клиента — тоже.
        Assert.True(sim.Until(() => viewer.World.Number(sim.With("Mover").First(), Kharos.Engine.World.Builtin.Position, 1) > 11, limit: 10));
        Assert.Empty(viewer.Log);
    }

    [Fact]
    public void ViewerRecordsInputSentOrdersAndUi()
    {
        SkipUnless(ModMode.Clr);
        var (sim, viewer) = Scene(ModMode.Clr);
        using var _ = sim;
        viewer.Run(0.5);
        // Щелчок по пустой земле и правый щелчок — что бы мод ни решил, кадры идут, приказы (если есть) уходят хосту.
        viewer.ClickWorld(6.5, 10.5);
        viewer.ClickWorld(12.5, 6.5, Kharos.Client.Ui.PointerButtons.Right);
        viewer.Run(0.5);
        foreach (var (name, _) in viewer.Sent.All)
            Assert.False(string.IsNullOrEmpty(name));
        Assert.NotNull(viewer.Ui.Texts());
        Assert.True(viewer.Draws.Total >= 0);
        Assert.Empty(viewer.Log);
        TestContext.Current.SendDiagnosticMessage($"Viewer: приказы [{string.Join(", ", viewer.Sent.Names)}], звуки [{string.Join(", ", viewer.Sounds.Names.Distinct())}], " +
            $"рисунки: линий {viewer.Draws.Lines.Count}, кругов {viewer.Draws.Circles.Count}, рамок {viewer.Draws.Rects.Count}, спрайтов {viewer.Draws.Sprites.Count}; кнопки [{string.Join(", ", viewer.Ui.Buttons())}]");
    }

    [Fact]
    public void ClrAndWasmClientsBuildTheSameFrame()
    {
        SkipUnless(ModMode.Clr);
        SkipUnless(ModMode.Wasm);
        var (clrSim, clr) = Scene(ModMode.Clr);
        var (wasmSim, wasm) = Scene(ModMode.Wasm);
        using var a = clrSim;
        using var b = wasmSim;
        Assert.Equal(ModMode.Wasm, Assert.Single(wasm.Game.Mods).Mode);
        Assert.Equal(clr.Game.Looks.Count, wasm.Game.Looks.Count);
        Assert.Equal(clr.Game.Atlas.Pixels, wasm.Game.Atlas.Pixels);
        Assert.NotEmpty(clr.Game.Looks);
        for (var tick = 0; tick < 40; tick++)
        {
            var x = clr.Run(0.05);
            var y = wasm.Run(0.05);
            Same(x, y, $"тик {tick}");
            Assert.True(x.SpriteLayers.Sum(layer => layer.Count) > 0, "в кадре нет спрайтов");
            Assert.Equal(clr.Sent.Names, wasm.Sent.Names);
            Assert.Equal(clr.Sounds.Names, wasm.Sounds.Names);
            Assert.Equal(clr.Ui.Texts(), wasm.Ui.Texts());
        }
        Assert.Empty(clr.Log);
        Assert.Empty(wasm.Log);
    }

    /// <summary>Кадры совпали: спрайты по слоям, слой поверх мира, интерфейс, свет и туман.</summary>
    private static void Same(RenderFrame x, RenderFrame y, string where)
    {
        Assert.Equal(x.View, y.View);
        for (var layer = 0; layer < RenderFrame.Layers; layer++)
            Assert.True(x.SpriteLayers[layer].SequenceEqual(y.SpriteLayers[layer]), $"{where}: слой спрайтов {layer}");
        Assert.True(x.World.Vertices.SequenceEqual(y.World.Vertices), $"{where}: слой поверх мира");
        Assert.True(x.Interface.Vertices.SequenceEqual(y.Interface.Vertices), $"{where}: интерфейс");
        Assert.True(x.Decals.SequenceEqual(y.Decals), $"{where}: следы");
        Assert.Equal((x.Light, x.Wind, x.Precipitation), (y.Light, y.Wind, y.Precipitation));
    }

    [Fact]
    public void ViewerRendersHeadlessFrame()
    {
        SkipUnless(ModMode.Clr);
        using var gpu = Viewer.TryGpu(out var reason);
        Assert.SkipWhen(gpu is null, $"нет адаптера WebGPU: {reason}");
        var (sim, viewer) = Scene(ModMode.Clr);
        using var _ = sim;
        viewer.Run(0.5);
        var pixels = viewer.Render(gpu!);
        Assert.Equal(640 * 400 * 4, pixels.Length);
        Assert.True(pixels.Where((_, i) => i % 4 == 0).Distinct().Count() > 8, "кадр однотонный");
    }
}
