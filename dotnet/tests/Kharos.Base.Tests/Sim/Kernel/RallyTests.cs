using Base.Shared;
using Base.Shared.Orders;
using static Kharos.Base.Tests.Kernel.Scene;

namespace Kharos.Base.Tests.Kernel;

/// <summary>Точка сбора и выпуск готовых (packages/tests/rally.test.ts).</summary>
public sealed class RallyTests
{
    /// <summary>Место под главное здание у начала мира: и (x, y), и (x + 3, y + 3) годятся.</summary>
    private static (int X, int Y) Spot(Sim sim)
    {
        for (var y = -60; y < 60; y++)
            for (var x = -60; x < 60; x++)
                if (Fits(sim, "command", x, y, 3, 3) && Fits(sim, "command", x + 3, y + 3, 3, 3))
                    return (x, y);
        throw new InvalidOperationException("нет места");
    }

    /// <summary>Главное здание игрока 1 на свободной скале (base).</summary>
    private static (Sim Sim, (int X, int Y) Spot, uint Core) Base()
    {
        var sim = Create(128);
        var spot = Spot(sim);
        return (sim, spot, Place(sim, "command", spot.X, spot.Y, 1));
    }

    /// <summary>Первый новый юнит за seconds секунд; нет — 0 (produced).</summary>
    private static uint Produced(Sim sim, double seconds)
    {
        var before = sim.With(Names.Unit.Name).ToHashSet();
        for (var i = 0; i < seconds * 20; i++)
        {
            sim.Advance();
            foreach (var entity in sim.With(Names.Unit.Name))
                if (!before.Contains(entity))
                    return entity;
        }
        return 0;
    }

    /// <summary>Очередь производителя видами — прямо в мир (Producer.queue — номера Proto).</summary>
    private static void SetQueue(Sim sim, uint producer, params string[] units)
    {
        var ids = units.Select(unit => Proto(sim, "units/" + unit)).ToArray();
        sim.Set(producer, "Producer.queue", Ids(ids));
        sim.Set(producer, "Producer.current", ids.Length > 0 ? ids[0] : -1);
    }

    [Fact(DisplayName = "точка сбора: готовый юнит сам едет к ней; чужому зданию её не поставить")]
    public void ProducedUnitDrivesToRallyPoint()
    {
        using var sim = Create(128);
        sim.SetCredits(1, 10000);
        var spot = Spot(sim);
        var core = Place(sim, "command", spot.X, spot.Y, 1);
        // Чужой не поставит, неверный тайл не примется.
        sim.Send(2, new RallyOrder { Building = core, X = spot.X + 8, Y = spot.Y });
        sim.Advance();
        Assert.Equal(0, sim.Num(core, "Producer.rally"));
        sim.Send(1, new RallyOrder { Building = core, X = spot.X + 8, Y = spot.Y + 1 });
        sim.Send(1, new ProduceOrder { Producer = core, Unit = "builder" });
        sim.Advance();
        Assert.Equal((1.0, spot.X + 8.0, spot.Y + 1.0), (sim.Num(core, "Producer.rally"), sim.Num(core, "Producer.rallyX"), sim.Num(core, "Producer.rallyY")));
        var before = sim.With(Names.Unit.Name).ToHashSet();
        uint made = 0;
        var ticks = 1;
        for (; ticks < 20 * 60 && made == 0; ticks++)
        {
            sim.Advance();
            made = sim.With(Names.Unit.Name).FirstOrDefault(entity => !before.Contains(entity));
        }
        Assert.NotEqual(0u, made);
        // Готов ровно к сроку и сразу приносит награду за первый юнит.
        Assert.Equal(BuildTicks(sim, "builder"), ticks);
        Assert.Equal(["unit"], Rewards(sim, 1));
        // Наземный едет к точке напрямую, без поиска пути.
        Assert.Equal(1, sim.Num(made, "Path.direct"));
        Assert.True(Math.Sqrt(Math.Pow(sim.Num(made, "Path.goalX") - (spot.X + 8), 2) + Math.Pow(sim.Num(made, "Path.goalY") - (spot.Y + 1), 2)) < 2);
    }

    [Fact(DisplayName = "летающий едет точно в точку сбора, даже над зданием; наземный — на свободный тайл рядом")]
    public void FlyerGoesExactlyToRallyGroundUnitNextToIt()
    {
        var (sim, spot, core) = Base();
        using var _sim = sim;
        var middle = (X: spot.X + 1, Y: spot.Y + 1);
        sim.Set(core, "Producer.rally", 1);
        sim.Set(core, "Producer.rallyX", middle.X);
        sim.Set(core, "Producer.rallyY", middle.Y);
        SetQueue(sim, core, "drone", "builder");
        foreach (var (good, amount) in Data.Units["drone"].Materials)
            sim.Set(core, "Stock." + good, amount);
        var drone = Produced(sim, Data.Units["drone"].BuildTime + 1);
        Assert.Equal("drone", TypeOf(sim, drone));
        Assert.Equal((middle.X, middle.Y), ((int)sim.Num(drone, "Path.goalX"), (int)sim.Num(drone, "Path.goalY")));
        var builder = Produced(sim, Data.Units["builder"].BuildTime + 1);
        // Тайл сбора наземного может оказаться тем, где он вышел: тогда ехать некуда.
        var goal = sim.Has(builder, Names.Path.Name)
            ? ((int)sim.Num(builder, "Path.goalX"), (int)sim.Num(builder, "Path.goalY"))
            : ((int)Math.Floor(sim.At(builder).X), (int)Math.Floor(sim.At(builder).Y));
        Assert.False(goal.Item1 >= spot.X && goal.Item1 < spot.X + 3 && goal.Item2 >= spot.Y && goal.Item2 < spot.Y + 3);
    }

    [Fact(DisplayName = "производитель вне зоны энергии, которому она нужна, не производит")]
    public void ProducerOutsidePowerZoneStands()
    {
        var (sim, spot, _) = Base();
        using var _sim = sim;
        (int X, int Y)? far = null;
        for (var y = spot.Y - 60; y < spot.Y + 60 && far is null; y++)
            for (var x = spot.X + 40; x < spot.X + 100 && far is null; x++)
                if (Fits(sim, "barracks", x, y, 3, 2))
                    far = (x, y);
        var barracks = Place(sim, "barracks", far!.Value.X, far.Value.Y, 1);
        SetQueue(sim, barracks, "infantry");
        Assert.Equal(0u, Produced(sim, Data.Units["infantry"].BuildTime + 2));
        Assert.Equal(["infantry"], Queue(sim, barracks));
        Assert.Equal(0, sim.Num(barracks, "Producer.progress"));
    }

    [Fact(DisplayName = "здание под разбором не производит, хотя очередь у него остаётся")]
    public void DemolishedBuildingKeepsQueueButStands()
    {
        var (sim, _, core) = Base();
        using var _sim = sim;
        SetQueue(sim, core, "builder");
        sim.Add(core, Names.Site.Name);
        sim.Set(core, "Site.progress", 1000);
        sim.Set(core, "Site.demolish", 1);
        Assert.Equal(0u, Produced(sim, Data.Units["builder"].BuildTime + 2));
        Assert.Equal(["builder"], Queue(sim, core));
        Assert.Equal(0, sim.Num(core, "Producer.progress"));
    }
}
