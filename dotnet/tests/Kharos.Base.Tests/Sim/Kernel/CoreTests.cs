using Base.Shared;
using Base.Shared.Orders;
using static Kharos.Base.Tests.Kernel.Scene;

namespace Kharos.Base.Tests.Kernel;

/// <summary>Ядро base (packages/tests/core.test.ts): кредиты, разворачивание и сворачивание MCV, производство.</summary>
public sealed class CoreTests
{
    private const double StartingCredits = 1500;
    private const double DeploySeconds = 3;

    /// <summary>Кредиты меняются ещё и доходом главного здания, 0,2 в секунду: сверка с допуском в кредит.</summary>
    private static void Near(double actual, double expected) => Assert.True(Math.Abs(actual - expected) <= 1, $"{actual} ≠ {expected} ± 1");

    private static double Cost(string unit) => Data.Units[unit].Cost;

    private static double BuildTime(string unit) => Data.Units[unit].BuildTime;

    [Fact(DisplayName = "игрок появляется с кредитами")]
    public void PlayerStartsWithCredits()
    {
        var (sim, _, _) = Start();
        using var _sim = sim;
        Assert.Equal(StartingCredits, sim.Credits(1));
        Assert.Equal(0, sim.Credits(2));
    }

    [Fact(DisplayName = "MCV разворачивается в главное здание и сворачивается обратно")]
    public void McvDeploysAndPacks()
    {
        var (sim, mcv, site) = Start();
        using var _sim = sim;
        Assert.True(CanDeploy(sim, 1, mcv));
        sim.Send(1, new DeployOrder { Unit = mcv });
        sim.Advance();
        Assert.True(sim.Has(mcv, Names.Converting.Name));
        // Пока разворачивается, с места не трогается и второй раз развернуть нельзя.
        sim.Send(1, new MoveOrder { Units = [mcv], X = site.X + 10, Y = site.Y });
        sim.Seconds(1);
        Assert.Equal(site.X + 1, (int)Math.Floor(sim.At(mcv).X));
        Assert.False(CanDeploy(sim, 1, mcv));

        sim.Seconds(2.1);
        Assert.False(sim.Alive(mcv));
        var building = Core(sim);
        Assert.NotEqual(0u, building);
        Assert.Equal((site.X, site.Y), ((int)sim.At(building).X, (int)sim.At(building).Y));
        Assert.Equal((double)site.X, sim.At(building).X);
        Assert.Equal(0, Units(sim, "mcv"));

        // Юниты, стоявшие на месте здания, вышли из него.
        sim.Seconds(3);
        foreach (var unit in sim.With(Names.Position.Name, Names.Unit.Name))
        {
            var (x, y) = sim.At(unit);
            Assert.Equal(0u, Occupant(sim, (int)Math.Floor(x), (int)Math.Floor(y)));
        }

        sim.Send(2, new PackOrder { Building = building });
        sim.Send(1, new PackOrder { Building = building });
        sim.Seconds(9);
        Assert.True(sim.Alive(building));
        sim.Seconds(1.2);
        Assert.False(sim.Alive(building));
        Assert.Equal(1, Units(sim, "mcv"));
        Assert.Equal(0u, Occupant(sim, site.X, site.Y));
    }

    [Fact(DisplayName = "развернуться можно только на свободной скале и только своим MCV")]
    public void DeployOnlyOnFreeRockAndOwnMcv()
    {
        var (sim, mcv, _) = Start();
        using var _sim = sim;
        Assert.False(CanDeploy(sim, 2, mcv));
        var infantry = UnitsOf(sim, "infantry")[0];
        Assert.False(CanDeploy(sim, 1, infantry));
        Assert.False(CanDeploy(sim, 1, 999999));

        // MCV на песке.
        for (var x = 0; x < 400; x++)
        {
            if (sim.Land!.TerrainAt(x, 0) == Kharos.Engine.Terrain.TerrainType.Mountain)
                continue;
            var sand = Spawn(sim, "mcv", 1, x, 0);
            if (!CanDeploy(sim, 1, sand))
                return;
            sim.Erase(sand);
        }
        Assert.Fail("Не нашлось места, где развернуться нельзя");
    }

