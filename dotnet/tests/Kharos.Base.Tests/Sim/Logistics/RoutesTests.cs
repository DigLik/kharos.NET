using Base.Shared.Orders;
using static Kharos.Base.Tests.Logistics.LogisticsScene;

namespace Kharos.Base.Tests.Logistics;

/// <summary>Маршруты, назначение и фильтр грузовиков (packages/tests/routes.test.ts).</summary>
public sealed class RoutesTests
{
    [Fact(DisplayName = "грузовик с маршрутом возит со склада на склад то, что принимает следующая остановка")]
    public void RouteCarriesWhatNextStopAccepts()
    {
        var (sim, buildings, truck, _, _) = Row(["metalYard", "metalYard"]);
        using var _sim = sim;
        var (from, to) = (buildings[0], buildings[1]);
        sim.SetStock(from, "metal", 100);
        sim.Send(1, new RouteOrder { Units = [truck], Stops = [from, to] });
        Until(sim, () => Amount(sim, to, "metal") >= 100);
        Assert.True(Amount(sim, from, "metal") < 1e-6);
        // Возить больше нечего: грузовик пустой ждёт, маршрут остаётся.
        sim.Seconds(3);
        Assert.Equal([from, to], sim.Hauler(truck).Route);
    }

    [Fact(DisplayName = "маршрут не вывозит резерв хранилища")]
    public void RouteKeepsReserve()
    {
        var (sim, buildings, truck, _, _) = Row(["metalYard", "metalYard"]);
        using var _sim = sim;
        var (from, to) = (buildings[0], buildings[1]);
        sim.SetStock(from, "metal", 100);
        sim.Send(1, new ReserveOrder { Buildings = [from], Amount = 60 });
        sim.Send(1, new RouteOrder { Units = [truck], Stops = [from, to] });
        Until(sim, () => Amount(sim, to, "metal") >= 40 - 1e-6);
        sim.Seconds(20);
        Assert.Equal(60, Amount(sim, from, "metal"), 6);
        Assert.Equal(40, Amount(sim, to, "metal"), 6);
    }

    [Fact(DisplayName = "фильтр: грузовик возит только разрешённое, и по маршруту тоже")]
    public void FilterAppliesToRoute()
    {
        var (sim, buildings, truck, _, _) = Row(["smelter", "metalYard"]);
        using var _sim = sim;
        var (smelter, yard) = (buildings[0], buildings[1]);
        sim.SetStock(smelter, "metal", 20);
        sim.Send(1, new FilterOrder { Units = [truck], Goods = ["silicon"] });
        sim.Send(1, new RouteOrder { Units = [truck], Stops = [smelter, yard] });
        sim.Seconds(20);
        Assert.Equal(0, Amount(sim, yard, "metal"));
        sim.Send(1, new FilterOrder { Units = [truck], Goods = [] });
        Until(sim, () => Amount(sim, yard, "metal") >= 20);
    }

    [Fact(DisplayName = "приказ идти снимает маршрут; с одной остановкой маршрута нет")]
    public void MoveClearsRoute()
    {
        var (sim, buildings, truck, _, _) = Row(["metalYard", "metalYard"]);
        using var _sim = sim;
        var (from, to) = (buildings[0], buildings[1]);
        sim.Send(1, new RouteOrder { Units = [truck], Stops = [from, from] });
        sim.Advance();
        Assert.Empty(sim.Hauler(truck).Route);
        sim.Send(1, new RouteOrder { Units = [truck], Stops = [from, to] });
        sim.Advance();
        Assert.Equal(2, sim.Hauler(truck).Route.Length);
        sim.Send(1, new MoveOrder { Units = [truck], X = 0, Y = 0 });
        sim.Advance();
        Assert.Empty(sim.Hauler(truck).Route);
    }

    [Fact(DisplayName = "фильтр действует и на работу от диспетчера: чужой груз грузовик не берёт")]
    public void FilterAppliesToDispatcher()
    {
        var (sim, buildings, truck, _, _) = Row(["smelter", "metalYard"]);
        using var _sim = sim;
        var (smelter, yard) = (buildings[0], buildings[1]);
        sim.SetStock(smelter, "metal", 20);
        sim.Send(1, new FilterOrder { Units = [truck], Goods = ["silicon", "fuel"] });
        sim.Seconds(20);
        Assert.Equal(0, Amount(sim, yard, "metal"));
        sim.Send(1, new FilterOrder { Units = [truck], Goods = ["metal"] });
        Until(sim, () => Amount(sim, yard, "metal") >= 20);
    }

    [Fact(DisplayName = "летающий грузовик возит по маршруту, как обычный: меньше за раз, но быстрее")]
    public void FlyingTruckFollowsRoute()
    {
        var (sim, buildings, truck, _, _) = Row(["metalYard", "metalYard"]);
        using var _sim = sim;
        var (from, to) = (buildings[0], buildings[1]);
        var (x, y) = sim.At(truck);
        sim.Erase(truck);
        var flyer = Spawn(sim, "airTruck", 1, (int)Math.Floor(x), (int)Math.Floor(y));
        Assert.True(sim.Has(flyer, "Hauler"));
        Assert.True(sim.Num(flyer, "Stock.capacity") < 25);
        sim.SetStock(from, "metal", 40);
        sim.Send(1, new RouteOrder { Units = [flyer], Stops = [from, to] });
        Until(sim, () => Amount(sim, to, "metal") >= 40);
    }

