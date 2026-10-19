using Base.Shared;
using Base.Shared.Orders;
using Kharos.Base.Tests.Kernel;
using static Kharos.Base.Tests.Logistics.LogisticsScene;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Logistics;

/// <summary>Строители сами возят материалы на свою стройку (packages/tests/fetching.test.ts).</summary>
public sealed class FetchingTests
{
    /// <summary>
    /// Главное здание игрока 1 на просторной скале, один строитель, грузовиков нет, а поодаль, не вплотную и без трубы, —
    /// склад металла с запасом: его стройке никто не привезёт, кроме строителя (start).
    /// </summary>
    private static (Sim Sim, uint Builder, uint Yard, (int X, int Y) Site) Start()
    {
        var sim = Create(512, new { techTree = false });
        for (var y = 0; y < 400; y++)
            for (var x = 0; x < 400; x++)
            {
                if (!Rock(sim, x, y, 16, 16))
                    continue;
                sim.Edit(new EditStartOrder { Player = 1, X = x + 2, Y = y + 2 });
                sim.Send(1, new DeployOrder { Unit = Scene.UnitsOf(sim, "mcv")[0] });
                sim.Seconds(6);
                foreach (var unit in Scene.UnitsOf(sim, "truck").Concat(Scene.UnitsOf(sim, "builder")))
                    sim.Erase(unit);
                var builder = Builders.AddBuilders(sim, 1, x + 2, y + 2, 1)[0];
                var yard = Place(sim, "metalYard", x + 8, y + 11);
                sim.SetStock(yard, "metal", 50);
                return (sim, builder, yard, (x + 9, y + 3));
            }
        throw new InvalidOperationException("В мире не нашлось места под базу");
    }

    [Fact(DisplayName = "строитель сам возит материал со склада на свою стройку, рейсами по кузову, и достраивает её")]
    public void BuilderFetchesMaterialInTrips()
    {
        var (sim, builder, yard, site) = Start();
        using var _sim = sim;
        sim.Send(1, new BuildOrder { Building = "spaceport", X = site.X, Y = site.Y, Builders = [builder] });
        sim.Seconds(1);
        var building = sim.SiteAt(site.X, site.Y);
        Assert.NotEqual(0u, building);
        Assert.DoesNotContain(sim.Zones(1), zone => zone.Sites.Contains(building) && zone.Buildings.Contains(yard));
        // Пока стройка не встала без материала, строитель строит, а не едет.
        var fetched = false;
        var carried = 0.0;
        for (var i = 0; i < Sim.TicksIn(120) && sim.Has(building, Names.Site.Name); i++)
        {
            sim.Advance();
            if (sim.FetchOf(builder) is { } fetch)
            {
                fetched = true;
                Assert.Equal(building, fetch.To);
                // Что везёт строитель, учитывают заявки: грузовики и трубы того же не повезут.
                Assert.True(sim.Incoming(building, "metal") > 0);
            }
            carried = Math.Max(carried, Amount(sim, builder, "metal"));
        }
        Assert.True(fetched);
        // Кузов — 10, космопорту нужно 20: два рейса.
        Assert.True(carried <= 10 + 1e-6, $"{carried}");
        Assert.False(sim.Has(building, Names.Site.Name));
        Assert.Equal(50 - Tables.Buildings["spaceport"].Materials["metal"], Amount(sim, yard, "metal"), 3);
        // Построил — и снова свободен, кузов пуст.
        Assert.Null(sim.FetchOf(builder));
        Assert.Equal(0, Amount(sim, builder, "metal"), 3);
    }

    [Fact(DisplayName = "приказ идти снимает поручение; свободный строитель с остатком отвозит его в хранилище")]
    public void MoveDropsFetchAndLeftoverGoesToStore()
    {
        var (sim, builder, yard, site) = Start();
        using var _sim = sim;
        sim.Send(1, new BuildOrder { Building = "spaceport", X = site.X, Y = site.Y, Builders = [builder] });
        // Ждём, пока наберёт груз.
        for (var i = 0; i < Sim.TicksIn(60) && !(sim.FetchOf(builder)?.Full ?? false); i++)
            sim.Advance();
        Assert.True(sim.FetchOf(builder)?.Full);
        Assert.True(Amount(sim, builder, "metal") > 0);
        // Стройку отменили: увести строителя — поручение снято, груз в кузове.
        var building = sim.SiteAt(site.X, site.Y);
        sim.Send(1, new CancelBuildOrder { Site = building });
        sim.Send(1, new MoveOrder { Units = [builder], X = site.X, Y = site.Y + 6 });
        sim.Advance();
        Assert.Null(sim.FetchOf(builder));
        Assert.False(sim.Has(builder, Names.Builds.Name));
        // Доехал и стоит без работы — везёт остаток назад на склад.
        sim.Seconds(40);
        Assert.Equal(0, Amount(sim, builder, "metal"), 3);
        Assert.Equal(50, Amount(sim, yard, "metal"), 3);
    }

    [Fact(DisplayName = "поручение строителя переживает сохранение")]
    public void FetchSurvivesSave()
    {
        var (sim, builder, _, site) = Start();
        using var _sim = sim;
        sim.Send(1, new BuildOrder { Building = "spaceport", X = site.X, Y = site.Y, Builders = [builder] });
        for (var i = 0; i < Sim.TicksIn(60) && sim.FetchOf(builder) is null; i++)
            sim.Advance();
        var fetch = sim.FetchOf(builder);
        Assert.NotNull(fetch);
        using var copy = sim.ThroughSave();
        Assert.Equal(fetch, copy.FetchOf(builder));
    }
}
