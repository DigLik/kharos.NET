using Base.Shared;
using Kharos.Base.Tests.Kernel;
using Kharos.Engine.Terrain;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Energy;

/// <summary>Накопители и литий (packages/tests/lithium.test.ts).</summary>
public sealed class LithiumTests
{
    private static double Capacity => Tables.Buildings["accumulator"].Battery!.Capacity;

    private static double Rate => Tables.Buildings["accumulator"].Battery!.Rate;

    private static Sim Create() => Grid.Create(new { protection = 0, techTree = false });

    private static double Charge(Sim sim, uint cell) => sim.Num(cell, "Accumulator.charge");

    private static double Flow(Sim sim, uint cell) => sim.Num(cell, "Accumulator.flow");

    private static void Close(double expected, double actual, int digits = 2) =>
        Assert.True(Math.Abs(expected - actual) < Math.Pow(10, -digits) / 2, $"ждали {expected}, вышло {actual}");

    /// <summary>Электростанция (10), радар (−3) и накопитель вплотную: излишек 7.</summary>
    private static (Sim Sim, int X, int Y, uint Generator, uint Radar, uint Cell) Base(double charge = 0)
    {
        var sim = Create();
        var (x, y) = Grid.Field(sim, 20, 10);
        var generator = Scene.Place(sim, "generator", x + 4, y + 2, 1);
        var radar = Scene.Place(sim, "radar", x + 6, y + 2, 1);
        var cell = Scene.Place(sim, "accumulator", x + 8, y + 2, 1);
        sim.Set(cell, "Accumulator.charge", charge);
        return (sim, x, y, generator, radar, cell);
    }

    [Fact(DisplayName = "накопитель копит излишек энергии зоны и не больше своей ёмкости")]
    public void StoresSurplus()
    {
        var (sim, _, _, _, _, cell) = Base();
        using var _sim = sim;
        sim.Seconds(10);
        // Излишек 7 в секунду — меньше скорости накопителя: за 10 секунд — 70.
        Close(70, Charge(sim, cell), 0);
        Close(-7, Flow(sim, cell));
        // Копит только излишек: радар работает в полную силу.
        Assert.Equal(3, sim.ZoneEconomies(1)[0].Demand);
        sim.Seconds(100);
        Assert.Equal(Capacity, Charge(sim, cell));
        Assert.Equal(0, Flow(sim, cell));
    }

    [Fact(DisplayName = "без электростанции накопитель держит зону, пока не кончится заряд")]
    public void HoldsZoneWithoutPlant()
    {
        var (sim, _, _, generator, radar, cell) = Base(30);
        using var _sim = sim;
        sim.Erase(generator);
        sim.Advance();
        // Отдаёт ровно сколько просят: радар работает в полную силу, и перегруза нет.
        Close(3, Flow(sim, cell));
        Close(3, sim.ZoneEconomies(1)[0].Stored);
        Assert.Equal(1, sim.PowerSupply()[radar]);
        Assert.False(sim.PowerStates().ContainsKey(radar));
        // 30 единиц на 3 в секунду — 10 секунд; потом радару энергии нет.
        sim.Seconds(9);
        Assert.Equal(1, sim.PowerSupply()[radar]);
        sim.Seconds(2);
        Assert.Equal(0, Charge(sim, cell));
        Assert.Equal(0, sim.PowerSupply()[radar]);
        Assert.Equal("starved", sim.PowerStates()[radar]);
    }

    [Fact(DisplayName = "накопитель отдаёт не быстрее своей скорости: остальное — нехватка")]
    public void LimitedByRate()
    {
        var (sim, x, y, generator, _, cell) = Base(Capacity);
        using var _sim = sim;
        sim.Erase(generator);
        // Радар и 12 турелей без цели, по 1 в простое, — 3 + 12 = 15: больше, чем накопитель отдаёт за секунду.
        for (var i = 0; i < 12; i++)
            Scene.Place(sim, "laserTurret", x + 4 + i % 6, y + 5 + i / 6, 1);
        sim.Advance();
        Assert.Equal(Rate, Flow(sim, cell));
        Assert.Equal(Rate, sim.ZoneEconomies(1)[0].Produced);
        Assert.Equal(15, sim.ZoneEconomies(1)[0].Demand);
    }

