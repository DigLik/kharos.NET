using Base.Shared.Orders;
using static Kharos.Base.Tests.Logistics.LogisticsScene;

namespace Kharos.Base.Tests.Logistics;

/// <summary>Ручной приказ «взять здесь, отвезти туда» (packages/tests/carrying.test.ts).</summary>
public sealed class CarryingTests
{
    /// <summary>Два склада металла поодаль, без труб, и два грузовика между ними (setup).</summary>
    private static (Sim Sim, uint From, uint To, uint[] Trucks) Setup()
    {
        var sim = Create(512, new { techTree = false });
        for (var y = -200; y < 200; y++)
            for (var x = -200; x < 200; x++)
            {
                if (!Rock(sim, x, y, 30, 10))
                    continue;
                var from = Place(sim, "metalYard", x, y + 3);
                var to = Place(sim, "metalYard", x + 18, y + 3);
                sim.SetStock(from, "metal", 100);
                uint[] trucks = [Spawn(sim, "truck", 1, x + 10, y + 1), Spawn(sim, "truck", 1, x + 10, y + 8)];
                return (sim, from, to, trucks);
            }
        throw new InvalidOperationException("Не нашлось скалы");
    }

    [Fact(DisplayName = "ручной приказ: грузовики перевозят заданное рейсами — в любой склад, где груз помещается, — и снова свободны")]
    public void TrucksCarryInTripsAndAreFreeAgain()
    {
        var (sim, from, to, trucks) = Setup();
        using var _sim = sim;
        // Склад склада не заказывает: металл между ними возят только по приказу.
        sim.OrderNow(1, new CarryOrder { Units = trucks, From = from, To = to, Resource = "metal", Amount = 60 });
        Assert.All(trucks, truck => Assert.NotNull(sim.CarryOf(truck)));
        sim.Advance(2);
        // Что везут, видят заявки: того же не повезёт другой.
        Assert.True(sim.Incoming(to, "metal") > 0);
        sim.Advance(3000);
        Assert.Equal(60, Amount(sim, to, "metal"), 3);
        Assert.Equal(40, Amount(sim, from, "metal"), 3);
        foreach (var truck in trucks)
        {
            Assert.Null(sim.CarryOf(truck));
            Assert.Equal(0, Amount(sim, truck, "metal"), 3);
        }
    }

    [Fact(DisplayName = "приказ кончается, когда источник опустел; переживает сохранение; негодный не принимается")]
    public void OrderEndsSurvivesSaveAndRejectsBad()
    {
        var (sim, from, to, trucks) = Setup();
        using var _sim = sim;
        sim.SetStock(from, "metal", 15);
        sim.OrderNow(1, new CarryOrder { Units = [trucks[0]], From = from, To = to, Resource = "metal", Amount = 500 });
        Assert.NotNull(sim.CarryOf(trucks[0]));
        sim.Advance(5);
        using (var copy = sim.ThroughSave())
            Assert.Equal(sim.CarryOf(trucks[0]), copy.CarryOf(trucks[0]));
        sim.Advance(3000);
        Assert.Equal(15, Amount(sim, to, "metal"), 3);
        Assert.Null(sim.CarryOf(trucks[0]));
        // Чужим не приказать, в себя не перевезти, груз, которому некуда, — тоже.
        sim.OrderNow(2, new CarryOrder { Units = trucks, From = from, To = to, Resource = "metal", Amount = 5 });
        sim.OrderNow(1, new CarryOrder { Units = trucks, From = from, To = from, Resource = "metal", Amount = 5 });
        sim.OrderNow(1, new CarryOrder { Units = trucks, From = to, To = from, Resource = "metalOre", Amount = 5 });
        Assert.All(trucks, truck => Assert.Null(sim.CarryOf(truck)));
    }

    [Fact(DisplayName = "приказ из кузова: грузовик выгружает то, что везёт, туда, куда сказали")]
    public void CarryFromCargo()
    {
        var (sim, _, to, trucks) = Setup();
        using var _sim = sim;
        sim.SetStock(trucks[0], "metal", 12);
        sim.OrderNow(1, new CarryOrder { Units = [trucks[0]], From = 0, To = to, Resource = "metal", Amount = 12 });
        Assert.NotNull(sim.CarryOf(trucks[0]));
        sim.Advance(2000);
        Assert.Equal(12, Amount(sim, to, "metal"), 3);
        Assert.Null(sim.CarryOf(trucks[0]));
    }
}
