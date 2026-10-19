using static Kharos.Base.Tests.Logistics.LogisticsScene;

namespace Kharos.Base.Tests.Logistics;

/// <summary>
/// Подъезд грузовиков с нормой поиска на тик (approach.test.ts, seekBeam). Остальные тесты файла — подъезд движка
/// (инструмент Stand) — в Kharos.Engine.Tests/Movement/ApproachTests.cs.
/// </summary>
public sealed class ApproachTests
{
    [Fact(DisplayName = "подъезд к зданию за кольцом труб не съедает тик: за тик грузовики осматривают не больше нормы")]
    public void SeekQuota()
    {
        using var sim = Blank(256);
        var building = Place(sim, "metalYard", 0, 0);
        // Кольцо труб вокруг склада: внутри места хватает, снаружи — вся карта, и поиск пути обходит её всю.
        for (var i = -10; i <= 12; i++)
            foreach (var (x, y) in new[] { (i, -10), (i, 12), (-10, i), (12, i) })
                if (sim.OccupantAt(x, y) == 0)
                    Place(sim, "pipe", x, y);
        // Три грузовика с металлом едут на склад: подъезд ищут все в один тик.
        uint[] trucks = [.. new[] { -30, -40, -50 }.Select(x => Spawn(sim, "truck", 1, x, 0))];
        foreach (var truck in trucks)
        {
            sim.SetStock(truck, "metal", 10);
            sim.SetHauler(truck, to: building, resource: "metal", amount: 10, full: true);
        }
        var search = sim.Host.Tools.Navigation!.Search;
        var before = search.Searched;
        sim.Advance();
        // Счётчик поиска пути учитывает и тайл, на котором поиск остановился.
        Assert.True(search.Searched - before <= 20000 + 1, $"{search.Searched - before}");
        // Первый искал и промахнулся; остальным не хватило нормы — они ищут в свою следующую очередь, без промаха.
        Assert.Equal([1.0, 0, 0], trucks.Select(truck => sim.Hauler(truck).Misses));
    }
}
