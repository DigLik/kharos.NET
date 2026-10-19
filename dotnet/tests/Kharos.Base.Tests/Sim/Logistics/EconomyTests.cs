using Base.Shared;
using Base.Shared.Orders;
using Kharos.Base.Tests.Kernel;
using static Kharos.Base.Tests.Logistics.LogisticsScene;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Logistics;

/// <summary>Экономика: месторождения, заявки зон, грузовики и стройки (packages/tests/economy.test.ts).</summary>
public sealed class EconomyTests
{
    /// <summary>Хранилища по порядку buildings.json (STORES).</summary>
    private static readonly string[] Stores = [.. Tables.Buildings.Where(pair => pair.Value.Stores).Select(pair => pair.Key)];

    /// <summary>База игрока 1: главное здание, электростанции, здания из списка и ряд хранилищ (по одному на груз), над ними труба.</summary>
    private sealed record Base(Sim Sim, int X, int Y, uint Core, List<uint> Buildings, List<uint> Shelves) : IDisposable
    {
        /// <summary>Хранилище, которое принимает этот груз (storeFor).</summary>
        public uint Store(string good) => Shelves[Array.FindIndex(Stores, type => Tables.Buildings[type].Accepts.Contains(good))];

        /// <summary>Кладёт груз в хранилища его вида (stash).</summary>
        public void Stash(params (string Good, double Amount)[] items)
        {
            foreach (var (good, amount) in items)
                Sim.SetStock(Store(good), good, amount);
        }

        public void Dispose() => Sim.Dispose();
    }

    private static Base Make(params string[] extra)
    {
        var sim = Create(512, new { techTree = false });
        sim.SetCredits(1, 10000);
        var layout = new List<(string Type, int Dx)> { ("command", 0), ("generator", 4), ("generator", 7) };
        var at = 10;
        // Ряд хранилищ — по одному на каждое готовое — после зданий из списка.
        foreach (var type in extra.Concat(Stores))
        {
            layout.Add((type, at));
            at += Width(type) + 1;
        }
        for (var y = -100; y < 100; y++)
            for (var x = -100; x < 100; x++)
            {
                if (!layout.All(item => Rock(sim, x + item.Dx, y, Width(item.Type), (int)Tables.Buildings[item.Type].Height)) || !Rock(sim, x, y + 4, 7, 3))
                    continue;
                if (!layout.All(item => Scene.CanPlace(sim, item.Type, x + item.Dx, y)))
                    continue;
                if (!Scene.CanPlace(sim, "command", x, y + 4) || !Scene.CanPlace(sim, "command", x + 4, y + 4))
                    continue;
                // Над рядом — труба вдоль всей базы: все здания в одной сети.
                if (!Enumerable.Range(0, at).All(dx => Scene.CanPlace(sim, "pipe", x + dx, y - 1)))
                    continue;
                var buildings = layout.Select(item => sim.PutBuilding(item.Type, x + item.Dx, y, 1)).ToList();
                for (var dx = 0; dx < at; dx++)
                    sim.PutBuilding("pipe", x + dx, y - 1, 1);
                return new Base(sim, x, y, buildings[0], buildings.GetRange(3, extra.Length), buildings.GetRange(3 + extra.Length, Stores.Length));
            }
        throw new InvalidOperationException("Не нашлось места под базу");
    }

    private static double Blocks(string type) => Tables.Buildings[type].Materials["blocks"];

    /// <summary>Тиков работы одного строителя на здание (siteTicks).</summary>
    private static double SiteTicks(string type) => Math.Floor(Tables.Buildings[type].Cost / 20 / Tick + 0.5);

    /// <summary>Какую долю стройки позволяют привезённые материалы (materialShare).</summary>
    private static double MaterialShare(Sim sim, uint site, string type) =>
        Tables.Buildings[type].Materials.Select(item => Amount(sim, site, item.Key) >= item.Value - 1e-6 ? 1 : Amount(sim, site, item.Key) / item.Value).DefaultIfEmpty(1).Min();