    [Fact(DisplayName = "фильтр снимает начатую работу с грузом, которого он не пускает")]
    public void FilterDropsStartedJob()
    {
        var (sim, buildings, truck, _, _) = Row(["smelter", "metalYard"]);
        using var _sim = sim;
        var smelter = buildings[0];
        sim.SetStock(smelter, "metal", 20);
        // Диспетчер дал работу: вывезти металл из плавильни в хранилище.
        Until(sim, () => sim.Hauler(truck).From == smelter);
        Assert.Equal("metal", sim.Hauler(truck).Resource);
        sim.Send(1, new FilterOrder { Units = [truck], Goods = ["silicon"] });
        sim.Advance();
        Assert.Equal(0u, sim.Hauler(truck).From);
        Assert.Equal(0u, sim.Hauler(truck).To);
        // И новую работу с металлом он не берёт.
        sim.Seconds(5);
        Assert.True(sim.Hauler(truck).From == 0 || sim.Hauler(truck).Resource != "metal");
    }

    [Fact(DisplayName = "назначенный на здания грузовик возит только по их заявкам, а груз берёт и в другой сети")]
    public void ServingTruckTakesCargoFromOtherNetwork()
    {
        var (sim, buildings, truck, _, _) = Row(["ammoBunker"]);
        using var _sim = sim;
        var bunker = buildings[0];
        sim.SetStock(bunker, "ammo", 200);
        // Застава из двух турелей в стороне от базы — отдельные сети без хранилищ.
        var (x, y) = sim.At(bunker);
        var mine = Place(sim, "turret", (int)x + 20, (int)y);
        var other = Place(sim, "turret", (int)x + 22, (int)y);
        sim.SetStock(mine, "ammo", 0);
        sim.SetStock(other, "ammo", 0);
        Assert.DoesNotContain(sim.Zones(1), zone => zone.Buildings.Contains(mine) && zone.Buildings.Contains(bunker));
        sim.Send(1, new ServeOrder { Units = [truck], Buildings = [mine] });
        // Турель заказывает, пока не наберёт три четверти запаса и больше.
        Until(sim, () => Amount(sim, mine, "ammo") >= sim.Num(mine, "Stock.capacity") * 0.75);
        sim.Seconds(20);
        Assert.Equal(0, Amount(sim, other, "ammo"));
    }

    [Fact(DisplayName = "назначение: чужие и без склада здания выбрасываются, приказ идти снимает его, снесённое выпадает")]
    public void ServeFiltersAndDrops()
    {
        var (sim, buildings, truck, _, _) = Row(["metalYard", "turret"]);
        using var _sim = sim;
        var (yard, turret) = (buildings[0], buildings[1]);
        sim.Send(1, new ServeOrder { Units = [truck], Buildings = [yard, turret, 99999] });
        sim.Advance();
        Assert.Equal([yard, turret], sim.Hauler(truck).Serve);
        sim.Erase(yard);
        sim.Seconds(1);
        Assert.Equal([turret], sim.Hauler(truck).Serve);
        sim.Send(1, new MoveOrder { Units = [truck], X = 0, Y = 0 });
        sim.Advance();
        Assert.Empty(sim.Hauler(truck).Serve);
    }

    [Fact(DisplayName = "маршрут снимает прежние приказы: назначение, обеспечение и дроп")]
    public void RouteClearsOtherOrders()
    {
        var (sim, buildings, truck, _, _) = Row(["metalYard", "metalYard"]);
        using var _sim = sim;
        var (from, to) = (buildings[0], buildings[1]);
        sim.Send(1, new ServeOrder { Units = [truck], Buildings = [to] });
        sim.Advance();
        Assert.Equal([to], sim.Hauler(truck).Serve);
        sim.SetHauler(truck, supply: to, pickup: from);
        sim.Send(1, new RouteOrder { Units = [truck], Stops = [from, to] });
        sim.Advance();
        var hauler = sim.Hauler(truck);
        Assert.Equal([from, to], hauler.Route);
        Assert.Empty(hauler.Serve);
        Assert.Equal(0u, hauler.Supply);
        Assert.Equal(0u, hauler.Pickup);
    }

    [Fact(DisplayName = "приказ с Shift прерывает маршрут: грузовик на маршруте не держит группу и идёт с ней")]
    public void ShiftOrderInterruptsRoute()
    {
        var (sim, buildings, truck, _, _) = Row(["metalYard", "metalYard"]);
        using var _sim = sim;
        var (from, to) = (buildings[0], buildings[1]);
        var at = sim.At(truck);
        var tank = Spawn(sim, "tank", 1, (int)Math.Floor(at.X) + 2, (int)Math.Floor(at.Y) + 1);
        sim.SetStock(from, "metal", 100);
        sim.Send(1, new RouteOrder { Units = [truck], Stops = [from, to] });
        sim.Seconds(1);
        Assert.Equal(2, sim.Hauler(truck).Route.Length);
        var goal = ((int)Math.Floor(at.X) + 6, (int)Math.Floor(at.Y) + 3);
        sim.Send(1, new MoveOrder { Units = [truck, tank], X = goal.Item1, Y = goal.Item2, Queue = true });
        sim.Seconds(0.2);
        // Маршрут конца не имеет: ждать его танку было бы вечно.
        Assert.Empty(sim.Hauler(truck).Route);
        Until(sim, () => Sim.Distance(sim.At(tank), goal) < 2.5, 30);
        Until(sim, () => Sim.Distance(sim.At(truck), goal) < 2.5, 30);
    }
}
