using static Kharos.Base.Tests.Logistics.LogisticsScene;

namespace Kharos.Base.Tests.Logistics;

/// <summary>Недоступные здания и запертые грузовики (packages/tests/unreachable.test.ts).</summary>
public sealed class UnreachableTests
{
    /// <summary>Кольцо стен вокруг прямоугольника (x, y, w, h) на расстоянии gap: внутрь технике не въехать.</summary>
    private static void Ring(Sim sim, int x, int y, int w, int h, int gap)
    {
        for (var i = x - gap - 1; i <= x + w + gap; i++)
        {
            Place(sim, "wall", i, y - gap - 1);
            Place(sim, "wall", i, y + h + gap);
        }
        for (var j = y - gap; j < y + h + gap; j++)
        {
            Place(sim, "wall", x - gap - 1, j);
            Place(sim, "wall", x + w + gap, j);
        }
    }

    /// <summary>Грузовик с рудой металла в кузове, который везёт её на переработку to (loaded).</summary>
    private static uint Loaded(Sim sim, int x, int y, uint to)
    {
        var truck = Spawn(sim, "truck", 1, x, y);
        sim.SetStock(truck, "metalOre", 20);
        sim.SetHauler(truck, from: 0, to: to, resource: "metalOre", amount: 20, full: true);
        return truck;
    }

    [Fact(DisplayName = "к обнесённой стеной переработке не подъехать: грузовик бросает рейс, руда ложится на землю, завод отмечен")]
    public void WalledRefineryIsMarked()
    {
        using var sim = Blank();
        var smelter = Place(sim, "smelter", 0, 0);
        Ring(sim, 0, 0, 3, 3, 2);
        var truck = Loaded(sim, -20, 1, smelter);
        sim.Seconds(20);
        var hauler = sim.Hauler(truck);
        Assert.True(sim.Unreachable(smelter));
        Assert.Equal(0u, hauler.To);
        Assert.False(hauler.Trapped);
        Assert.Equal(0, Amount(sim, truck, "metalOre"));
        Assert.Equal([20.0], sim.Drops().Select(drop => Amount(sim, drop, "metalOre")));
    }

    [Fact(DisplayName = "руда с недоступного завода едет на другой, куда подъехать можно")]
    public void OreGoesToReachableRefinery()
    {
        using var sim = Blank();
        var walled = Place(sim, "smelter", 0, 0);
        Ring(sim, 0, 0, 3, 3, 2);
        var open = Place(sim, "smelter", -40, 0);
        // Ближе к грузовику — обнесённый: на него руду и повезут первым делом.
        var truck = Loaded(sim, -12, 1, walled);
        sim.Seconds(60);
        Assert.True(sim.Unreachable(walled));
        Assert.False(sim.Unreachable(open));
        Assert.Equal(0, Amount(sim, truck, "metalOre"));
        Assert.True(Amount(sim, open, "metalOre") > 0);
        Assert.Empty(sim.Drops());
    }

    [Fact(DisplayName = "запертый грузовик отмечается сам, а здание, к которому он ехал, — нет; груз остаётся в кузове")]
    public void TrappedTruckMarksItself()
    {
        using var sim = Blank();
        var smelter = Place(sim, "smelter", 30, 0);
        var truck = Loaded(sim, 0, 0, smelter);
        Ring(sim, 0, 0, 1, 1, 1);
        sim.Seconds(20);
        Assert.True(sim.Hauler(truck).Trapped);
        Assert.False(sim.Has(smelter, "Unreachable"));
        Assert.Equal(20, Amount(sim, truck, "metalOre"));
    }
}