    [Fact(DisplayName = "месторождения бывают разных видов: что где лежит, решает биом")]
    public void DepositKindsDependOnBiome()
    {
        using var sim = Create(512, new { techTree = false });
        var kinds = new HashSet<string>();
        for (var cellY = -12; cellY < 12; cellY++)
            for (var cellX = -12; cellX < 12; cellX++)
                if (sim.DepositIn(cellX, cellY) is { } spot)
                    kinds.Add(spot.Kind);
        Assert.Equal([.. Sim.DepositTypes.Order()], kinds.Order());
    }

    [Fact(DisplayName = "стройка ждёт материалов: без стройблоков она не идёт дальше привезённого, по трубам их привозят из хранилища")]
    public void SiteWaitsForMaterialsByPipes()
    {
        using var @base = Make();
        var (sim, x, y) = (@base.Sim, @base.X, @base.Y);
        var builder = Spawn(sim, "builder", 1, x + 5, y + 4);
        sim.Send(1, new BuildOrder { Building = "factory", X = x, Y = y + 4, Builders = [builder] });
        // Труба от главного здания к площадке: стройка в сети базы.
        Place(sim, "pipe", x, y + 3);
        sim.Advance();
        var site = sim.SiteAt(x, y + 4);
        sim.Seconds(10);
        // Материалов нет — стройка стоит в самом начале.
        Assert.True(sim.AwaitsMaterials(site));
        Assert.Equal(0, sim.Num(site, "Site.progress"));

        // Стройблоки в хранилище — стройка идёт до конца: к ней подведена труба.
        @base.Stash(("blocks", 50));
        Until(sim, () => MaterialShare(sim, site, "factory") > 0, 200);
        Assert.True(sim.Num(site, "Site.progress") <= SiteTicks("factory") * MaterialShare(sim, site, "factory") + 1e-6);
        Until(sim, () => !sim.Has(site, Names.Site.Name), 200);
        Assert.Equal(50 - Blocks("factory"), Amount(sim, @base.Store("blocks"), "blocks"), 6);
    }

    [Fact(DisplayName = "грузовик довозит материалы и на площадку, к которой строитель ещё не приступил")]
    public void TruckSuppliesUnstartedSite()
    {
        using var @base = Make();
        var (sim, x, y) = (@base.Sim, @base.X, @base.Y);
        @base.Stash(("blocks", 50));
        // Строителя нет: площадка только размечена, здания на ней ещё нет.
        sim.Send(1, new BuildOrder { Building = "factory", X = x, Y = y + 4, Builders = [] });
        sim.Advance();
        var site = sim.SiteAt(x, y + 4);
        Assert.NotEqual(0u, site);
        Spawn(sim, "truck", 1, x + 6, y + 8);
        Until(sim, () => Amount(sim, site, "blocks") >= Blocks("factory") - 1e-6, 200);
        Assert.Equal(50 - Blocks("factory"), Amount(sim, @base.Store("blocks"), "blocks"), 6);
    }

    [Fact(DisplayName = "производство ждёт материалов первого заказа и тратит их в начале работы")]
    public void ProductionWaitsForMaterials()
    {
        using var @base = Make("factory");
        var (sim, factory) = (@base.Sim, @base.Buildings[0]);
        sim.Send(1, new ProduceOrder { Producer = factory, Unit = "tank" });
        sim.Seconds(Tables.Units["tank"].BuildTime + 2);
        Assert.Equal(["tank"], Scene.Queue(sim, factory));
        Assert.Equal(0, sim.Num(factory, "Producer.progress"));

        @base.Stash(("metal", 40), ("parts", 10));
        Until(sim, () => Scene.Units(sim, "tank") > 0, 200);
        // Привезли ровно на танк, и танк их забрал.
        var tank = Tables.Units["tank"].Materials;
        Assert.Equal(40 - tank["metal"], Amount(sim, @base.Store("metal"), "metal") + Amount(sim, factory, "metal"), 6);
        Assert.Equal(10 - tank["parts"], Amount(sim, @base.Store("parts"), "parts") + Amount(sim, factory, "parts"), 6);
    }

