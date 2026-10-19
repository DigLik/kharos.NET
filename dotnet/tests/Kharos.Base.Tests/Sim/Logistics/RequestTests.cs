using Base.Shared.Orders;
using static Kharos.Base.Tests.Logistics.LogisticsScene;

namespace Kharos.Base.Tests.Logistics;

/// <summary>Станции запроса: заказ игрока и его доставка (packages/tests/request.test.ts).</summary>
public sealed class RequestTests
{
    private static readonly object Unprotected = new { protection = 0 };

    private static double Wanted(Sim sim, uint station, string good) => sim.Num(station, "Wanted." + good);

    [Fact(DisplayName = "станция запроса: грузовик довозит заказанное, лишнее после уменьшения заказа увозит в хранилище")]
    public void StationGetsOrderAndSurplusGoesBack()
    {
        var (sim, buildings, _, _, _) = Row(["metalYard", "requestStation"], rules: Unprotected);
        using var _sim = sim;
        var (yard, station) = (buildings[0], buildings[1]);
        sim.SetStock(yard, "metal", 100);
        sim.Send(1, new WantOrder { Buildings = [station], Resource = "metal", Amount = 40 });
        Until(sim, () => Amount(sim, station, "metal") >= 40 - 1e-6);
        sim.Seconds(10);
        Assert.Equal(40, Amount(sim, station, "metal"), 6);
        Assert.Equal(60, Amount(sim, yard, "metal"), 6);

        sim.Send(1, new WantOrder { Buildings = [station], Resource = "metal", Amount = 10 });
        Until(sim, () => Amount(sim, station, "metal") <= 10 + 1e-6);
        sim.Seconds(10);
        Assert.Equal(10, Amount(sim, station, "metal"), 6);
        Assert.Equal(90, Amount(sim, yard, "metal"), 6);
    }

    [Fact(DisplayName = "станция запроса в сети труб получает заказ по трубам")]
    public void StationInPipeNetworkGetsOrderByPipes()
    {
        var (sim, buildings, _, _, _) = Row(["metalYard", "requestStation"], truck: false, rules: Unprotected);
        using var _sim = sim;
        var (yard, station) = (buildings[0], buildings[1]);
        sim.ConnectAll(1);
        sim.SetStock(yard, "metal", 100);
        sim.Send(1, new WantOrder { Buildings = [station], Resource = "metal", Amount = 30 });
        Until(sim, () => Amount(sim, station, "metal") >= 30 - 1e-6, 60);
        sim.Seconds(10);
        Assert.Equal(30, Amount(sim, station, "metal"), 6);
    }

    [Fact(DisplayName = "станции запроса не берут друг у друга")]
    public void StationsDoNotTakeFromEachOther()
    {
        var (sim, buildings, _, _, _) = Row(["requestStation", "requestStation"], rules: Unprotected);
        using var _sim = sim;
        var (full, empty) = (buildings[0], buildings[1]);
        sim.Send(1, new WantOrder { Buildings = [full, empty], Resource = "metal", Amount = 50 });
        sim.SetStock(full, "metal", 50);
        sim.Seconds(30);
        Assert.Equal(50, Amount(sim, full, "metal"), 6);
        Assert.Equal(0, Amount(sim, empty, "metal"));
    }

    [Fact(DisplayName = "заказ: не больше склада всем грузом вместе, без руды, только свои станции")]
    public void OrderLimits()
    {
        var (sim, buildings, _, _, _) = Row(["requestStation", "metalYard"], truck: false, rules: Unprotected);
        using var _sim = sim;
        var (station, yard) = (buildings[0], buildings[1]);
        var capacity = sim.Num(station, "Stock.capacity");
        sim.OrderNow(1, new WantOrder { Buildings = [station], Resource = "metal", Amount = 70 });
        Assert.Equal(70, Wanted(sim, station, "metal"));
        sim.OrderNow(1, new WantOrder { Buildings = [station], Resource = "ammo", Amount = 1e9 });
        Assert.Equal((70.0, capacity - 70), (Wanted(sim, station, "metal"), Wanted(sim, station, "ammo")));
        sim.OrderNow(1, new WantOrder { Buildings = [station], Resource = "metal", Amount = 0 });
        Assert.Equal((0.0, capacity - 70), (Wanted(sim, station, "metal"), Wanted(sim, station, "ammo")));
        // Руду не заказывают, у хранилища заказа нет, чужой станции не заказать.
        sim.OrderNow(1, new WantOrder { Buildings = [station], Resource = "metalOre", Amount = 10 });
        Assert.Equal(0, Wanted(sim, station, "metalOre"));
        sim.OrderNow(1, new WantOrder { Buildings = [yard], Resource = "metal", Amount = 10 });
        Assert.False(sim.Has(yard, "Wanted"));
        sim.OrderNow(2, new WantOrder { Buildings = [station], Resource = "metal", Amount = 10 });
        Assert.Equal(0, Wanted(sim, station, "metal"));
    }

    [Fact(DisplayName = "юниты в кругах станции запроса пополняют магазины из её запаса")]
    public void UnitsRefillMagazinesFromStation()
    {
        var (sim, buildings, _, x, y) = Row(["requestStation"], truck: false, rules: Unprotected);
        using var _sim = sim;
        var station = buildings[0];
        sim.OrderNow(1, new WantOrder { Buildings = [station], Resource = "ammo", Amount = 100 });
        sim.SetStock(station, "ammo", 100);
        var tank = Spawn(sim, "tank", 1, x + 11, y + 3);
        sim.Set(tank, "Magazine.ammo", 0);
        sim.Seconds(10);
        Assert.Equal(sim.MagazineOf("tank"), sim.Rounds(tank), 2);
        Assert.Equal(100 - sim.MagazineOf("tank"), Amount(sim, station, "ammo"), 2);
    }

    [Fact(DisplayName = "кнопки заказа: груз, потом количество шагами, не больше склада за вычетом других заказов", Skip = "клиент (Н6): кнопки заказа в интерфейсе")]
    public void OrderButtons()
    {
    }
}
