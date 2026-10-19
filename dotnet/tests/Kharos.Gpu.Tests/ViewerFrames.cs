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
            var pixels = ViewerScenes.Render(gpu, scene.Name);
            Directory.CreateDirectory(Parity.CacheDirectory);
            File.WriteAllLines(Path.Combine(Parity.CacheDirectory, scene.Name + "-notes.txt"), ViewerScenes.Notes.GetValueOrDefault(scene.Name) ?? []);
            return pixels;
        };
    }
}
