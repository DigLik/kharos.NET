using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Kharos.Base.Tests.Kernel;
using Tables = Kharos.Base.Tests.Kernel.Data;
using static Kharos.Base.Tests.Mining.MiningScene;

namespace Kharos.Base.Tests.Mining;

/// <summary>
/// Добыча (packages/tests/mining.test.ts): месторождения из сида, шахты, переработка, вывоз руды грузовиками (раздел
/// Logistics). Продажу космопорту ведёт раздел Trade; механику добычи без грузовиков проверяют свои тесты в конце файла.
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

    [Fact(DisplayName = "шахта ставится только на месторождение и без своей зоны, строит её строитель")]
    public void MineOnlyOnDepositAndOutsideForeignZone()
    {
        using var sim = Create();
        // start() в mining.test.ts: металлическое месторождение, справа от которого есть скала под хранилище.
        DepositSpot? found = null;
        for (var cellY = -5; cellY < 5 && found is null; cellY++)
            for (var cellX = -5; cellX < 5 && found is null; cellX++)
                if (sim.DepositIn(cellX, cellY) is { Kind: "metal" } candidate && Scene.CanPlace(sim, "metalYard", candidate.X + 3, candidate.Y))
                    found = candidate;
        var spot = found ?? throw new InvalidOperationException("В мире не нашлось месторождения");
        sim.SetCredits(1, 1000);
        Assert.Empty(sim.Zones(1));
        Assert.True(sim.CanBuild(1, "mine", spot.X, spot.Y));
        Assert.False(sim.CanBuild(1, "mine", spot.X + 1, spot.Y));
        // Чужая зона закрывает месторождение.
        var core = sim.PutBuilding("command", spot.X + 6, spot.Y, 2);
        Assert.False(sim.CanBuild(1, "mine", spot.X, spot.Y));
        sim.Erase(core);

        var builder = sim.PutUnit("builder", 1, spot.X - 1 + 0.5, spot.Y + 0.5);
        sim.Send(1, new BuildOrder { Building = "mine", X = spot.X, Y = spot.Y, Builders = [] });
        sim.Seconds(30);
        // Готовая шахта начинает свою зону: рядом можно строить.
        Assert.Single(sim.Zones(1));
        Assert.Equal(["mine"], Scene.Rewards(sim, 1));
        Assert.True(sim.CanBuild(1, "metalYard", spot.X + 3, spot.Y));
        Assert.NotEqual(0u, builder);
    }

    [Fact(DisplayName = "шахта копит руду; привязанный грузовик возит её на переработку, а без неё руду не берёт")]
    public void BoundHaulerCarriesOreToRefinery()
    {
        var (sim, spot, mine, core, truck) = HaulBase();
        using var _sim = sim;
        var rate = Tables.All.Deposits["metal"].Rate;
        double Cargo() => sim.Stock(truck, "metalOre");
        // Без грузовика шахта добывает руду в свой склад.
        sim.Seconds(5);
        Close(rate * 5, sim.Stock(mine, "metalOre"), 2);
        Close(spot.Reserve - sim.Stock(mine, "metalOre"), sim.ReserveLeft(spot.X, spot.Y), 2);
        Assert.Equal(0u, sim.Hauler(truck).Mine);
        // Чужой грузовик к шахте не привязать.
        var stranger = sim.PutUnit("truck", 2, spot.X - 1 + 0.5, spot.Y + 1 + 0.5);
        sim.Send(2, new HaulOrder { Units = [stranger], Mine = mine });
        sim.Send(1, new HaulOrder { Units = [truck, stranger], Mine = mine });
        sim.Advance();
        Assert.Equal(0u, sim.Hauler(stranger).Mine);
        sim.Erase(stranger);

        // Переработки нет — руду некуда везти, и грузовик её не берёт.
        sim.Seconds(5);
        Assert.Equal(0, Cargo());
        Assert.Equal(0u, sim.Hauler(truck).From);
        var refinery = HaulRefinery(sim, spot);
        // Появилась переработка: грузовик подъезжает на длину своего луча и выкачивает добытое.
        Assert.True(sim.Until(() => sim.Hauler(truck).Loading, 120));
        Assert.True(sim.Gap(mine, truck) <= sim.Num(truck, "Beam.radius"));
        Assert.Equal([new BeamLink(mine, true, "metalOre")], sim.Links(truck));
        Assert.True(sim.Until(() => sim.Stock(mine, "metalOre") < 0.1, 120));
        // Дальше грузится по мере добычи.
        var loaded = Cargo();
        sim.Seconds(10);
        Close(loaded + rate * 10, Cargo(), 0);

        // Полный кузов уезжает на переработку и становится металлом; руду в хранилища не кладут.
        Assert.True(sim.Until(() => sim.Hauler(truck).Full, 120));
        Assert.Equal(TruckCapacity, Cargo());
        Assert.True(sim.Until(() => !sim.Hauler(truck).Full, 120));
        Assert.Equal(0, sim.Stock(core, "metalOre"));
        Close(TruckCapacity, sim.Stock(refinery, "metal") + sim.Stock(refinery, "metalOre"), 2);
        Assert.True(sim.Until(() => sim.Stock(refinery, "metal") > 15, 120));
        // Готовое копится у завода: заявок на него нет, а сам грузовик возит только руду.
        sim.Seconds(10);
        Assert.Empty(StockOf(sim, 1).Items);

        // Приказ идти снимает грузовик с шахты. Дальше он свободен: накопленное в шахте диспетчер велит ему увезти.
        sim.Send(1, new MoveOrder { Units = [truck], X = spot.X - 3, Y = spot.Y });
        sim.Seconds(1);
        Assert.Equal(0u, sim.Hauler(truck).Mine);
        sim.SetStock(mine, "metalOre", Tables.Buildings["mine"].Inventory!.Value);
        Assert.True(sim.Until(() => sim.Hauler(truck).From == mine, 120));

        // Сохранение помнит и руду, и груз, и запас.
        using (var copy = sim.ThroughSave())
        {
            Assert.Equal(sim.ReserveLeft(spot.X, spot.Y), copy.ReserveLeft(spot.X, spot.Y));
            Assert.Equal(Cargo(), copy.Stock(truck, "metalOre"));
            Assert.Equal(sim.Stock(refinery, "metal"), copy.Stock(refinery, "metal"));
            Assert.Equal(sim.Stock(mine, "metalOre"), copy.Stock(mine, "metalOre"));
            Assert.Equivalent(sim.Hauler(truck), copy.Hauler(truck), strict: true);
        }

        // Полная шахта, которую некому вывезти, не добывает.
        sim.Erase(truck);
        sim.SetStock(mine, "metalOre", Tables.Buildings["mine"].Inventory!.Value);
        var left = sim.ReserveLeft(spot.X, spot.Y);
        sim.Seconds(5);
        Assert.Equal(left, sim.ReserveLeft(spot.X, spot.Y));
    }

    [Fact(DisplayName = "без переработки руда копится в шахте, а грузовик её не берёт: металла не появляется нигде")]
    public void OreStaysInMineWithoutRefinery()
    {
        var (sim, spot, mine, _, truck) = HaulBase();
        using var _sim = sim;
        sim.Send(1, new HaulOrder { Units = [truck], Mine = mine });
        sim.Seconds(30);
        Assert.Empty(StockOf(sim, 1).Items);
        Assert.Equal(0, sim.Stock(truck, "metalOre"));
        Assert.True(sim.Stock(mine, "metalOre") > Math.Min(Tables.All.Deposits["metal"].Rate * 25, Tables.Buildings["mine"].Inventory!.Value - 1));
        Assert.True(sim.ReserveLeft(spot.X, spot.Y) > 0);
    }

    [Fact(DisplayName = "очередей нет: несколько грузовиков выкачивают шахту одновременно")]
    public void NoQueuesSeveralHaulersDrainMine()
    {
        var (sim, spot, mine, _, truck) = HaulBase();
        using var _sim = sim;
        var refinery = HaulRefinery(sim, spot);
        var second = sim.PutUnit("truck", 1, spot.X - 1 + 0.5, spot.Y + 1 + 0.5);
        sim.SetStock(mine, "metalOre", Tables.Buildings["mine"].Inventory!.Value);
        sim.Send(1, new HaulOrder { Units = [truck, second], Mine = mine });
        // Руду грузовикам даёт только шахта: прибавилось у обоих в один тик — качали одновременно.
        var both = 0;
        for (var i = 0; i < Sim.TicksIn(20); i++)
        {
            var before = (sim.Stock(truck, "metalOre"), sim.Stock(second, "metalOre"));
            sim.Advance();
            if (sim.Stock(truck, "metalOre") > before.Item1 && sim.Stock(second, "metalOre") > before.Item2)
                both++;
        }
        Assert.True(both > 0);
        // В шахте было на два кузова: оба доехали до переработки и стали металлом.
        Assert.True(sim.Until(() => sim.Stock(refinery, "metal") + sim.Stock(refinery, "metalOre") >= 2 * TruckCapacity - 1e-6, 200));
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

    [Fact(DisplayName = "хранилище конечно: грузовик берёт столько, сколько влезет, остальное — когда появится место; запас — по всем хранилищам")]
    public void StorageIsFinite()
    {
        var (sim, spot, mine, _, truck) = HaulBase();
        using var _sim = sim;
        var refinery = HaulRefinery(sim, spot);
        // Руда шахты тут не при чём: у завода уже лежит готовый металл, а в кузове шахты — нет.
        sim.Erase(mine);
        var first = Logistics.LogisticsScene.Place(sim, "metalYard", spot.X + 5, spot.Y + 5);
        var capacity = Tables.Buildings["metalYard"].Inventory!.Value;
        sim.SetStock(first, "metal", capacity - 5);
        sim.SetStock(refinery, "metal", TruckCapacity);

        // В склад влезает только пять единиц: столько грузовик и увозит, остальное остаётся у завода.
        Assert.True(sim.Until(() => sim.Stock(first, "metal") >= capacity - 1e-6, 120));
        sim.Seconds(10);
        Close(capacity, sim.Stock(first, "metal"), 2);
        Assert.Equal(0, sim.Stock(truck, "metal"));
        Close(TruckCapacity - 5, sim.Stock(refinery, "metal"), 2);

        // Новое хранилище — грузовик увозит туда остаток.
        var second = Logistics.LogisticsScene.Place(sim, "metalYard", spot.X + 9, spot.Y + 5);
        Assert.True(sim.Until(() => sim.Stock(second, "metal") >= TruckCapacity - 5 - 1e-6, 120));
        Assert.False(sim.Hauler(truck).Full);
        Assert.Equal(capacity * 2, StockOf(sim, 1).Capacity);
    }

    [Fact(DisplayName = "космопорт продаёт по заявке: диспетчер шлёт свободные грузовики свезти товар из хранилищ, потом приходят кредиты")]
    public void SpaceportSellsByOrder()
    {
        var (sim, spot, mine, _, truck) = HaulBase();
        using var _sim = sim;
        var first = Logistics.LogisticsScene.Place(sim, "metalYard", spot.X + 5, spot.Y + 5);
        sim.SetStock(first, "metal", 80);
        var second = Logistics.LogisticsScene.Place(sim, "metalYard", spot.X + 9, spot.Y + 5);
        sim.SetStock(second, "metal", 15);
        // С деньгами главное здание даёт обычный доход, без подмоги: иначе её не отличить от выручки.
        sim.SetCredits(1, global::Base.Shared.Rules.PowerRules.ReliefBelow);
        var credits = sim.Credits(1);
        double Stored() => sim.Stock(first, "metal") + sim.Stock(second, "metal");
        // Без космопорта металл лежит и денег не приносит.
        sim.Seconds(5);
        Assert.True(sim.Credits(1) - credits <= 1);
        Assert.True(sim.CanBuild(1, "spaceport", spot.X + 9, spot.Y));
        var port = Logistics.LogisticsScene.Place(sim, "spaceport", spot.X + 9, spot.Y);
        Assert.True(sim.CanSell(1, port));
        Assert.False(sim.CanSell(2, port));

        // Заявка сама по себе товар не приносит: его должны привезти.
        sim.Send(2, new SellOrder { Port = port, Resource = "metal", Amount = 50 });
        sim.Send(1, new SellOrder { Port = port, Resource = "metal", Amount = 50 });
        sim.Advance();
        Assert.Equal(50, sim.TradeOf(port)!.Wanted);
        Assert.Equal(95, Stored());
        // Пока заявка открыта, вторую космопорт не берёт.
        sim.Send(1, new SellOrder { Port = port, Resource = "metal", Amount = 10 });
        sim.Advance();
        Assert.Equal(50, sim.TradeOf(port)!.Wanted);

        // Космопорт заказал металл, и диспетчер отдал заявку свободному грузовику: тот выкачивает его из хранилища
        // и сгружает в космопорт.
        Assert.True(sim.Until(() => sim.Hauler(truck).To == port, 120));
        Assert.True(sim.Until(() => sim.Hauler(truck).Loading && sim.Stock(truck, "metal") > 0, 120));
        Assert.True(sim.Until(() => sim.Delivered(port) > 0, 120));
        Assert.True(sim.Gap(port, truck) <= sim.Num(truck, "Beam.radius"));
        // Металл нигде не теряется: он в хранилищах, в кузове или в космопорте.
        Close(95, Stored() + sim.Stock(truck, "metal") + sim.Delivered(port), 2);

        // Привезли всё — грузовик свободен, корабль улетел, кредиты приходят после полёта.
        Assert.True(sim.Until(() => sim.TradeOf(port)!.Total > 0, 120));
        Close(50, sim.Delivered(port), 2);
        Close(45, Stored(), 2);
        Assert.True(sim.Until(() => sim.Hauler(truck).To != port, 5));
        Assert.Equal(0, sim.Stock(truck, "metal"));
        var before = sim.Credits(1);
        const double sellSeconds = global::Base.Shared.Rules.TradeRules.SellSeconds;
        sim.Seconds(sellSeconds - 2);
        Assert.True(sim.Credits(1) - before < 10);
        Assert.NotNull(sim.TradeOf(port));
        sim.Seconds(2);
        Assert.Null(sim.TradeOf(port));
        Assert.Equal(0, sim.Delivered(port));
        var income = Tables.Buildings["command"].Income!.Value * sellSeconds;
        var reward = Tables.All.Rewards["sale"].Credits;
        Assert.True(Math.Abs(sim.Credits(1) - before - 50 * Tables.All.Resources["metal"].Price - income - reward) <= 1);

        // Грузовик, привязанный к шахте, на зов космопорта не идёт; заявку можно закрыть с тем, что привезли.
        sim.Send(1, new HaulOrder { Units = [truck], Mine = mine });
        sim.Seconds(1);
        var stock = Math.Floor(Stored());
        sim.Send(1, new SellOrder { Port = port, Resource = "metal", Amount = 1000 });
        sim.Seconds(5);
        Assert.Equal(stock, sim.TradeOf(port)!.Wanted);
        Assert.NotEqual(port, sim.Hauler(truck).To);
        Assert.Equal(0, sim.Delivered(port));
        sim.Send(1, new CloseSaleOrder { Port = port });
        sim.Advance();
        Assert.Null(sim.TradeOf(port));
        Assert.True(Stored() >= 45);
    }

    [Fact(DisplayName = "здания можно ставить вплотную к шахте, а выработанное месторождение освобождает грузовик")]
    public void BuildNextToMineAndSpentDepositFreesHauler()
    {
        var (sim, spot, mine, _, truck) = HaulBase();
        using var _sim = sim;
        sim.SetCredits(1, 1000);
        HaulRefinery(sim, spot);
        // Вплотную к шахте строить можно: место для грузовика под ней не нужно.
        Assert.True(sim.CanBuild(1, "metalYard", spot.X, spot.Y + 2));
        Logistics.LogisticsScene.Place(sim, "metalYard", spot.X + 5, spot.Y + 5);

        sim.Send(1, new HaulOrder { Units = [truck], Mine = mine });
        Assert.True(sim.Until(() => sim.Hauler(truck).Loading, 120));
        // Даём грузовику набрать груз, потом вырабатываем месторождение.
        sim.Seconds(15);
        foreach (var deposit in sim.With(Names.Deposit.Name))
            sim.Set(deposit, "Deposit.mined", spot.Reserve - 1);
        Assert.True(sim.Until(() => sim.ReserveLeft(spot.X, spot.Y) == 0, 120));
        // Довёз остаток до переработки и освободился.
        Assert.True(sim.Until(() => sim.Hauler(truck).Mine == 0 && sim.Stock(truck, "metalOre") == 0, 120));
        // Переработанный металл свободный грузовик свозит в хранилище.
        Assert.True(sim.Until(() => StockOf(sim, 1).Items.GetValueOrDefault("metal") > 1, 120));
        Assert.False(sim.CanBuild(1, "mine", spot.X, spot.Y));
    }

    [Fact(DisplayName = "стройке в зоне без нужного груза его везут из другой своей зоны: металл из плавильни за пределами базы")]
    public void SiteWithoutCargoGetsItFromAnotherZone()
    {
        var (sim, spot, _, core, _) = HaulBase();
        using var _sim = sim;
        // Плавильня с готовым металлом — далеко, в своей зоне: труб к базе нет.
        var smelter = Logistics.LogisticsScene.Place(sim, "smelter", spot.X + 30, spot.Y);
        sim.SetStock(smelter, "metal", 30);
        // Стройка космопорта у главного здания ждёт металл: площадка прямо в мир, как в TS.
        var site = sim.World.Spawn();
        sim.Add(site, Names.Position.Name);
        sim.Set(site, "Position.x", spot.X + 9);
        sim.Set(site, "Position.y", spot.Y);
        sim.Add(site, Names.Site.Name);
        sim.Add(site, Names.Owner.Name);
        sim.Set(site, "Owner.player", 1);
        sim.Add(site, Names.Proto.Name);
        sim.Set(site, "Proto.id", Scene.Proto(sim, "buildings/spaceport"));
        sim.Add(site, Names.Stock.Name);
        sim.Set(site, "Stock.capacity", 20);
        sim.Add(site, Names.Limits.Name);
        sim.Set(site, "Limits.metal", 20);
        Assert.DoesNotContain(sim.Zones(1), zone => zone.Buildings.Contains(core) && zone.Buildings.Contains(smelter));
        Assert.True(sim.Until(() => sim.Stock(site, "metal") >= 20 - 1e-6, 300));
        Assert.True(sim.Stock(smelter, "metal") < 30);
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

    // ===== Грузовики (раздел Logistics) =====

    /// <summary>Сколько помещается в грузовик (TRUCK_CAPACITY).</summary>
    private static double TruckCapacity => Tables.Units["truck"].Inventory!.Value;

    /// <summary>База у месторождения металла: шахта, главное здание справа от неё и грузовик игрока 1 (base в mining.test.ts).</summary>
    private static (Sim Sim, DepositSpot Spot, uint Mine, uint Core, uint Truck) HaulBase()
    {
        var sim = Create();
        var navigation = sim.Host.Tools.Navigation!;
        (int X, int Y)[] around = [(0, 2), (1, 2), (6, 3), (-1, 0), (-1, 1), (-1, 2), (2, 2), (3, 1)];
        for (var cellY = -6; cellY < 6; cellY++)
            for (var cellX = -6; cellX < 6; cellX++)
            {
                var spot = sim.DepositIn(cellX, cellY);
                if (spot is not { Kind: "metal" } || !Scene.CanPlace(sim, "command", spot.X + 5, spot.Y))
                    continue;
                // Место под грузовики вокруг шахты и под главным зданием должно быть проходимо.
                if (!around.All(at => navigation.IsWalkable(spot.X + at.X, spot.Y + at.Y)) || !Scene.CanPlace(sim, "spaceport", spot.X + 9, spot.Y))
                    continue;
                // И места под переработку, хранилища и дальнюю плавильню, которые тесты ставят рядом.
                if (!Scene.Rock(sim, spot.X + 12, spot.Y, 4, 5) || !Scene.Rock(sim, spot.X + 5, spot.Y + 5, 7, 3) || !Scene.Rock(sim, spot.X + 30, spot.Y, 3, 3))
                    continue;
                var mine = MineAt(sim, spot);
                var core = sim.PutBuilding("command", spot.X + 5, spot.Y, 1);
                var truck = sim.PutUnit("truck", 1, spot.X - 1 + 0.5, spot.Y + 0.5);
                return (sim, spot, mine, core, truck);
            }
        throw new InvalidOperationException("В мире не нашлось места под базу у месторождения");
    }

    /// <summary>Переработка с электростанцией, соединённые трубой (refineryAt).</summary>
    private static uint HaulRefinery(Sim sim, DepositSpot spot)
    {
        var refinery = Logistics.LogisticsScene.Place(sim, "smelter", spot.X + 12, spot.Y + 2);
        Logistics.LogisticsScene.Place(sim, "generator", spot.X + 14, spot.Y);
        Logistics.LogisticsScene.Place(sim, "pipe", spot.X + 15, spot.Y + 2);
        return refinery;
    }

    /// <summary>Запас игрока по хранилищам всех его зон (stockOf): готовое и вместимость.</summary>
    private static (Dictionary<string, double> Items, double Capacity) StockOf(Sim sim, uint player)
    {
        var items = new Dictionary<string, double>();
        var capacity = 0.0;
        var wares = Tables.All.Resources.Keys.Concat(Tables.All.Products.Keys).ToList();
        foreach (var zone in sim.Zones(player))
            foreach (var entity in zone.Buildings)
            {
                if (!sim.Has(entity, Names.Stock.Name) || Scene.TypeOf(sim, entity) is not { } type || !Tables.Buildings.TryGetValue(type, out var spec) || !spec.Stores)
                    continue;
                foreach (var ware in wares)
                    if (sim.Stock(entity, ware) is var amount and > 0)
                        items[ware] = items.GetValueOrDefault(ware) + amount;
                capacity += sim.Num(entity, "Stock.capacity");
            }
        return (items, capacity);
    }
}