    [Fact(DisplayName = "космопорт продаёт любой ресурс по его цене; первая продажа — награда")]
    public void SpaceportSells()
    {
        using var @base = Make("spaceport");
        var (sim, port) = (@base.Sim, @base.Buildings[0]);
        @base.Stash(("metal", 30));
        sim.Send(1, new SellOrder { Port = port, Resource = "metal", Amount = 20 });
        sim.Seconds(1);
        var before = sim.Credits(1);
        Until(sim, () => sim.Credits(1) - before >= 20 * Tables.All.Resources["metal"].Price, 200);
        Assert.Equal(10, Amount(sim, @base.Store("metal"), "metal"), 6);
        Assert.Equal(0, Amount(sim, port, "metal"), 6);
        Assert.Contains("sale", Scene.Rewards(sim, 1));
        // Пока везли и летели, прошло не меньше полёта.
        Assert.True(sim.Time > global::Base.Shared.Rules.TradeRules.SellSeconds);
    }

    [Fact(DisplayName = "корабль улетает, когда товар привезён; рейс улучшенного космопорта короче; выручка — целыми кредитами")]
    public void ShipLeaves()
    {
        using var sim = Blank();
        sim.SetCredits(1, 0);
        var port = Place(sim, "spaceport", 0, 0);
        sim.Set(port, "Building.level", 2);
        // Привезено без крошки меньше погрешности: продаётся, и выручка округляется.
        sim.SetStock(port, "metal", 20 - 1e-10);
        sim.PutTrade(port, "metal", 20);
        sim.Advance();
        var ticks = sim.FlightTicks(port, global::Base.Shared.Rules.TradeRules.SellSeconds);
        Assert.True(ticks < Math.Round(global::Base.Shared.Rules.TradeRules.SellSeconds / Tick));
        var order = sim.TradeOf(port)!;
        Assert.Equal((ticks, ticks), (order.Left, order.Total));
        sim.Advance(ticks - 1);
        Assert.NotNull(sim.TradeOf(port));
        sim.Advance();
        Assert.Null(sim.TradeOf(port));
        Assert.Equal(20 * Tables.All.Resources["metal"].Price + Tables.All.Rewards["sale"].Credits, sim.Credits(1));
    }

    [Fact(DisplayName = "переработка отправляет готовое в хранилище по трубам пачками, а вставшая — до крошки")]
    public void RefineryPushesByPipes()
    {
        using var @base = Make("smelter");
        var (sim, smelter) = (@base.Sim, @base.Buildings[0]);
        // Руда ещё есть: переработка сделает больше, и 3 единицы ждут полной пачки.
        sim.SetStock(smelter, "metal", 3);
        sim.SetStock(smelter, "metalOre", 20);
        sim.Seconds(0.5);
        Assert.Equal(0, Amount(sim, @base.Store("metal"), "metal"));

        // Руды нет и не везут: остаток меньше порога увозят весь.
        sim.SetStock(smelter, "metal", 7);
        sim.SetStock(smelter, "metalOre", 0);
        Until(sim, () => Amount(sim, smelter, "metal") < 1e-9, 200);
        Until(sim, () => Amount(sim, @base.Store("metal"), "metal") >= 7 - 1e-6, 200);
    }

    [Fact(DisplayName = "дробный остаток материалов довозят: завод не встаёт на 19,7 из 20")]
    public void FractionalRemainderDelivered()
    {
        using var @base = Make("factory");
        var (sim, factory) = (@base.Sim, @base.Buildings[0]);
        var tank = Tables.Units["tank"].Materials;
        // Дробное — прямо в мир: правка edit.stock округляет до целых.
        sim.Set(factory, "Stock.metal", tank["metal"] - 0.3);
        sim.SetStock(factory, "parts", tank["parts"]);
        @base.Stash(("metal", 10));
        sim.Send(1, new ProduceOrder { Producer = factory, Unit = "tank" });
        Until(sim, () => sim.Num(factory, "Producer.progress") > 0, 60);
        Assert.True(Amount(sim, @base.Store("metal"), "metal") < 10);
    }

