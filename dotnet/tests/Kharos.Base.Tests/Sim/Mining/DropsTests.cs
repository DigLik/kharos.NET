using Base.Shared;
using Kharos.Base.Tests.Kernel;
using Tables = Kharos.Base.Tests.Kernel.Data;
using static Kharos.Base.Tests.Mining.MiningScene;

namespace Kharos.Base.Tests.Mining;

/// <summary>
/// Дропы (packages/tests/drops.test.ts): груз, брошенный на землю. Бросает его раздел Mining (Drops.Items, DropCargo);
/// разбор, стройка поверх и вывоз грузовиками ждут С1 и С2b (Skip).
/// </summary>
public sealed class DropsTests
{
    [Fact(DisplayName = "разобранное хранилище бросает груз на землю, свободный грузовик отвозит его в другое хранилище", Skip = "ждёт С1: разбор (demolish) и С2b: логистика (свободный грузовик отвозит дроп)")]
    public void DemolishedStoreDropsCargoAndHaulerCarriesItAway()
    {
    }

    [Fact(DisplayName = "стройка поверх дропа его уничтожает", Skip = "ждёт С1: стройка (build, clearDrops на площадке)")]
    public void BuildingOverDropDestroysIt()
    {
    }

    [Fact(DisplayName = "за дропом вне своих зон свободный грузовик сам не едет", Skip = "ждёт С2b: логистика (свободный грузовик, зоны)")]
    public void IdleHaulerIgnoresDropOutsideZones()
    {
    }

    [Fact(DisplayName = "посланный за дропом грузовик вывозит его весь, сколько бы рейсов ни понадобилось, и освобождается", Skip = "ждёт С2b: логистика (приказ pickup)")]
    public void SentHaulerClearsWholeDrop()
    {
    }

    [Fact(DisplayName = "груз с дропа, которому негде лежать, грузовик не берёт и ждёт; фильтр, не пускающий ничего с дропа, снимает приказ", Skip = "ждёт С2b: логистика (pickup, filter)")]
    public void HaulerWaitsWhenDropHasNowhereToGo()
    {
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

    [Fact(DisplayName = "разбор возвращает оплаченное вперёд: очередь юнитов и закупку с орбиты, чей корабль не прилетел", Skip = "ждёт С1: разбор (demolish, refundOf) и С2d: торговля (закупка с орбиты)")]
    public void DemolitionRefundsPaidInAdvance()
    {
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