    [Fact(DisplayName = "производство списывает кредиты, строит по очереди и выпускает юнитов рядом")]
    public void ProductionPaysQueuesAndReleasesNearby()
    {
        var (sim, @base, _) = Deployed();
        using var _sim = sim;
        var credits = sim.Credits(1);
        var (builders, trucks) = (Units(sim, "builder"), Units(sim, "truck"));
        sim.Send(1, new ProduceOrder { Producer = @base, Unit = "truck" });
        sim.Send(1, new ProduceOrder { Producer = @base, Unit = "builder" });
        sim.Advance();
        Near(sim.Credits(1), credits - Cost("truck") - Cost("builder"));
        Assert.Equal(["truck", "builder"], Queue(sim, @base));

        sim.Seconds(BuildTime("truck") + 0.1);
        Assert.Equal(trucks + 1, Units(sim, "truck"));
        Assert.Equal(builders, Units(sim, "builder"));
        sim.Seconds(BuildTime("builder") + 0.1);
        Assert.Equal(builders + 1, Units(sim, "builder"));
        Assert.Empty(Queue(sim, @base));

        // Каждый вышел на свой тайл, а не встал на голову соседу.
        var tiles = sim.With(Names.Position.Name, Names.Unit.Name).Select(unit => ((int)Math.Floor(sim.At(unit).X), (int)Math.Floor(sim.At(unit).Y))).ToList();
        Assert.Equal(tiles.Count, tiles.Distinct().Count());
    }

    [Fact(DisplayName = "в свёрнутой базе заказать нельзя: MCV не производит")]
    public void PackedBaseDoesNotProduce()
    {
        var (sim, mcv, _) = Start();
        using var _sim = sim;
        Assert.Empty(Producible(sim, mcv));
        sim.Send(1, new ProduceOrder { Producer = mcv, Unit = "builder" });
        sim.Advance();
        Assert.Empty(Queue(sim, mcv));
        Assert.Equal(StartingCredits, sim.Credits(1));
    }

    [Fact(DisplayName = "заказ отклоняется без кредитов, у чужого производителя, сверх очереди и для непроизводимого юнита")]
    public void OrderRefusedWithoutCreditsForeignOverQueueOrWrongUnit()
    {
        var (sim, @base, _) = Deployed();
        using var _sim = sim;
        var credits = sim.Credits(1);
        sim.Send(2, new ProduceOrder { Producer = @base, Unit = "truck" });
        sim.Send(1, new ProduceOrder { Producer = @base, Unit = "mcv" });
        sim.Send(1, new ProduceOrder { Producer = @base, Unit = "dragon" });
        sim.Advance();
        Assert.Empty(Queue(sim, @base));
        Near(sim.Credits(1), credits);

        // Очередь — не больше пяти заказов.
        for (var i = 0; i < 8; i++)
            sim.Send(1, new ProduceOrder { Producer = @base, Unit = "builder" });
        sim.Advance();
        Assert.Equal(5, Queue(sim, @base).Length);
        Near(sim.Credits(1), credits - 5 * Cost("builder"));

        // Готовый юнит освободил в очереди только одно место.
        sim.Seconds(BuildTime("builder") + 0.1);
        sim.Send(1, new ProduceOrder { Producer = @base, Unit = "builder" });
        sim.Send(1, new ProduceOrder { Producer = @base, Unit = "builder" });
        sim.Advance();
        Assert.Equal(5, Queue(sim, @base).Length);

        // Без кредитов заказ не принимается.
        var account = Account(sim, 1);
        sim.Set(account, "Player.credits", 0);
        sim.Set(account, "Player.earned", 0);
        sim.Send(1, new CancelProductionOrder { Producer = @base });
        sim.Advance();
        sim.Send(1, new ProduceOrder { Producer = @base, Unit = "truck" });
        sim.Advance();
        Assert.Equal(4, Queue(sim, @base).Length);
    }

    [Fact(DisplayName = "отмена возвращает кредиты за последний заказ")]
    public void CancelRefundsLastOrder()
    {
        var (sim, @base, _) = Deployed();
        using var _sim = sim;
        var credits = sim.Credits(1);
        sim.Send(1, new ProduceOrder { Producer = @base, Unit = "builder" });
        sim.Send(1, new ProduceOrder { Producer = @base, Unit = "truck" });
        sim.Send(1, new CancelProductionOrder { Producer = @base });
        sim.Advance();
        Assert.Equal(["builder"], Queue(sim, @base));
        Near(sim.Credits(1), credits - Cost("builder"));

        sim.Seconds(1);
        sim.Send(1, new CancelProductionOrder { Producer = @base });
        sim.Send(1, new CancelProductionOrder { Producer = @base });
        sim.Advance();
        Near(sim.Credits(1), credits);
        Assert.Equal(0, sim.Num(@base, "Producer.progress"));
    }

