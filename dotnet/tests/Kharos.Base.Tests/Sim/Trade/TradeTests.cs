using Base.Shared.Orders;
using Base.Shared.Rules;
using Kharos.Base.Tests.Kernel;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Trade;

/// <summary>
/// Космопорт (trade.ts) сверх сценариев TS: проверка заявки, закупка с орбиты, закрытие заявки, сохранение корабля в пути.
/// Сценарии TS с космопортом — в EconomyTests, MiningTests, DropsTests и LithiumTests.
/// </summary>
public sealed class TradeTests
{
    private static readonly object Rules = new { protection = 0, techTree = false };

    private static double Price(string good) => Tables.All.Resources[good].Price;

    private static (Sim Sim, uint Port, uint Yard) Make(params string[] extra)
    {
        var sim = Scene.Create(128, Rules);
        var one = FerryTests.MakeBase(sim, 1, ["spaceport", "metalYard", .. extra]);
        sim.SetCredits(1, 5000);
        return (sim, one.Buildings[1], one.Buildings[2]);
    }

    [Fact(DisplayName = "заявка на продажу: чужой порт, лишнее и второй раз — отказ; заявка не больше запаса игрока")]
    public void SaleOrderIsCheckedAgainstTheStock()
    {
        var (sim, port, yard) = Make();
        using var _sim = sim;
        sim.SetStock(yard, "metal", 30);
        Assert.False(sim.Sell(2, port, "metal", 10));
        Assert.False(sim.Sell(1, port, "metal", 0.5));
        Assert.False(sim.Sell(1, port, "fuel", 10));
        Assert.False(sim.Sell(1, port, "metalOre", 10));
        Assert.Null(sim.TradeOf(port));
        Assert.True(sim.Sell(1, port, "metal", 1000));
        var order = sim.TradeOf(port)!;
        Assert.Equal(("metal", 30.0, 0.0, false), (order.Resource, order.Wanted, order.Total, order.Buy));
        Assert.Equal(0, sim.Delivered(port));
        Assert.False(sim.CanSell(1, port));
        Assert.False(sim.Sell(1, port, "metal", 5));
        Assert.Equal(30, sim.StockOf(1)["metal"]);
    }

    [Fact(DisplayName = "привезённый товар улетает кораблём: кредиты по цене, награда за первую продажу, заявку можно закрыть раньше")]
    public void DeliveredGoodsFlyAwayAndSaleCanBeClosed()
    {
        var (sim, port, yard) = Make();
        using var _sim = sim;
        sim.SetStock(yard, "metal", 30);
        Assert.True(sim.Sell(1, port, "metal", 30));
        // Привезли часть: закрытие улетает с привезённым.
        sim.SetStock(port, "metal", 12);
        Assert.Equal(12, sim.Delivered(port));
        Assert.False(sim.Sell(1, port, "metal", 5));
        sim.Send(1, new CloseSaleOrder { Port = port });
        sim.Advance();
        Assert.Equal(12, sim.TradeOf(port)!.Wanted);
        Assert.True(sim.TradeOf(port)!.Total > 0);
        // Закрыть улетевший корабль нельзя.
        Assert.False(sim.Now(1, new CloseSaleOrder { Port = port }));
        var before = sim.Credits(1);
        Assert.True(sim.Until(() => sim.TradeOf(port) is null, 30));
        Assert.Equal(0, sim.Stock(port, "metal"));
        // Сверху — доход главного здания за время полёта.
        var earned = sim.Credits(1) - before - BuildRules.Round(12 * Price("metal")) - Tables.All.Rewards["sale"].Credits;
        Assert.InRange(earned, 0, 40);
        Assert.Contains("sale", Scene.Rewards(sim, 1));
        // Ничего не привезли: закрытие просто снимает заявку.
        Assert.True(sim.Sell(1, port, "metal", 10));
        Assert.True(sim.Now(1, new CloseSaleOrder { Port = port }));
        Assert.Null(sim.TradeOf(port));
    }

    [Fact(DisplayName = "закупка с орбиты: кредиты списываются сразу, груз выгружается в склад порта после полёта; лишнего не продают")]
    public void PurchaseFliesInAndUnloadsIntoThePort()
    {
        var (sim, port, _) = Make();
        using var _sim = sim;
        var credits = sim.Credits(1);
        // Нет такого ресурса, уран только добывают, дробное меньше единицы, не хватает денег — отказ.
        Assert.False(sim.Buy(1, port, "metalOre", 10));
        Assert.False(sim.Buy(1, port, "uranium", 10));
        Assert.False(sim.Buy(1, port, "metal", 0.5));
        Assert.False(sim.Buy(2, port, "metal", 10));
        sim.SetCredits(1, 5);
        Assert.False(sim.Buy(1, port, "metal", 10));
        sim.SetCredits(1, credits);

        Assert.True(sim.Buy(1, port, "metal", 10.9));
        var cost = 10 * TradeRules.BuyPrice(Price("metal"));
        Assert.Equal(credits - cost, sim.Credits(1));
        var order = sim.TradeOf(port)!;
        Assert.True(order.Buy);
        Assert.Equal(10, order.Wanted);
        Assert.Equal(sim.FlightTicks(port, TradeRules.BuySeconds), order.Total);
        // Пока корабль в пути, порт занят и склад пуст.
        Assert.False(sim.CanSell(1, port));
        Assert.Equal(0, sim.Delivered(port));
        sim.Advance((int)order.Total - 1);
        Assert.NotNull(sim.TradeOf(port));
        Assert.Equal(0, sim.Stock(port, "metal"));
        sim.Advance();
        Assert.Null(sim.TradeOf(port));
        Assert.Equal(10, sim.Stock(port, "metal"));
        Assert.True(sim.CanSell(1, port));
    }

    [Fact(DisplayName = "корабль закупки и заявка продажи переживают сохранение")]
    public void TradeSurvivesASave()
    {
        var (sim, port, _) = Make();
        using var _sim = sim;
        Assert.True(sim.Buy(1, port, "metal", 10));
        sim.Seconds(3);
        var left = sim.TradeOf(port)!.Left;
        using var loaded = sim.ThroughSave();
        Assert.Equal(left, loaded.TradeOf(port)!.Left);
        loaded.Advance((int)left);
        Assert.Null(loaded.TradeOf(port));
        Assert.Equal(10, loaded.Stock(port, "metal"));
    }
}