    [Fact(DisplayName = "груз для заявки грузовик берёт и с дропа в зоне, если он ближе склада")]
    public void TruckTakesFromDropInZone()
    {
        using var @base = Make();
        var (sim, x, y) = (@base.Sim, @base.X, @base.Y);
        var builder = Spawn(sim, "builder", 1, x + 5, y + 8);
        var drop = sim.DropItems(x + 4, y + 8, ("blocks", 30));
        sim.Send(1, new BuildOrder { Building = "factory", X = x, Y = y + 4, Builders = [builder] });
        sim.Advance();
        var site = sim.SiteAt(x, y + 4);
        var truck = Spawn(sim, "truck", 1, x + 6, y + 8);
        // Сразу по заявке стройки — с дропа на площадку, а не сначала в хранилище.
        Until(sim, () => sim.Hauler(truck).From != 0, 200);
        Assert.Equal(drop, sim.Hauler(truck).From);
        Assert.Equal(site, sim.Hauler(truck).To);
        Until(sim, () => !sim.Has(site, Names.Site.Name), 200);
    }

    [Fact(DisplayName = "ПКМ грузовиком по стройке: нужное из кузова он везёт сразу")]
    public void SupplyOrderCarriesCargoAtOnce()
    {
        using var @base = Make();
        var (sim, x, y) = (@base.Sim, @base.X, @base.Y);
        var builder = Spawn(sim, "builder", 1, x + 5, y + 8);
        sim.Send(1, new BuildOrder { Building = "factory", X = x, Y = y + 4, Builders = [builder] });
        sim.Advance();
        var site = sim.SiteAt(x, y + 4);
        var truck = Spawn(sim, "truck", 1, x + 6, y + 8);
        sim.SetStock(truck, "blocks", 25);
        Assert.True(sim.CanSupply(1, site));
        sim.Send(1, new SupplyOrder { Units = [truck], Target = site });
        sim.Advance();
        Assert.Equal(site, sim.Hauler(truck).To);
        Until(sim, () => !sim.Has(site, Names.Site.Name), 200);
        // Остаток грузовик увёз в хранилище, а приказ исполнен: он снова свободен.
        Until(sim, () => Amount(sim, truck, "blocks") == 0, 200);
        Assert.Equal(0u, sim.Hauler(truck).Supply);
    }

    [Fact(DisplayName = "чего нет в сети стройки, свободный грузовик везёт из другой сети")]
    public void TruckBringsFromOtherNetwork()
    {
        using var @base = Make();
        var (sim, x, y) = (@base.Sim, @base.X, @base.Y);
        var builder = Spawn(sim, "builder", 1, x + 5, y + 8);
        sim.Send(1, new BuildOrder { Building = "factory", X = x, Y = y + 4, Builders = [builder] });
        // Труба от главного здания к площадке: стройка в сети базы.
        sim.PutBuilding("pipe", x, y + 3, 1);
        sim.Advance();
        var site = sim.SiteAt(x, y + 4);
        // Стройблоки — только на складе далеко за базой, в отдельной сети: трубы до него не доходят. В TS склад — у
        // самого края досягаемости строителя (FETCH_REACH = 40), строитель везёт блоки сам, а грузовик получает работу
        // от заявки в 0,5 блока, мелькающей в тике прихода строителя, — только если этот тик кратен тику диспетчера. Здесь
        // склад дальше досягаемости строителя: блоки везёт грузовик, как и сказано в заголовке.
        uint far = 0;
        foreach (var dy in new[] { 0, -6, 6, -12, 12 })
            for (var dx = 46; dx < 120 && far == 0; dx++)
                if (Scene.CanPlace(sim, "blockYard", x + dx, y + dy))
                    far = sim.PutBuilding("blockYard", x + dx, y + dy, 1);
        Assert.NotEqual(0u, far);
        sim.SetStock(far, "blocks", 50);
        var truck = Spawn(sim, "truck", 1, x + 6, y + 8);
        Until(sim, () => sim.Hauler(truck).From != 0, 200);
        Assert.Equal(far, sim.Hauler(truck).From);
        Until(sim, () => !sim.Has(site, Names.Site.Name), 200);
        Assert.Equal(50 - Blocks("factory"), Amount(sim, far, "blocks"), 6);
    }

