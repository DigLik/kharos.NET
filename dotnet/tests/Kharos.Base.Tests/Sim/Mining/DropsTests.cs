using Base.Shared;
using Base.Shared.Orders;
using Kharos.Base.Tests.Construction;
using Kharos.Base.Tests.Kernel;
using Tables = Kharos.Base.Tests.Kernel.Data;
using static Kharos.Base.Tests.Mining.MiningScene;

namespace Kharos.Base.Tests.Mining;

/// <summary>
/// Дропы (packages/tests/drops.test.ts): груз, брошенный на землю. Бросает его раздел Mining (Drops.Items, DropCargo);
/// вывозят грузовики (раздел Logistics); закупку с орбиты возвращает разбор космопорта (раздел Trade).
/// </summary>
public sealed class DropsTests
{
    [Fact(DisplayName = "разобранное хранилище бросает груз на землю, свободный грузовик отвозит его в другое хранилище")]
    public void DemolishedStoreDropsCargoAndHaulerCarriesItAway()
    {
        var (sim, buildings, _, x, y) = Logistics.LogisticsScene.Row(["metalYard", "metalYard"]);
        using var _sim = sim;
        var (gone, kept) = (buildings[0], buildings[1]);
        var builder = Logistics.LogisticsScene.Spawn(sim, "builder", 1, x + 13, y + 4);
        sim.SetStock(gone, "metal", 50);
        sim.Send(1, new DemolishOrder { Building = gone, Builders = [builder] });
        Assert.True(sim.Until(() => !sim.Alive(gone), 120));
        var drop = Assert.Single(sim.Drops());
        Assert.Equal(50, sim.Stock(drop, "metal"));
        Assert.True(sim.Until(() => sim.Stock(kept, "metal") >= 50, 120));
        // Пустой дроп исчезает.
        sim.Advance();
        Assert.Empty(sim.Drops());
    }


