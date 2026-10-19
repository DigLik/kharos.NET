using Base.Shared.Orders;
using Kharos.Client.Game;
using static Kharos.Base.Tests.Client.Hud.RequestButtonsTests;

namespace Kharos.Base.Tests.Client.Panels;

/// <summary>Игроки, союзы, защита, поражение на обвязке Viewer: сигналы ally, defeated, sheltered, player_respawned.</summary>
public sealed class PlayersViewerTests
{
    private static void SkipUnless() => Assert.SkipWhen(Viewer.Unavailable(Sim.DefaultMode) is not null, Viewer.Unavailable(Sim.DefaultMode) ?? "");

    /// <summary>Два игрока с главными зданиями; зритель — первый. start — партия со стартовым набором (и новым после «Начать заново»).</summary>
    private static (Sim Sim, Viewer Viewer, Viewer Other) Duel(bool start = false)
    {
        var sim = Sim.Create(new SimOptions { Game = "frames", Size = start ? 128 : 64, Fog = false, Land = new { blank = true }, Settings = start ? new { start = true } : null, Players = [1, 2] });
        sim.SetCredits(1, 500);
        sim.SetCredits(2, 500);
        ViewerScenes.Place(sim, "command", 0, 0, 1);
        ViewerScenes.Place(sim, "command", 20, 0, 2);
        var viewer = sim.Viewer(1, new ViewerOptions { Width = 1280, Height = 720 });
        viewer.Camera = new CameraState(10, 0, 32);
        // Второй игрок в сети: только тогда он есть в списке игроков партии.
        var other = sim.Viewer(2, new ViewerOptions { Width = 1280, Height = 720 });
        other.Step(3);
        viewer.Step(3);
        return (sim, viewer, other);
    }

    private static ClientInput Tab(Viewer viewer) => viewer.Input with { Keys = ["tab"] };

    [Fact(DisplayName = "сигнал ally: вести о союзе идут строками уведомлений, союзник появляется на панели, кнопки списка по Tab шлют приказы")]
    public void AllySignalMakesNoticesAndButtons()
    {
        SkipUnless();
        var (sim, viewer, other) = Duel();
        using var _ = sim;
        using var __ = other;
        // Второй предлагает союз: первому приходит весть, в списке по Tab — «Принять» и «Отклонить».
        sim.Offer(2, 1);
        viewer.Run(0.5);
        Assert.Contains("предлагает союз", viewer.Ui.Text("notices"));
        viewer.Frame(Tab(viewer));
        viewer.Frame(Tab(viewer));
        Assert.True(viewer.Ui.Has("roster"));
        Assert.Contains(T("hud.allyAsked"), viewer.Ui.Text("roster"));
        Assert.Contains($"roster:2:hud.allyAccept", viewer.Ui.Buttons());
        Assert.Contains($"roster:2:hud.allyDecline", viewer.Ui.Buttons());
        // «Принять» — приказ ally; когда союз заключён, весть и союзник над панелью.
        viewer.Sent.Clear();
        viewer.Click("roster:2:hud.allyAccept");
        viewer.Step();
        var order = Assert.Single(viewer.Sent.Of<AllyOrder>());
        Assert.Equal(2u, order.To);
        viewer.Run(0.5);
        Assert.Contains("ally:2", viewer.Ui.Ids());
        Assert.Contains("заключён", viewer.Ui.Text("notices"));
        // Союз есть: в списке по Tab у союзника — «Исключить» (глава союза — тот, кто принял), у себя — выход или роспуск.
        viewer.Frame(Tab(viewer));
        viewer.Frame(Tab(viewer));
        Assert.Contains(viewer.Ui.Buttons(), id => id.StartsWith("roster:1:", StringComparison.Ordinal));
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "сигналы sheltered: срок защиты новичка на полосе, «под защитой» в списке игроков")]
    public void ShelteredSignalShowsProtection()
    {
        SkipUnless();
        var (sim, viewer, other) = Duel();
        using var _ = sim;
        using var __ = other;
        Assert.False(viewer.Ui.Has("protection"));
        sim.Protect(1, 5);
        viewer.Run(1);
        Assert.True(viewer.Ui.Has("protection"));
        Assert.StartsWith(T("hud.protection", "left", "").TrimEnd(), viewer.Ui.Text("protection"));
        Assert.Contains(T("hud.sheltered"), viewer.Ui.Text("notices"));
        viewer.Frame(Tab(viewer));
        viewer.Frame(Tab(viewer));
        Assert.Contains(T("hud.sheltered"), viewer.Ui.Text("roster"));
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "сигналы defeated и player_respawned: окно поражения с «Начать заново», весть другим игрокам, после новой жизни окно уходит")]
    public void DefeatedAndRespawnedSignals()
    {
        SkipUnless();
        var (sim, viewer, other) = Duel(start: true);
        using var _ = sim;
        using var __ = other;
        Assert.False(viewer.Ui.Has("defeat"));
        // Сдался второй: вся его база исчезает, первому приходит весть о поражении.
        sim.Send(2, new SurrenderOrder());
        viewer.Run(8);
        Assert.Contains(T("host.defeated"), viewer.Ui.Text("notices"));
        Assert.False(viewer.Ui.Has("defeat"));
        // Сдался первый: окно поражения с кнопкой «Начать заново», та просит хост начать заново.
        sim.Send(1, new SurrenderOrder());
        viewer.Run(8);
        Assert.True(viewer.Ui.Has("defeat"));
        Assert.Contains(T("game.startOver"), viewer.Ui.Text("defeat"));
        // «Начать заново» — просьба оболочке (Shell Respawn), та просит хост; мод хоста даёт новое стартовое.
        viewer.Click("defeat:again");
        viewer.Step();
        viewer.Run(1);
        Assert.True(sim.Owned(1) > 0, "после «Начать заново» у игрока снова есть база");
        // Хост начал игрока заново: сигнал player_respawned — окна нет, прошлые вести сняты.
        Assert.False(viewer.Ui.Has("defeat"));
        Assert.Contains(T("host.restarts"), viewer.Ui.Text("notices"));
        Assert.DoesNotContain(T("host.defeated"), viewer.Ui.Text("notices"));
        Assert.Empty(viewer.Log);
    }
}
