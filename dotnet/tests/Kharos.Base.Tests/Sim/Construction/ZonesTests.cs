using Base.Shared.Orders;
using Kharos.Base.Tests.Kernel;
using static Kharos.Base.Tests.Construction.Yard;

namespace Kharos.Base.Tests.Construction;

/// <summary>Зоны строительства и сети труб (packages/tests/zones.test.ts).</summary>
public sealed class ZonesTests
{
    /// <summary>Ближайший к началу мира прямоугольник сплошной скалы width×height (rock).</summary>
    private static (int X, int Y) RockAt(Sim sim, int width, int height)
    {
        for (var y = -200; y < 200; y++)
            for (var x = -200; x < 200; x++)
                if (Rock(sim, x, y, width, height))
                    return (x, y);
        throw new InvalidOperationException("Не нашлось скалы");
    }

    [Fact(DisplayName = "шахта рядом с главным зданием — отдельная сеть, пока их не соединит труба")]
    public void MineIsSeparateUntilPiped()
    {
        using var sim = Create();
        var (x, y) = RockAt(sim, 16, 4);
        Scene.Place(sim, "command", x, y, 1);
        Scene.Place(sim, "mine", x + 10, y, 1);
        Assert.Equal(2, sim.Zones(1).Length);
        for (var tx = x + 3; tx < x + 10; tx++)
            Scene.Place(sim, "pipe", tx, y, 1);
        Assert.Single(sim.Zones(1));
        Assert.Equal(2 + 7, sim.Zones(1)[0].Buildings.Length);
    }

    [Fact(DisplayName = "сеть с главным зданием — первая; стена в сеть не входит")]
    public void CoreZoneFirstWallsApart()
    {
        using var sim = Create();
        var (x, y) = RockAt(sim, 24, 4);
        Scene.Place(sim, "generator", x + 20, y, 1);
        var core = Scene.Place(sim, "command", x, y, 1);
        var wall = Scene.Place(sim, "wall", x + 3, y, 1);
        Assert.Equal(core, sim.Zones(1)[0].Buildings[0]);
        Assert.DoesNotContain(sim.Zones(1), zone => zone.Buildings.Contains(wall));
    }

    [Fact(DisplayName = "после загрузки сохранения сети те же, и разрыв трубы снова их делит")]
    public void ZonesSurviveSave()
    {
        using var sim = Create();
        var (x, y) = RockAt(sim, 34, 4);
        Scene.Place(sim, "command", x, y, 1);
        var generator = Scene.Place(sim, "generator", x + 30, y, 1);
        Assert.Equal(2, sim.Zones(1).Length);
        for (var tx = x + 3; tx < x + 30; tx++)
            Scene.Place(sim, "pipe", tx, y, 1);
        Assert.Single(sim.Zones(1));
        using var loaded = sim.ThroughSave();
        Assert.Single(loaded.Zones(1));
        Assert.Contains(generator, loaded.Zones(1)[0].Buildings);
        loaded.Erase(loaded.OccupantAt(x + 20, y));
        loaded.Advance();
        Assert.Equal(2, loaded.Zones(1).Length);
    }

    [Fact(DisplayName = "стройка с подведённой трубой входит в сеть: ей сеть везёт материалы")]
    public void PipedSiteJoinsZone()
    {
        using var sim = Create();
        var (x, y) = RockAt(sim, 16, 4);
        Scene.Place(sim, "command", x, y, 1);
        Scene.Place(sim, "pipe", x + 3, y, 1);
        sim.SetCredits(1, sim.Credits(1) + 1000);
        sim.Send(1, new BuildOrder { Building = "factory", X = x + 4, Y = y });
        sim.Advance();
        Assert.Single(sim.Zones(1)[0].Sites);
    }
}
