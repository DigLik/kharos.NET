using Base.Client;
using Base.Shared.Queries;
using Kharos.Client.Game;
using Kharos.Client.Looks;
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

    [Fact(DisplayName = "каждый чертёж рисуется во всех кадрах и сообщает одни и те же огни")]
    public void EveryArtDraws()
    {
        // Все виды base на CLR: поток облика → растеризация клиента; число и порядок огней кадра не зависят ни от фазы, ни от работы (место огня может ходить: головка ремонта, стрела).
        foreach (var name in BuildingArt.Names)
        {
            var look = Look.Build(0, LookStream.Decode(BuildingArt.Look(name, new BuildingInfo(2, 2, BuildingKit.Role.Storage)).Encode()));
            Assert.NotEmpty(look.Images);
            Assert.Equal(look.Images.Count, look.FrameLights.Count);
            Assert.All(look.Images, image => Assert.Contains(image, b => b != 0));
            var per = look.Images.Count / look.Variants;
            for (var i = 0; i < look.FrameLights.Count; i++)
            {
                var first = look.FrameLights[i / per * per];
                Assert.True(look.FrameLights[i].Length == first.Length, $"{name}: кадр {i} — огней {look.FrameLights[i].Length}, в первом {first.Length}");
            }
        }
    }

    [Fact(DisplayName = "чертёж стены протягивает рукава только к соседям по четырём сторонам")]
    public void WallArtArms()
    {
        var look = Look.Build(0, LookStream.Decode(BuildingArt.Look("wall", new BuildingInfo(1, 1, BuildingKit.Role.Defense)).Encode()));
        Assert.Equal(16, look.Variants);
        var per = look.Images.Count / look.Variants;
        // Тайл стены лежит в кадре со сдвигом 16 пикселей поля; край тайла закрашен только там, где есть сосед.
        bool Solid(int variant, int x, int y) => look.Images[variant * per][((16 + y) * look.Width + 16 + x) * 4 + 3] != 0;
        var seen = new HashSet<string>();
        for (var mask = 0; mask < 16; mask++)
        {
            Assert.Equal((mask & 1) != 0, Solid(mask, 8, 0));
            Assert.Equal((mask & 2) != 0, Solid(mask, 15, 8));
            Assert.Equal((mask & 4) != 0, Solid(mask, 8, 15));
            Assert.Equal((mask & 8) != 0, Solid(mask, 0, 8));
            // Углы тайла не закрашены ни у одного варианта: рукава идут только по четырём сторонам.
            foreach (var (x, y) in new[] { (0, 0), (15, 0), (0, 15), (15, 15) })
                Assert.False(Solid(mask, x, y));
            Assert.True(seen.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(look.Images[mask * per]))), $"вариант {mask} повторяет другой");
        }
    }
}