    [Fact(DisplayName = "очередь главного здания едет в свёрнутой базе и стоит, а после разворота продолжается")]
    public void QueueTravelsInPackedBase()
    {
        var (sim, @base, _) = Deployed();
        using var _sim = sim;
        sim.Send(1, new ProduceOrder { Producer = @base, Unit = "builder" });
        sim.Send(1, new ProduceOrder { Producer = @base, Unit = "truck" });
        sim.Send(1, new PackOrder { Building = @base });
        sim.Advance();
        var builders = Units(sim, "builder");
        sim.Seconds(10.1);
        var mcv = UnitsOf(sim, "mcv")[0];
        Assert.Equal(["builder", "truck"], Queue(sim, mcv));
        // Свёрнутая — не производит, сколько ни жди; отменить заказ можно.
        sim.Seconds(BuildTime("builder") + 1);
        Assert.Equal(builders, Units(sim, "builder"));
        sim.Send(1, new CancelProductionOrder { Producer = mcv });
        sim.Advance();
        Assert.Equal(["builder"], Queue(sim, mcv));

        sim.Send(1, new DeployOrder { Unit = mcv });
        sim.Seconds(3.1);
        Assert.Equal(["builder"], Queue(sim, Core(sim)));
        sim.Seconds(BuildTime("builder") + 0.1);
        Assert.Equal(builders + 1, Units(sim, "builder"));
    }

    [Fact(DisplayName = "сохранение посреди превращения и производства продолжается так же")]
    public void SaveDuringConversionAndProductionContinuesTheSame()
    {
        var (sim, @base, _) = Deployed();
        using var _sim = sim;
        sim.Send(1, new ProduceOrder { Producer = @base, Unit = "truck" });
        sim.Send(1, new PackOrder { Building = @base });
        sim.Seconds(1);
        using var copy = sim.ThroughSave();
        sim.Seconds(10);
        copy.Seconds(10);
        Assert.True(copy.SameWorld(sim));
    }

    [Fact(DisplayName = "MCV не разворачивается, пока под будущим зданием чужой юнит, а своих выгоняет")]
    public void McvWaitsForStrangerAndEvictsOwn()
    {
        var (sim, mcv, site) = Start();
        using var _sim = sim;
        var stranger = Spawn(sim, "infantry", 0, site.X, site.Y);
        var own = Spawn(sim, "builder", 1, site.X + 2, site.Y);
        sim.Send(1, new DeployOrder { Unit = mcv });
        sim.Seconds(8);
        // Свой строитель ушёл, чужой пехотинец стоит, MCV ждёт.
        var (x, y) = sim.At(own);
        Assert.False(x >= site.X && x < site.X + 3 && y >= site.Y && y < site.Y + 3);
        Assert.Equal(site.X, (int)Math.Floor(sim.At(stranger).X));
        Assert.Equal(0u, Core(sim));
        Assert.True(sim.Has(mcv, Names.Converting.Name));

        sim.Send(0, new MoveOrder { Units = [stranger], X = site.X - 3, Y = site.Y });
        sim.Seconds(5);
        Assert.NotEqual(0u, Core(sim));
    }

    /// <summary>Место в нескольких тайлах от MCV, где он сможет развернуться: левый верхний тайл основания.</summary>
    private static (int X, int Y) SiteAway(Sim sim, uint mcv, (int X, int Y) site)
    {
        for (var ring = 6; ring < 60; ring++)
            for (var dy = -ring; dy <= ring; dy++)
                for (var dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                        continue;
                    var (x, y) = (site.X + dx, site.Y + dy);
                    if (Rock(sim, x, y, 3, 3) && ClearOfCliffs(sim, x, y) && CanDeployAt(sim, 1, mcv, x, y))
                        return (x, y);
                }
        throw new InvalidOperationException("Рядом нет второго места под главное здание");
    }