    [Fact(DisplayName = "грузу некуда деться — грузовик везёт его заказчику, а нет заказчика — обратно, и снова свободен")]
    public void HomelessCargoGoesToRequesterOrBack()
    {
        using var @base = Make("smelter", "factory");
        var (sim, x, y) = (@base.Sim, @base.X, @base.Y);
        var (smelter, factory) = (@base.Buildings[0], @base.Buildings[1]);
        var shelf = @base.Store("metal");
        sim.SetStock(shelf, "metal", sim.Num(shelf, "Stock.capacity"));
        var truck = Spawn(sim, "truck", 1, x + 5, y + 4);
        double Cargo() => Amount(sim, truck, "metal");
        void Load()
        {
            sim.SetStock(truck, "metal", 10);
            sim.SetHauler(truck, from: smelter, to: 0, resource: "metal", amount: 10, full: true);
        }
        // Заказчик есть — металл едет на завод.
        sim.Send(1, new ProduceOrder { Producer = factory, Unit = "buggy" });
        Load();
        Until(sim, () => Cargo() < 1e-9, 60);
        var started = sim.Num(factory, "Producer.progress") > 0 ? Tables.Units["buggy"].Materials["metal"] : 0;
        Assert.True(Amount(sim, factory, "metal") + started > 0);
        // Заказчиков нет — обратно в переработку.
        sim.Set(factory, "Producer.queue", []);
        sim.Set(factory, "Producer.current", -1);
        Until(sim, () => sim.Bytes(factory, "Producer.queue").Length == 0 && !sim.Hauler(truck).Full, 60);
        Load();
        Until(sim, () => Cargo() < 1e-9, 60);
        Assert.True(Amount(sim, smelter, "metal") > 0);
    }

    [Fact(DisplayName = "машинный завод строит MCV из стройблоков и металла, и тот разворачивается в новую базу")]
    public void FactoryBuildsMcv()
    {
        using var @base = Make("factory");
        var (sim, factory) = (@base.Sim, @base.Buildings[0]);
        @base.Stash(("blocks", 40), ("metal", 40));
        sim.Send(1, new ProduceOrder { Producer = factory, Unit = "mcv" });
        Until(sim, () => Scene.Units(sim, "mcv") > 0, 200);
        Assert.True(sim.Has(Scene.UnitsOf(sim, "mcv")[0], Names.Producer.Name));
    }

    [Fact(DisplayName = "груз, который уже везут на переработку, занимает её место: второй грузовик туда не едет и не набирает руду")]
    public void CargoOnTheWayTakesRefineryRoom()
    {
        using var @base = Make("smelter");
        var (sim, x, y) = (@base.Sim, @base.X, @base.Y);
        var smelter = @base.Buildings[0];
        const double room = 6;
        sim.SetStock(smelter, "metalOre", Tables.Buildings["smelter"].Limits["metalOre"] - room);
        var first = Spawn(sim, "truck", 1, x, y + 5);
        var second = Spawn(sim, "truck", 1, x + 2, y + 5);
        Assert.Equal(smelter, sim.RefineryFor(first, "metalOre"));
        sim.SetStock(first, "metalOre", room);
        sim.SetHauler(first, to: smelter, resource: "metalOre", amount: room, full: true);
        Assert.True(sim.SpaceFor(second, smelter, "metalOre") <= 1e-9);
        Assert.Equal(0u, sim.RefineryFor(second, "metalOre"));
        // Свой груз грузовик у себя же места не отнимает.
        Assert.Equal(room, sim.SpaceFor(first, smelter, "metalOre"));
    }
}
