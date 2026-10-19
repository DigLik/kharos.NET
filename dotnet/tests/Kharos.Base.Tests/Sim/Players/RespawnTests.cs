using Base.Shared.Signals;
using Kharos.Base.Tests.Kernel;

namespace Kharos.Base.Tests.Players;

/// <summary>
/// Начать заново на стороне хоста (restart в host.ts: vision.forget, forgetBuildings): мод, начавший игрока заново,
/// шлёт ему player_respawned, и хост закрывает ему туман и забывает его память о чужих зданиях.
/// </summary>
public sealed class RespawnTests
{
    [Fact(DisplayName = "начавший заново начинает в тумане: разведанное и память о чужих зданиях забыты; отказ мода ничего не забывает")]
    public void RespawnForgetsFogAndBuildingMemory()
    {
        Scene.SkipUnlessAvailable();
        using var sim = Sim.Create(new SimOptions { Size = 128, Land = new { blank = true }, Fog = true });
        var yard = sim.PutBuilding("wall", 40, 40, 2);
        var scout = sim.PutUnit("infantry", 1, 37.5, 40.5);
        // Своё здание: игрок не побеждён, и без велений администратора начать заново ему нельзя.
        sim.PutBuilding("wall", 100, 100, 1);
        sim.Seconds(0.5);
        Assert.True(sim.Host.Vision!.Sees(1, 40.5, 40.5));
        // Разведчик ушёл: место в тумане, но разведано, а здание осталось в памяти игрока.
        sim.Move(-30, 0, scout);
        sim.Seconds(0.5);
        Assert.False(sim.Host.Vision.Sees(1, 40.5, 40.5));
        Assert.True(sim.Host.Vision.Explored(1, 40.5, 40.5));
        Assert.True(sim.Host.Buildings.Remembers(1, yard));

        // Не побеждён — мод отказывает, и хост ничего не забывает.
        sim.Respawn(1);
        Assert.Empty(sim.TakeSignals(RespawnedSignal.Name));
        Assert.True(sim.Host.Vision.Explored(1, 40.5, 40.5));
        Assert.True(sim.Host.Buildings.Remembers(1, yard));

        // По велению администратора — начинает заново: туман закрыт, память о зданиях пуста.
        sim.Respawn(1, forced: true);
        Assert.Single(sim.TakeSignals(RespawnedSignal.Name));
        Assert.False(sim.Host.Vision.Explored(1, 40.5, 40.5));
        Assert.False(sim.Host.Vision.Explored(1, 7.5, 40.5));
        Assert.False(sim.Host.Buildings.Remembers(1, yard));
        Assert.Empty(sim.Host.Buildings.Known(1));
        // Здание второго игрока стоит, как стояло.
        Assert.True(sim.Alive(yard));
    }
}
