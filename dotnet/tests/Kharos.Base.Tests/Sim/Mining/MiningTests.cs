using Base.Shared;
using Base.Shared.Queries;
using Kharos.Base.Tests.Kernel;
using Tables = Kharos.Base.Tests.Kernel.Data;
using static Kharos.Base.Tests.Mining.MiningScene;

namespace Kharos.Base.Tests.Mining;

/// <summary>
/// Добыча (packages/tests/mining.test.ts): месторождения из сида, шахты, переработка. Что идёт через грузовиков, стройку и
/// торговлю, ждёт своих разделов (Skip) — механику добычи без них проверяют свои тесты в конце файла.
/// </summary>
public sealed class MiningTests
{
    /// <summary>Сколько руды переработка принимает в секунду и сколько руды уходит на единицу готового (REFINE_RATE, REFINE_RATIO).</summary>
    private const double RefineRate = 1.5, RefineRatio = 1;

    /// <summary>toBeCloseTo(expected, digits): разница меньше половины единицы последнего знака.</summary>
    private static void Close(double expected, double actual, int digits) =>
        Assert.True(Math.Abs(expected - actual) < Math.Pow(10, -digits) / 2, $"{actual} ≠ {expected} (до {digits} знаков)");

    [Fact(DisplayName = "месторождения считаются из сида: одни и те же в двух мирах, на скале и не чаще одного на клетку")]
    public void DepositsAreComputedFromSeed()
    {
        using var first = Create();
        using var second = Create();
        var spots = new List<DepositSpot>();
        for (var cellY = -6; cellY < 6; cellY++)
            for (var cellX = -6; cellX < 6; cellX++)
            {
                var spot = first.DepositIn(cellX, cellY);
                Assert.Equal(second.DepositIn(cellX, cellY), spot);
                if (spot is not null)
                    spots.Add(spot);
            }
        // Месторождений заметно меньше, чем клеток, но они есть.
        Assert.True(spots.Count > 20, $"{spots.Count} месторождений");
        Assert.True(spots.Count < 144, $"{spots.Count} месторождений");
        foreach (var spot in spots)
        {
            Assert.True(Scene.CanPlace(first, "mine", spot.X, spot.Y));
            var kind = Tables.All.Deposits[spot.Kind];
            Assert.InRange(spot.Reserve, kind.Min, kind.Max);
            Assert.Equal(spot, first.DepositAt(spot.X, spot.Y));
            Assert.Null(first.DepositAt(spot.X + 1, spot.Y));
            Assert.Equal(spot, first.DepositNear(spot.X + 2.5, spot.Y + 1, 3));
        }
        // Нетронутые месторождения в мире не хранятся.
        Assert.Empty(first.With(Names.Deposit.Name));
    }

    [Fact(DisplayName = "у большинства точек карты все четыре вида месторождений в радиусе 150 тайлов")]
    public void AllFourKindsWithin150Tiles()
    {
        // Спавн игроков случайный (как в Rust), поэтому проверяется статистика по сетке точек, а не одна точка: шансы видов
        // подобраны так, что у 8 из 10 точек карты в радиусе 150 тайлов есть все четыре вида, и ни один вид не отсутствует
        // более чем у каждой десятой точки (вопрос 14 §6 design.md). Литий и уран необязательны и лежат в своих биомах.
        string[] basic = ["metal", "silicon", "fuel", "kharite"];
        using var sim = Create();
        const double reach = 150;
        var all = 0;
        var points = 0;
        var missing = basic.ToDictionary(kind => kind, _ => 0);
        for (var x = -448; x <= 448; x += 64)
            for (var y = -448; y <= 448; y += 64)
            {
                points++;
                var kinds = new HashSet<string>();
                for (var cellY = (int)Math.Floor((y - reach) / Sim.DepositCell); cellY <= (int)Math.Floor((y + reach) / Sim.DepositCell); cellY++)
                    for (var cellX = (int)Math.Floor((x - reach) / Sim.DepositCell); cellX <= (int)Math.Floor((x + reach) / Sim.DepositCell); cellX++)
                        if (sim.DepositIn(cellX, cellY) is { } spot && Math.Sqrt(Math.Pow(spot.X + 1 - x, 2) + Math.Pow(spot.Y + 1 - y, 2)) <= reach)
                            kinds.Add(spot.Kind);
                if (basic.All(kinds.Contains))
                    all++;
                foreach (var kind in basic.Where(kind => !kinds.Contains(kind)))
                    missing[kind]++;
            }
        Assert.True((double)all / points >= 0.8, $"{all} из {points} точек со всеми видами");
        foreach (var kind in basic)
            Assert.True((double)missing[kind] / points <= 0.1, $"{kind}: нет у {missing[kind]} из {points} точек");
    }

