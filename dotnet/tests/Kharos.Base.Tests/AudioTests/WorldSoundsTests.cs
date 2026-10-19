using Kharos.Base.Scenario;
using Kharos.Client.Game;

namespace Kharos.Base.Tests.AudioTests;

/// <summary>
/// Звуки мира — packages/tests/worldSounds.test.ts, и звук работы зданий — machines.test.ts. Настоящий клиент base на
/// реплике партии без тумана: камера в (0, 0) на шестнадцать тайлов в стороны, мир правится прямо у хоста, звук пишется в
/// <c>Sounds</c> (разовые — Play, петли — Loop) и рендерится офлайн.
/// </summary>
public sealed class WorldSoundsTests
{
    private static (Sim Sim, Viewer Viewer) Setup()
    {
        Assert.SkipWhen(Viewer.Unavailable(Sim.DefaultMode) is not null, Viewer.Unavailable(Sim.DefaultMode) ?? "");
        var sim = Sim.Create(new SimOptions { Size = 64, Fog = false, Land = new { blank = true } });
        var viewer = sim.Viewer(1, new ViewerOptions { Width = 1280, Height = 800, Audio = true });
        viewer.Camera = new CameraState(0, 0, 32);
        // Мир сначала устаканивается: всё, что есть в первые секунды, — не события.
        viewer.Run(3);
        return (sim, viewer);
    }

    /// <summary>Разовые звуки по порядку.</summary>
    private static IReadOnlyList<string> Played(Viewer viewer) => [.. viewer.Sounds.Of(Ops.Play).Select(call => call.Name!)];

    /// <summary>Звуки мира по порядку, без боя: у чужого юнита у ворот стреляет ядро главного здания.</summary>
    private static IReadOnlyList<string> World(Viewer viewer) => [.. Played(viewer).Where(name => name is not ("laser" or "sizzle"))];

    /// <summary>Громкость, которую в последний раз просили у петли; null — не просили.</summary>
    private static float? Loop(Viewer viewer, string name) => viewer.Sounds.Of(Ops.Loop).LastOrDefault(call => call.Name == name) is { Name: not null } call ? call.Values[0] : null;

    [Fact(DisplayName = "стройка: стучит, пока движется, и звенит, достроившись")]
    public void ConstructionKnocksWhileMovingAndChimesWhenDone()
    {
        var (sim, viewer) = Setup();
        using var _ = sim;
        var entity = ViewerScenes.Place(sim, "generator", 2, 2, 1);
        sim.Add(entity, "Site");
        viewer.Run(0.1);
        sim.Set(entity, "Site.progress", 5);
        viewer.Run(0.3);
        Assert.True(Loop(viewer, "construct") > 0);
        sim.Remove(entity, "Site");
        viewer.Run(0.1);
        Assert.Contains("built", Played(viewer));
        viewer.Run(1);
        Assert.Equal(0f, Loop(viewer, "construct"));
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "ворота лязгают, щит звенит от удара, гаснет и поднимается")]
    public void GatesClangShieldRingsOnHitGoesDownAndComesUp()
    {
        var (sim, viewer) = Setup();
        using var _ = sim;
        // Щиту нужна энергия: без неё хост обнуляет заряд каждый тик.
        ViewerScenes.Place(sim, "command", -6, 2, 1);
        ViewerScenes.Place(sim, "generator", -6, -2, 1);
        var gate = ViewerScenes.Place(sim, "gate", 1, 1, 1);
        var shield = ViewerScenes.Place(sim, "shieldGenerator", -2, 0, 1);
        viewer.Run(1.5);
        sim.Set(shield, "Shield.charge", 100);
        viewer.Run(0.2);
        Assert.Empty(Played(viewer));
        Assert.Equal(1, sim.Num(gate, "Gate.open"));
        // Удар по щиту; чужой наземный юнит у ворот поднимает плиты — ворота лязгают.
        sim.Set(shield, "Shield.hit", 5);
        viewer.Run(0.1);
        Assert.Equal(["shieldHit"], Played(viewer));
        sim.PutUnit("truck", 2, 6.5, 1.5);
        Assert.True(viewer.Until(() => sim.Num(gate, "Gate.open") == 0, limit: 3));
        viewer.Run(0.2);
        Assert.Equal(["shieldHit", "gate"], World(viewer));
        // Пробитый купол не растёт, пока не выйдет Shield.down: заряд держится там, где его поставили.
        sim.Set(shield, "Shield.down", sim.Tick + 1000);
        sim.Set(shield, "Shield.charge", 0);
        viewer.Run(0.1);
        sim.Set(shield, "Shield.charge", 50);
        viewer.Run(0.1);
        Assert.Equal(["shieldHit", "gate", "shieldDown", "shieldUp"], World(viewer));
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "ядерный взрыв слышен, вспыхнув")]
    public void NukeBlastIsHeardWhenItFlashes()
    {
        var (sim, viewer) = Setup();
        using var _ = sim;
        // Вспышка взрыва: сущность с Position и Reveal для всех (GLARE на хосте).
        var glare = sim.World.Spawn();
        sim.Add(glare, "Position");
        sim.Set(glare, "Position.x", 3);
        sim.Set(glare, "Position.y", 3);
        sim.Add(glare, "Reveal");
        sim.Set(glare, "Reveal.radius", 12);
        sim.Set(glare, "Reveal.until", sim.Tick + 1000);
        sim.Set(glare, "Reveal.all", 1);
        sim.Add(glare, "Sighting");
        sim.Set(glare, "Sighting.rule", 2);
        viewer.Run(0.1);
        Assert.Equal(["nukeBlast"], Played(viewer));
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "рубильник, корабль с покупкой, подарок")]
    public void SwitchShipWithPurchaseAndGift()
    {
        var (sim, viewer) = Setup();
        using var _ = sim;
        var port = ViewerScenes.Place(sim, "spaceport", 3, 3, 1);
        var factory = ViewerScenes.Place(sim, "generator", -3, 3, 2);
        viewer.Run(0.1);
        sim.Add(port, "Off");
        sim.PutTrade(port, "metal", 10, 10, 10, buy: true);
        viewer.Run(0.1);
        sim.Remove(port, "Trade");
        sim.Set(factory, "Owner.player", 1);
        viewer.Run(0.1);
        Assert.Equal(["powerOff", "shipLand", "delivered", "gift"], Played(viewer));
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "своя электростанция гудит, чужая — нет")]
    public void OwnPowerPlantHumsAnEnemysDoesNot()
    {
        Assert.SkipWhen(Viewer.Unavailable(Sim.DefaultMode) is not null, Viewer.Unavailable(Sim.DefaultMode) ?? "");
        using var sim = Sim.Create(new SimOptions { Size = 64, Fog = false, Land = new { blank = true } });
        var viewer = sim.Viewer(1, new ViewerOptions { Width = 1280, Height = 800, Audio = true });
        viewer.Camera = new CameraState(0, 0, 32);
        ViewerScenes.Place(sim, "generator", 4, 4, 2);
        viewer.Run(0.5);
        Assert.Equal(0f, Loop(viewer, "hum"));
        ViewerScenes.Place(sim, "generator", -4, 4, 1);
        viewer.Run(0.5);
        Assert.True(Loop(viewer, "hum") > 0);
        Assert.Empty(viewer.Log);
    }
}