    [Fact(DisplayName = "MCV с местом едет туда и разворачивается там")]
    public void McvDrivesToSiteAndDeploys()
    {
        var (sim, mcv, site) = Start();
        using var _sim = sim;
        var target = SiteAway(sim, mcv, site);
        sim.Send(2, new DeployOrder { Unit = mcv, At = true, X = target.X, Y = target.Y });
        sim.Send(1, new DeployOrder { Unit = mcv, At = true, X = target.X, Y = target.Y });
        sim.Advance();
        Assert.Equal((target.X, target.Y), ((int)sim.Num(mcv, "Deploys.x"), (int)sim.Num(mcv, "Deploys.y")));
        Assert.False(sim.Has(mcv, Names.Converting.Name));
        for (var i = 0; i < 600 && sim.Alive(mcv); i++)
            sim.Advance();
        Assert.False(sim.Alive(mcv));
        Assert.Equal(((double)target.X, (double)target.Y), sim.At(Core(sim)));
    }

    [Fact(DisplayName = "на своём месте MCV разворачивается сразу, а негодное место не принимает")]
    public void DeploysAtOwnSiteRightAwayAndRefusesBadSite()
    {
        var (sim, mcv, site) = Start();
        using var _sim = sim;
        // Вне карты — не принимается вовсе.
        sim.Send(1, new DeployOrder { Unit = mcv, At = true, X = -100000, Y = 0 });
        sim.Advance();
        Assert.False(sim.Has(mcv, Names.Deploys.Name));
        // Место под ним, но сдвинутое на тайл, — тоже на месте: ехать не нужно.
        sim.Send(1, new DeployOrder { Unit = mcv, At = true, X = site.X, Y = site.Y });
        sim.Advance();
        Assert.True(sim.Has(mcv, Names.Converting.Name));
        sim.Seconds(DeploySeconds + 0.2);
        Assert.Equal(((double)site.X, (double)site.Y), sim.At(Core(sim)));
    }

    [Fact(DisplayName = "поездку на разворачивание снимают отмена и приказ идти")]
    public void TripIsCancelledByCancelAndMove()
    {
        var (sim, mcv, site) = Start();
        using var _sim = sim;
        var target = SiteAway(sim, mcv, site);
        sim.Send(1, new DeployOrder { Unit = mcv, At = true, X = target.X, Y = target.Y });
        sim.Advance();
        sim.Send(1, new CancelDeployOrder { Unit = mcv });
        sim.Advance();
        Assert.False(sim.Has(mcv, Names.Deploys.Name));
        Assert.False(sim.Has(mcv, Names.Path.Name));

        sim.Send(1, new DeployOrder { Unit = mcv, At = true, X = target.X, Y = target.Y });
        sim.Advance();
        sim.Send(1, new MoveOrder { Units = [mcv], X = site.X - 4, Y = site.Y });
        sim.Advance();
        Assert.False(sim.Has(mcv, Names.Deploys.Name));
        sim.Seconds(30);
        Assert.Equal(0u, Core(sim));
    }

    [Fact(DisplayName = "место заняли, пока MCV ехал: он остаётся машиной")]
    public void SiteTakenWhileDriving()
    {
        var (sim, mcv, site) = Start();
        using var _sim = sim;
        var target = SiteAway(sim, mcv, site);
        sim.Send(1, new DeployOrder { Unit = mcv, At = true, X = target.X, Y = target.Y });
        sim.Advance();
        Place(sim, "generator", target.X, target.Y, 2);
        sim.Seconds(30);
        Assert.True(sim.Alive(mcv));
        Assert.False(sim.Has(mcv, Names.Deploys.Name));
        Assert.Equal(0u, Core(sim));
    }

    [Fact(DisplayName = "разворачивание можно отменить: MCV снова едет")]
    public void DeployCanBeCancelled()
    {
        var (sim, mcv, site) = Start();
        using var _sim = sim;
        Spawn(sim, "infantry", 0, site.X, site.Y);
        sim.Send(1, new DeployOrder { Unit = mcv });
        sim.Seconds(5);
        sim.Send(2, new CancelDeployOrder { Unit = mcv });
        sim.Send(1, new CancelDeployOrder { Unit = mcv });
        sim.Advance();
        Assert.False(sim.Has(mcv, Names.Converting.Name));
        Assert.Equal(0u, Core(sim));
        var before = sim.At(mcv);
        sim.Send(1, new MoveOrder { Units = [mcv], X = site.X + 6, Y = site.Y + 1 });
        sim.Seconds(2);
        Assert.NotEqual(before.X, sim.At(mcv).X);
    }

