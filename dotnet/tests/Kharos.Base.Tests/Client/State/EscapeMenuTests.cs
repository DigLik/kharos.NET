using Kharos.Client.Game;
using Kharos.Mod.Sdk.Client;

namespace Kharos.Base.Tests.ClientState;

/// <summary>
/// Esc в партии (controls.ts: onMenu): сначала снимает выделение и закрывает окна, а когда отменять нечего — клиент base
/// просит оболочку открыть меню игры (запись Shell, ShellActionMenu). F10 оболочки остаётся запасной клавишей.
/// </summary>
public sealed class EscapeMenuTests
{
    private static int Menus(Viewer viewer) => viewer.Game.ShellRequests.Count(request => request.Action == (byte)ShellAction.Menu);

    [Fact(DisplayName = "Esc без выделения и окон открывает меню игры; с выделением — сначала снимает его, открытое окно — закрывает")]
    public void EscapeOpensMenu()
    {
        Assert.SkipWhen(Viewer.Unavailable(Sim.DefaultMode) is not null, Viewer.Unavailable(Sim.DefaultMode) ?? "");
        using var sim = Sim.Create(new SimOptions { Game = "frames", Size = 64, Fog = false, Land = new { blank = true }, Players = [1] });
        sim.SetCredits(1, 5000);
        var core = ViewerScenes.Place(sim, "command", 4, 4, 1);
        using var viewer = sim.Viewer(1, new ViewerOptions { Width = 1280, Height = 720 });
        viewer.Camera = new CameraState(5.5, 5.5, 32);
        viewer.Step(3);
        viewer.Game.ShellRequests.Clear();

        // Выбрано здание: Esc снимает выделение, меню не открывает.
        viewer.ClickWorld(5.5, 5.5);
        viewer.Step(2);
        Assert.NotNull(viewer.Ui.Text("info:title"));
        viewer.Press("escape");
        Assert.Equal(0, Menus(viewer));
        Assert.Null(viewer.Ui.Text("info:title"));

        // Открыто окно исследований: Esc его закрывает, меню не открывает.
        viewer.Press("r");
        viewer.Step(2);
        Assert.True(viewer.Ui.Has("tech"));
        viewer.Press("escape");
        viewer.Step();
        Assert.False(viewer.Ui.Has("tech"));
        Assert.Equal(0, Menus(viewer));

        // Отменять нечего — меню игры.
        viewer.Press("escape");
        Assert.Equal(1, Menus(viewer));
        Assert.Empty(viewer.Log);
        _ = core;
    }
}
