using Base.Shared.Queries;
using Kharos.Base.Tests.Kernel;

namespace Kharos.Base.Tests.Construction;

/// <summary>Здания (packages/tests/buildings.test.ts): занятость и зазор; чертежи — клиент.</summary>
public sealed class BuildingsTests
{
    /// <summary>Ближайший к началу мира тайл, где встаёт турель 1×1 (freeTile).</summary>
    private static (int X, int Y) FreeTile(Sim sim)
    {
        for (var y = 0; y < 400; y++)
            for (var x = 0; x < 400; x++)
                if (Scene.Fits(sim, "turret", x, y, 1, 1))
                    return (x, y);
        throw new InvalidOperationException("В мире не нашлось скалы");
    }

    private static Sim Create() => Scene.Create();

    [Fact(DisplayName = "занятость следит за появлением и исчезновением зданий")]
    public void OccupancyFollowsBuildings()
    {
        using var sim = Create();
        var (x, y) = FreeTile(sim);
        var turret = Scene.Place(sim, "turret", x, y, 0);
        Assert.Equal(turret, sim.OccupantAt(x, y));
        Assert.False(Scene.CanPlace(sim, "turret", x, y));

        sim.Erase(turret);
        Assert.Equal(0u, sim.OccupantAt(x, y));
        Assert.True(Scene.CanPlace(sim, "turret", x, y));
    }

    [Fact(DisplayName = "зазор не даёт ставить здания вплотную")]
    public void GapKeepsBuildingsApart()
    {
        using var sim = Create();
        var (x, y) = FreeTile(sim);
        Scene.Place(sim, "turret", x, y, 0);
        Assert.False(sim.Query<bool>(KernelQueries.CanPlace, new PlaceArgs { Type = "turret", X = x - 2, Y = y, Gap = 2 }));
    }

    [Fact(DisplayName = "каждый чертёж рисуется во всех кадрах и сообщает одни и те же огни", Skip = "клиент: чертежи зданий — Looks (К, Н6)")]
    public void EveryArtDraws()
    {
    }

    [Fact(DisplayName = "чертёж стены протягивает рукава только к соседям по четырём сторонам", Skip = "клиент: чертежи зданий — Looks (К, Н6)")]
    public void WallArtArms()
    {
    }
}