    [Fact(DisplayName = "MCV, которому не доехать до места, пробует снова всё реже и после пяти промахов бросает поездку")]
    public void UnreachableSiteIsDroppedAfterFiveMisses()
    {
        var (sim, mcv, site) = Start();
        using var _sim = sim;
        var target = SiteAway(sim, mcv, site);
        // Второй MCV заперт стенами на своём тайле: места ему не достать.
        var trapped = Spawn(sim, "mcv", 1, target.X + 1, target.Y + 1);
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                if (dx != 0 || dy != 0)
                    Place(sim, "wall", target.X + 1 + dx, target.Y + 1 + dy, 1);
        sim.Send(1, new DeployOrder { Unit = trapped, At = true, X = site.X, Y = site.Y });
        sim.Seconds(10);
        Assert.True(sim.Num(trapped, "Deploys.misses") > 1);
        sim.Seconds(60);
        Assert.False(sim.Has(trapped, Names.Deploys.Name));
    }

    [Fact(DisplayName = "место стало негодным, пока MCV ехал: чужая зона, площадка стройки, дорога — он остаётся машиной")]
    public void SiteSpoiledWhileDriving()
    {
        // Чужая зона строительства (здание игрока 2 рядом) — правило зон раздела Construction (С1); здесь — площадка и дорога.
        var spoilers = new List<Action<Sim, (int X, int Y)>>
        {
            (sim, target) =>
            {
                var site = sim.World.Spawn();
                sim.Add(site, Names.Position.Name);
                sim.Set(site, "Position.x", target.X + 1);
                sim.Set(site, "Position.y", target.Y);
                sim.Add(site, Names.Site.Name);
                sim.Add(site, Names.Footprint.Name);
                sim.Add(site, Names.Owner.Name);
                sim.Set(site, "Owner.player", 1);
            },
            (sim, target) =>
            {
                var road = sim.World.Spawn();
                sim.Add(road, Names.Position.Name);
                sim.Set(road, "Position.x", target.X + 2);
                sim.Set(road, "Position.y", target.Y + 2);
                sim.Add(road, Names.Pave.Name);
            },
        };
        foreach (var spoil in spoilers)
        {
            var (sim, mcv, site) = Start();
            using var _sim = sim;
            var target = SiteAway(sim, mcv, site);
            sim.Send(1, new DeployOrder { Unit = mcv, At = true, X = target.X, Y = target.Y });
            sim.Advance();
            spoil(sim, target);
            sim.Seconds(30);
            Assert.True(sim.Alive(mcv));
            Assert.False(sim.Has(mcv, Names.Deploys.Name));
            Assert.Equal(0u, Core(sim));
        }
    }

    [Fact(DisplayName = "место заняли, пока MCV разворачивался: он остаётся машиной")]
    public void SiteTakenWhileDeploying()
    {
        var (sim, mcv, site) = Start();
        using var _sim = sim;
        sim.Send(1, new DeployOrder { Unit = mcv });
        sim.Advance();
        Assert.True(sim.Has(mcv, Names.Converting.Name));
        Place(sim, "wall", site.X + 2, site.Y, 1);
        sim.Seconds(DeploySeconds + 0.2);
        Assert.True(sim.Alive(mcv));
        Assert.False(sim.Has(mcv, Names.Converting.Name));
        Assert.Equal(0u, Core(sim));
    }

    [Fact(DisplayName = "развёрнутая база уничтожает дропы под собой, но не рядом, и получает фазу по счёту зданий", Skip = "ждёт С2а: дропы (dropItems, dropAt)")]
    public void DeployedBaseClearsDropsUnderIt()
    {
    }

    [Fact(DisplayName = "свёрнутая база уносит прочность здания в MCV")]
    public void PackedBaseCarriesHealth()
    {
        var (sim, @base, _) = Deployed();
        using var _sim = sim;
        sim.Set(@base, "Health.value", 0.5);
        sim.Send(1, new PackOrder { Building = @base });
        sim.Seconds(10.2);
        var mcv = UnitsOf(sim, "mcv")[0];
        Assert.Equal(0.5, sim.Num(mcv, "Health.value"));
    }
}
