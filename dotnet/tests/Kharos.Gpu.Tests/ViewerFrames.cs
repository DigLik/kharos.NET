using System.Runtime.CompilerServices;
using Kharos.Base.Scenario;

namespace Kharos.Gpu.Tests;

/// <summary>
/// Кадры .NET для сверки с TS (Parity.NetFrame, docs/dotnet-n6.md, Н6.4): сцену собирает Viewer на сценарии base
/// (Sim + ClientGame в режиме CLR или KHAROS_MODS=wasm) и рисует без окна на данной видеокарте. Что сборке пришлось
/// обойти (ViewerScenes.Notes), пишется рядом с картинками: dotnet/.cache/frames/&lt;сцена&gt;-notes.txt. Мод base не
/// собран — хука нет, сверка пропускается.
/// </summary>
internal static class ViewerFrames
{
    [ModuleInitializer]
    internal static void Install()
    {
        if (Viewer.Unavailable(Sim.DefaultMode) is not null)
            return;
        Parity.NetFrame = (gpu, scene) =>
        {
            var pixels = Render(gpu, ViewerScenes.Find(scene.Name));
            Directory.CreateDirectory(Parity.CacheDirectory);
            File.WriteAllLines(Path.Combine(Parity.CacheDirectory, scene.Name + "-notes.txt"), ViewerScenes.Notes.GetValueOrDefault(scene.Name) ?? []);
            return pixels;
        };
    }

    private static readonly ConditionalWeakTable<GpuContext, Renderer> Renderers = [];

    /// <summary>
    /// Как ViewerScenes.Render, но снос осадков — нулевой: TS рисует сцену одним кадром renderer.draw(camera, time, 0),
    /// пелена за ветром у него не успевает уйти (uDrift копится по delta кадров, precipitationPass.ts).
    /// </summary>
    private static byte[] Render(GpuContext gpu, FrameScene scene)
    {
        var (sim, viewer, _) = ViewerScenes.Build(scene);
        using (sim)
        {
            var frame = ViewerScenes.Settle(viewer, scene);
            frame.Drift = (0, 0);
            if (!scene.Hud)
                frame.Interface.Clear();
            foreach (var error in viewer.Log)
                (ViewerScenes.Notes.TryGetValue(scene.Name, out var notes) ? notes : ViewerScenes.Notes[scene.Name] = []).Add($"клиент: {error}");
            Renderer renderer;
            lock (Renderers)
                renderer = Renderers.GetValue(gpu, static gpu => new Renderer(gpu));
            return viewer.Render(renderer);
        }
    }
}