    [Fact(DisplayName = "накопитель отдаёт ровно нехватку зоны без себя: в счёт идут прочность станций, уровни, теснота, простой и стержни")]
    public void GivesExactShortage()
    {
        var cases = new (string Name, Action<Sim, int, int> Setup, double Flow)[]
        {
            // Станция вполсилы даёт 5, радар просит 3, четыре лазерные турели без дела — по одной: нехватка 2.
            ("повреждённая станция", (sim, x, y) =>
            {
                sim.Set(Scene.Place(sim, "generator", x + 4, y + 2, 1), "Health.value", 0.5);
                Scene.Place(sim, "radar", x + 6, y + 2, 1);
                for (var i = 0; i < 4; i++)
                    Scene.Place(sim, "laserTurret", x + 4 + i, y + 5, 1);
            }, 2),
            // Станция второго уровня — 15, радар второго уровня — 4,5, три генератора материи теснят друг друга — 3 + 6 + 9: нехватка 7,5.
            ("уровни и теснота", (sim, x, y) =>
            {
                sim.Set(Scene.Place(sim, "generator", x + 4, y + 2, 1), "Building.level", 2);
                sim.Set(Scene.Place(sim, "radar", x + 6, y + 2, 1), "Building.level", 2);
                for (var i = 0; i < 3; i++)
                    Scene.Place(sim, "matter", x + 4 + i * 2, y + 5, 1);
            }, 7.5),
            // Реактор без стержня не даёт ничего: радар — весь на накопителе.
            ("реактор без стержня", (sim, x, y) =>
            {
                Scene.Place(sim, "reactor", x + 3, y + 2, 1);
                Scene.Place(sim, "radar", x + 6, y + 2, 1);
            }, 3),
            // Разрядные турели без дела просят по одной из трёх: четыре — 4, и радар 3.
            ("простой", (sim, x, y) =>
            {
                Scene.Place(sim, "radar", x + 6, y + 2, 1);
                for (var i = 0; i < 4; i++)
                    Scene.Place(sim, "arcTurret", x + 4 + i, y + 5, 1);
            }, 7),
        };
        foreach (var (name, setup, flow) in cases)
        {
            using var sim = Create();
            var (x, y) = Grid.Field(sim, 20, 10);
            setup(sim, x, y);
            var cell = Scene.Place(sim, "accumulator", x + 8, y + 2, 1);
            sim.Set(cell, "Accumulator.charge", Capacity / 2);
            sim.Advance();
            Assert.Equal((name, flow), (name, Flow(sim, cell)));
        }
    }

    [Fact(DisplayName = "два накопителя делят нехватку: второй отдаёт только то, чего не покрыл первый")]
    public void TwoCellsShare()
    {
        var (sim, _, _, generator, _, cell) = Base(30);
        using var _sim = sim;
        sim.Erase(generator);
        var (cx, cy) = sim.At(cell);
        var second = Scene.Place(sim, "accumulator", (int)cx, (int)cy + 2, 1);
        sim.Set(second, "Accumulator.charge", 30);
        sim.Advance();
        Close(3, Flow(sim, cell) + Flow(sim, second));
    }

    [Fact(DisplayName = "заряд накопителя переживает сохранение")]
    public void ChargeSurvivesSave()
    {
        var (sim, _, _, _, _, cell) = Base(250);
        using var _sim = sim;
        using var loaded = sim.ThroughSave();
        Assert.Equal(250, Charge(loaded, cell));
    }

    [Fact(DisplayName = "литий покупается с орбиты, только когда изучена его выпарка; уран — никогда")]
    public void LithiumBoughtWithResearch()
    {
        // В TS дерево технологий включают после создания партии, здесь — с самого начала: темы выдаёт запись Science.done.
        using var sim = Grid.Create(new { protection = 0, techTree = true });
        var (x, y) = Grid.Field(sim, 20, 10);
        sim.SetCredits(1, 50000);
        var port = Scene.Place(sim, "spaceport", x + 4, y + 2, 1);
        Scene.Place(sim, "generator", x + 7, y + 2, 1);
        var granted = new List<int>();
        void Grant(IEnumerable<string> topics)
        {
            granted.AddRange(topics.Select(topic => Scene.Proto(sim, "research/" + topic)));
            sim.Set(sim.Wallet(1), "Science.done", Scene.Ids([.. granted]));
        }
        Grant(Tables.All.Research.Keys.Where(topic => topic != "lithiumOre"));
        Assert.False(sim.Buy(1, port, "uranium", 10));
        Assert.Equal("lithiumOre", sim.BuyResearch(1, "lithium"));
        Assert.False(sim.Buy(1, port, "lithium", 10));
        Assert.False(sim.Has(port, Names.Trade.Name));
        // Металл и прочее темы для покупки не просят.
        Assert.Null(sim.BuyResearch(1, "metal"));
        Grant(["lithiumOre"]);
        Assert.True(sim.Buy(1, port, "lithium", 10));
        sim.Seconds(25);
        Assert.Equal(10, sim.Stock(port, "lithium"));
    }

    [Fact(DisplayName = "у каждого биома свой приз: харит — в эрге, литий — на солончаках, уран — в рыжих пустошах")]
    public void BiomePrizes()
    {
        using var sim = Create();
        var count = new Dictionary<Biome, Dictionary<string, int>>();
        for (var cellY = -25; cellY < 25; cellY++)
            for (var cellX = -25; cellX < 25; cellX++)
            {
                if (sim.DepositIn(cellX, cellY) is not { } spot)
                    continue;
                var biome = sim.Land!.BiomeAt(spot.X, spot.Y);
                if (!count.TryGetValue(biome, out var kinds))
                    count[biome] = kinds = [];
                kinds[spot.Kind] = kinds.GetValueOrDefault(spot.Kind) + 1;
            }
        double Share(Biome biome, string kind)
        {
            if (!count.TryGetValue(biome, out var kinds))
                return 0;
            var all = kinds.Values.Sum();
            return all == 0 ? 0 : kinds.GetValueOrDefault(kind) / (double)all;
        }
        double Others(Biome home, string kind) =>
            new[] { Biome.Erg, Biome.SaltFlats, Biome.RedWastes, Biome.Marsh }.Where(biome => biome != home).Max(biome => Share(biome, kind));
        Assert.True(Share(Biome.Erg, "kharite") > 2 * Others(Biome.Erg, "kharite"));
        Assert.True(Share(Biome.SaltFlats, "lithium") > 3 * Others(Biome.SaltFlats, "lithium"));
        Assert.True(Share(Biome.RedWastes, "uranium") > 3 * Others(Biome.RedWastes, "uranium"));
    }
}