    [Fact(DisplayName = "шахта ставится только на месторождение и без своей зоны, строит её строитель", Skip = "ждёт С1: стройка (canBuild, зоны, приказ build, награда за шахту)")]
    public void MineOnlyOnDepositAndOutsideForeignZone()
    {
    }

    [Fact(DisplayName = "шахта копит руду; привязанный грузовик возит её на переработку, а без неё руду не берёт", Skip = "ждёт С2b: логистика (haul, грузовики, луч, диспетчер зон)")]
    public void BoundHaulerCarriesOreToRefinery()
    {
    }

    [Fact(DisplayName = "без переработки руда копится в шахте, а грузовик её не берёт: металла не появляется нигде", Skip = "ждёт С2b: логистика (приказ haul, грузовики)")]
    public void OreStaysInMineWithoutRefinery()
    {
    }

    [Fact(DisplayName = "очередей нет: несколько грузовиков выкачивают шахту одновременно", Skip = "ждёт С2b: логистика (haul, луч грузовика)")]
    public void NoQueuesSeveralHaulersDrainMine()
    {
    }

    [Fact(DisplayName = "переработка берёт 1,5 руды в секунду и отдаёт металл один к одному")]
    public void RefineryTakesOneAndHalfOrePerSecond()
    {
        using var sim = Create();
        var spot = RefinerySpot(sim);
        var refinery = RefineryAt(sim, spot);
        sim.SetStock(refinery, "metalOre", 30);
        sim.Seconds(10);
        var made = sim.Stock(refinery, "metal");
        Close((RefineRate * 10) / RefineRatio, made, 1);
        Close(30 - made * RefineRatio, sim.Stock(refinery, "metalOre"), 2);
        // Руда не теряется: сколько переработано, столько и стало металла.
        sim.Seconds(20);
        Close(30 / RefineRatio, sim.Stock(refinery, "metal"), 2);
        Close(0, sim.Stock(refinery, "metalOre"), 2);
    }

    [Fact(DisplayName = "заводу некуда девать готовое — он стоит и руду не тратит")]
    public void RefineryIdlesWhenProductHasNoRoom()
    {
        using var sim = Create();
        var refinery = RefineryAt(sim, RefinerySpot(sim));
        sim.SetStock(refinery, "metalOre", 25);
        sim.SetStock(refinery, "metal", sim.Num(refinery, "Stock.capacity") - 25);
        sim.Seconds(10);
        Assert.Equal(25, sim.Stock(refinery, "metalOre"));
        Assert.Equal(sim.Num(refinery, "Stock.capacity") - 25, sim.Stock(refinery, "metal"));
    }

    [Fact(DisplayName = "хранилище конечно: грузовик берёт столько, сколько влезет, остальное — когда появится место; запас — по всем хранилищам", Skip = "ждёт С2b: логистика (грузовик, хранилища, запас по всем хранилищам)")]
    public void StorageIsFinite()
    {
    }

    [Fact(DisplayName = "космопорт продаёт по заявке: диспетчер шлёт свободные грузовики свезти товар из хранилищ, потом приходят кредиты", Skip = "ждёт С2b: логистика (диспетчер, грузовики) и С2d: торговля (sell, closeSale)")]
    public void SpaceportSellsByOrder()
    {
    }

    [Fact(DisplayName = "здания можно ставить вплотную к шахте, а выработанное месторождение освобождает грузовик", Skip = "ждёт С1: стройка (canBuild) и С2b: логистика (haul)")]
    public void BuildNextToMineAndSpentDepositFreesHauler()
    {
    }

    [Fact(DisplayName = "стройке в зоне без нужного груза его везут из другой своей зоны: металл из плавильни за пределами базы", Skip = "ждёт С1: стройка (площадка, зоны) и С2b: логистика (fetchMaterials)")]
    public void SiteWithoutCargoGetsItFromAnotherZone()
    {
    }