    [Fact(DisplayName = "стройка поверх дропа его уничтожает")]
    public void BuildingOverDropDestroysIt()
    {
        using var yard = Yard.Start();
        var (sim, site) = (yard.Sim, yard.Site);
        Assert.NotEqual(0u, sim.DropItems(site.X + 1, site.Y, ("metal", 10)));
        Assert.Single(sim.Drops());
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [.. yard.Builders] });
        sim.Seconds(30);
        Assert.Empty(sim.Drops());
    }

    [Fact(DisplayName = "разобранное хранилище бросает свой груз на землю (вывоз грузовиком ждёт С2b)")]
    public void DemolishedStoreDropsItsCargo()
    {
        using var yard = Yard.Start();
        var (sim, site) = (yard.Sim, yard.Site);
        var store = sim.PutBuilding("metalYard", site.X, site.Y, 1);
        sim.SetStock(store, "metal", 50);
        sim.Send(1, new DemolishOrder { Building = store, Builders = [.. yard.Builders] });
        sim.Seconds(60);
        Assert.False(sim.Alive(store));
        var drop = Assert.Single(sim.Drops());
        Assert.Equal(50, sim.Stock(drop, "metal"));
    }
    [Fact(DisplayName = "за дропом вне своих зон свободный грузовик сам не едет")]
    public void IdleHaulerIgnoresDropOutsideZones()
    {
        var (sim, _, truck, x, y) = Logistics.LogisticsScene.Row(["metalYard"]);
        using var _sim = sim;
        var drop = sim.DropItems(x + 400, y, ("metal", 10));
        var start = sim.At(truck);
        sim.Seconds(5);
        Assert.Equal(10, sim.Stock(drop, "metal"));
        Assert.Equal(start, sim.At(truck));
    }

    [Fact(DisplayName = "посланный за дропом грузовик вывозит его весь, сколько бы рейсов ни понадобилось, и освобождается")]
    public void SentHaulerClearsWholeDrop()
    {
        var (sim, buildings, truck, x, y) = Logistics.LogisticsScene.Row(["metalYard"]);
        using var _sim = sim;
        var store = buildings[0];
        var capacity = Tables.Units["truck"].Inventory!.Value;
        var drop = sim.DropItems(x + 60, y, ("metal", capacity * 2));
        sim.Send(1, new PickupOrder { Units = [truck], Drop = drop });
        Assert.True(sim.Until(() => !sim.Alive(drop), 600));
        Assert.True(sim.Until(() => sim.Stock(store, "metal") >= capacity * 2, 120));
        Assert.True(sim.Until(() => sim.Hauler(truck).Pickup == 0, 10));
    }

    [Fact(DisplayName = "груз с дропа, которому негде лежать, грузовик не берёт и ждёт; фильтр, не пускающий ничего с дропа, снимает приказ")]
    public void HaulerWaitsWhenDropHasNowhereToGo()
    {
        var (sim, _, truck, x, y) = Logistics.LogisticsScene.Row(["metalYard"]);
        using var _sim = sim;
        var drop = sim.DropItems(x + 20, y, ("fuel", 3));
        sim.Send(1, new PickupOrder { Units = [truck], Drop = drop });
        sim.Seconds(30);
        Assert.Equal(3, sim.Stock(drop, "fuel"));
        Assert.Equal(drop, sim.Hauler(truck).Pickup);
        sim.Send(1, new FilterOrder { Units = [truck], Goods = ["metal"] });
        Assert.True(sim.Until(() => sim.Hauler(truck).Pickup == 0, 10));
    }

    [Fact(DisplayName = "груз падает к уже лежащему дропу того же тайла")]
    public void CargoFallsToExistingDropOfTheSameTile()
    {
        using var sim = Create();
        sim.DropItems(10 + 2.4, 10 + 6.7, ("metal", 3));
        sim.DropItems(10 + 2, 10 + 6, ("fuel", 2), ("metalOre", 1));
        var drop = Assert.Single(sim.Drops());
        Assert.Equal([3, 2, 1], [sim.Stock(drop, "metal"), sim.Stock(drop, "fuel"), sim.Stock(drop, "metalOre")]);
    }

    [Fact(DisplayName = "разбор возвращает оплаченное вперёд: очередь юнитов и закупку с орбиты, чей корабль не прилетел")]
    public void DemolitionRefundsPaidInAdvance()
    {
        var (sim, buildings, _, x, y) = Logistics.LogisticsScene.Row(["barracks", "spaceport"]);
        using var _sim = sim;
        var (barracks, port) = (buildings[0], buildings[1]);
        var builder = Logistics.LogisticsScene.Spawn(sim, "builder", 1, x + 13, y + 4);
        sim.Set(barracks, "Producer.queue", Scene.Ids(Scene.Proto(sim, "units/infantry"), Scene.Proto(sim, "units/infantry"), Scene.Proto(sim, "units/rocketeer")));
        sim.PutTrade(port, "metal", 10, left: 100000, total: 100000, buy: true);
        var queued = Tables.Units["infantry"].Cost * 2 + Tables.Units["rocketeer"].Cost;
        var before = sim.Credits(1);
        sim.Send(1, new DemolishOrder { Building = barracks, Builders = [builder] });
        Assert.True(sim.Until(() => !sim.Alive(barracks), 120));
        // Сверху — доход главного здания за время разбора.
        var gained = sim.Credits(1) - before;
        var refund = global::Base.Shared.Rules.BuildRules.RefundOf(Tables.Buildings["barracks"]);
        Assert.True(gained >= refund + queued, $"{gained} < {refund + queued}");
        Assert.True(gained < refund + queued + 40, $"{gained} >= {refund + queued + 40}");

        var middle = sim.Credits(1);
        sim.Send(1, new DemolishOrder { Building = port, Builders = [builder] });
        Assert.True(sim.Until(() => !sim.Alive(port), 120));
        var back = sim.Credits(1) - middle;
        var bought = 10 * global::Base.Shared.Rules.TradeRules.BuyPrice(Tables.All.Resources["metal"].Price);
        var portRefund = global::Base.Shared.Rules.BuildRules.RefundOf(Tables.Buildings["spaceport"]);
        Assert.True(back >= portRefund + bought, $"{back} < {portRefund + bought}");
        Assert.True(back < portRefund + bought + 60, $"{back} >= {portRefund + bought + 60}");
    }

    [Fact(DisplayName = "дроп — склад без хозяина на одном тайле: места ровно под груз")]
    public void DropIsOwnerlessStockSizedToItsCargo()
    {
        using var sim = Create();
        var drop = sim.DropItems(20.9, 30.2, ("metal", 3), ("fuel", 2), ("khariteOre", 0.5));
        Assert.NotEqual(0u, drop);
        Assert.Equal((20.0, 30.0), sim.At(drop));
        Assert.False(sim.Has(drop, Names.Owner.Name));
        Assert.Equal(5.5, sim.Num(drop, "Stock.capacity"));
        // Ещё груза на тот же тайл — вместимость растёт на него, а не пропадает.
        sim.DropItems(20, 30, ("metal", 1.5));
        Assert.Single(sim.Drops());
        Assert.Equal(4.5, sim.Stock(drop, "metal"));
        Assert.Equal(7, sim.Num(drop, "Stock.capacity"));
        // Другой тайл — другой дроп.
        Assert.NotEqual(drop, sim.DropItems(21, 30, ("metal", 1)));
        Assert.Equal(2, sim.Drops().Count);
    }

    [Fact(DisplayName = "нечего бросать — дропа нет: пустое и крохи меньше эпсилон не бросают")]
    public void NothingToDropMakesNoDrop()
    {
        using var sim = Create();
        Assert.Equal(0u, sim.DropItems(5, 5, ("metal", 0), ("fuel", 1e-12)));
        Assert.Empty(sim.Drops());
    }

    [Fact(DisplayName = "пустой дроп исчезает на следующем тике, полный остаётся")]
    public void EmptyDropVanishes()
    {
        using var sim = Create();
        var full = sim.DropItems(5, 5, ("metal", 4));
        var empty = sim.DropItems(8, 5, ("metal", 4));
        sim.SetStock(empty, "metal", 0);
        sim.Advance();
        Assert.True(sim.Alive(full));
        Assert.False(sim.Alive(empty));
        Assert.Equal(full, Assert.Single(sim.Drops()));
    }

    [Fact(DisplayName = "груз здания падает дропом на тайл в середине основания, груз юнита — на его тайл")]
    public void CargoOfBuildingAndUnitFalls()
    {
        using var sim = Create();
        var spot = Of(sim, "metal");
        var mine = MineAt(sim, spot);
        sim.SetStock(mine, "metalOre", 10);
        sim.DropCargo(mine);
        // Шахта 2×2: середина основания — тайл (x + 1, y + 1).
        var drop = sim.DropAt(spot.X + 1, spot.Y + 1);
        Assert.NotEqual(0u, drop);
        Assert.Equal(10, sim.Stock(drop, "metalOre"));
        // Склад самой шахты dropCargo не трогает: его убирает тот, кто убирает шахту.
        Assert.Equal(10, sim.Stock(mine, "metalOre"));
        var tiles = FreeTiles(sim, spot, 2);
        var truck = sim.PutUnit("truck", 1, tiles[0].X + 0.5, tiles[0].Y + 0.5);
        sim.SetStock(truck, "silicon", 7);
        sim.DropCargo(truck);
        Assert.Equal(7, sim.Stock(sim.DropAt(tiles[0].X, tiles[0].Y), "silicon"));
        // Пустой склад бросать нечего.
        var count = sim.Drops().Count;
        sim.DropCargo(sim.PutUnit("truck", 1, tiles[1].X + 0.5, tiles[1].Y + 0.5));
        Assert.Equal(count, sim.Drops().Count);
    }
}