    [Fact(DisplayName = "шахта копит руду в свой склад со скоростью вида, а запас месторождения убывает")]
    public void MineFillsItsStock()
    {
        using var sim = Create();
        var spot = Of(sim, "metal");
        var mine = MineAt(sim, spot);
        Assert.Equal(0u, sim.DepositEntity(spot.X, spot.Y));
        sim.Seconds(5);
        var ore = sim.Stock(mine, "metalOre");
        Close(Tables.All.Deposits["metal"].Rate * 5, ore, 6);
        Close(spot.Reserve - ore, sim.ReserveLeft(spot.X, spot.Y), 6);
        // Первая добыча завела месторождению сущность со счётом добытого.
        var mined = sim.DepositEntity(spot.X, spot.Y);
        Assert.NotEqual(0u, mined);
        Close(ore, sim.Num(mined, "Deposit.mined"), 6);
        Assert.Equal(0, sim.Stock(mine, "metal"));
        // Нетронутые месторождения по-прежнему без сущности.
        Assert.Equal(1, sim.Count(Names.Deposit.Name));
    }

    [Fact(DisplayName = "шахта берёт руду по виду месторождения под ней")]
    public void MineOreFollowsDepositKind()
    {
        using var sim = Create();
        foreach (var kind in new[] { "metal", "silicon", "fuel", "kharite" })
        {
            var spot = Of(sim, kind);
            var mine = MineAt(sim, spot);
            Assert.Equal(kind + "Ore", sim.Query<string>(MiningQueries.Ore, new EntityArgs { Entity = mine }));
            Assert.Equal(kind, sim.Query<string>(MiningQueries.Kind, new DepositArgs { X = spot.X, Y = spot.Y }));
            sim.Seconds(2);
            // Скорость у каждого вида своя (rate в deposits.json).
            Close(Tables.All.Deposits[kind].Rate * 2, sim.Stock(mine, kind + "Ore"), 6);
        }
        Assert.Equal("", sim.Query<string>(MiningQueries.Kind, new DepositArgs { X = 1, Y = 1 }));
    }

    [Fact(DisplayName = "шахта уровнем выше качает быстрее, а полный склад не добывает")]
    public void MineLevelSpeedsUpAndFullStockStops()
    {
        using var sim = Create();
        var spot = Of(sim, "metal");
        var mine = MineAt(sim, spot);
        sim.Set(mine, "Building.level", 3);
        sim.Seconds(5);
        // Каждый уровень сверх первого — на половину быстрее.
        Close(Tables.All.Deposits["metal"].Rate * 5 * 2, sim.Stock(mine, "metalOre"), 6);
        // Осталось место на единицу: шахта добирает до краю, а запас убывает ровно на добытое.
        var capacity = sim.Num(mine, "Stock.capacity");
        sim.SetStock(mine, "metalOre", capacity - 1);
        var left = sim.ReserveLeft(spot.X, spot.Y);
        sim.Seconds(3);
        Close(capacity, sim.Stock(mine, "metalOre"), 6);
        Close(left - 1, sim.ReserveLeft(spot.X, spot.Y), 6);
    }

    [Fact(DisplayName = "выработанное месторождение шахта не качает")]
    public void SpentDepositGivesNothing()
    {
        using var sim = Create();
        var spot = Of(sim, "metal");
        var mine = MineAt(sim, spot);
        sim.Exhaust(spot);
        sim.Seconds(2);
        Assert.Equal(0, sim.Stock(mine, "metalOre"));
        Assert.Equal(0, sim.ReserveLeft(spot.X, spot.Y));
    }

    [Fact(DisplayName = "склад не берёт то, что не принимает вид, и держит предел груза")]
    public void StockObeysAcceptsAndLimits()
    {
        using var sim = Create();
        var refinery = RefineryAt(sim, RefinerySpot(sim));
        // Плавильня принимает свою руду и металл, предел каждого — 30 (buildings.json).
        sim.SetStock(refinery, "metalOre", 30);
        sim.SetStock(refinery, "silicon", 5);
        sim.Seconds(1);
        // Лишнее по accepts (кремний) заводом не трогается и склада не занимает при расчёте места: металл идёт, пока есть предел.
        Assert.Equal(5, sim.Stock(refinery, "silicon"));
        Assert.True(sim.Stock(refinery, "metal") > 0);
    }
}
