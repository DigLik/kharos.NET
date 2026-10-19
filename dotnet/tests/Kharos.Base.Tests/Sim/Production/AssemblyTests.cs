using Base.Shared;
using Base.Shared.Orders;
using Kharos.Base.Tests.Kernel;
using static Kharos.Base.Tests.Logistics.LogisticsScene;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Production;

/// <summary>Цеха изделий: сборка, простой, выключатель, заказ сырья (packages/tests/assembly.test.ts).</summary>
public sealed class AssemblyTests
{
    /// <summary>Хранилища по порядку buildings.json (STORES).</summary>
    private static readonly string[] Stores = [.. Tables.Buildings.Where(pair => pair.Value.Stores).Select(pair => pair.Key)];

    /// <summary>База игрока 1 в ряд на скале (base в assembly.test.ts): главное здание, две электростанции, здания из списка и ряд хранилищ; над ними труба.</summary>
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

    private static Base Make(params string[] extra) => Make(true, extra);

    /// <summary>База; piped — с трубой над рядом (все в одной сети), без неё каждое здание — сеть само по себе.</summary>
    private static Base Make(bool piped, params string[] extra)
    {
        var sim = Create(512, new { techTree = false });
        sim.SetCredits(1, 10000);
        var layout = new List<(string Type, int Dx)> { ("command", 0), ("generator", 4), ("generator", 7) };
        var at = 10;
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
                for (var dx = 0; dx < at && piped; dx++)
                    sim.PutBuilding("pipe", x + dx, y - 1, 1);
                return new Base(sim, x, y, buildings[0], buildings.GetRange(3, extra.Length), buildings.GetRange(3 + extra.Length, Stores.Length));
            }
        throw new InvalidOperationException("Не нашлось места под базу");
    }

    /// <summary>Сколько секунд идёт сборка изделия (cycleSeconds): единиц сырья в рецепте на единицу в секунду.</summary>
    private static double Cycle(string product) => Tables.All.Products[product].Recipe.Values.Sum();

    /// <summary>Сколько этого изделия или ресурса лежит в хранилищах игрока (stockOf(...).items).</summary>
    private static double Stored(Sim sim, string good) =>
        sim.Zones(1).SelectMany(zone => zone.Buildings)
            .Where(entity => sim.Has(entity, Names.Stock.Name) && Scene.TypeOf(sim, entity) is { } type && Tables.Buildings[type].Stores)
            .Sum(entity => sim.Stock(entity, good));

    private static double Progress(Sim sim, uint workshop) => sim.Num(workshop, Names.Assembly.Name, Names.Assembly.Progress);

    private static bool On(Sim sim, uint workshop) => sim.Num(workshop, Names.Assembly.Name, Names.Assembly.On) != 0;

    [Fact(DisplayName = "завод стройблоков собирает их из сырья хранилищ, отвозит в хранилище и встаёт, когда сырьё кончилось")]
    public void BlockPlantAssemblesFromStores()
    {
        using var @base = Make("blockPlant");
        var (sim, x, y) = (@base.Sim, @base.X, @base.Y);
        var workshop = @base.Buildings[0];
        @base.Stash(("metal", 20), ("silicon", 10));
        sim.Send(1, new WorkOrder { Building = workshop, On = true });
        for (var i = 0; i < 2; i++)
            Spawn(sim, "truck", 1, x + 2 + i * 2, y + 5);

        double Blocks() => Stored(sim, "blocks");
        // Сырья — на 10 сборок: нормы нет, цех съедает его целиком.
        Until(sim, () => Blocks() + sim.Stock(workshop, "blocks") >= 10, 300);
        sim.Seconds(Cycle("blocks") * 5);
        Assert.Equal(10, Blocks() + sim.Stock(workshop, "blocks"));
        Assert.Equal(0, Stored(sim, "metal") + sim.Stock(workshop, "metal"), 6);
        // Свободные грузовики увезли готовое в хранилища: цех не копит его у себя.
        Assert.True(Blocks() > 0);
    }

    [Fact(DisplayName = "цех встаёт, когда готовому некуда лечь")]
    public void WorkshopStopsWhenNoRoom()
    {
        using var @base = Make("ammoPlant");
        var (sim, x, y) = (@base.Sim, @base.X, @base.Y);
        var plant = @base.Buildings[0];
        @base.Stash(("metal", 500));
        sim.Send(1, new WorkOrder { Building = plant, On = true });
        for (var i = 0; i < 2; i++)
            Spawn(sim, "truck", 1, x + 2 + i * 2, y + 5);
        sim.Seconds(240);
        double Metal() => Stored(sim, "metal") + sim.Stock(plant, "metal");
        var before = Metal();
        sim.Seconds(30);
        // Бункер и склад цеха полны: металл больше не тратится, хотя его ещё много.
        Assert.Equal(before, Metal());
        Assert.True(before > 100);
        Assert.False(sim.HasRoom(plant));
    }

    [Fact(DisplayName = "без энергии начатая сборка стоит: сырьё на неё ушло один раз, следующее не трогается")]
    public void NoPowerHoldsStartedAssembly()
    {
        using var @base = Make("blockPlant");
        var sim = @base.Sim;
        var workshop = @base.Buildings[0];
        sim.Erase([.. sim.With(Names.Building.Name).Where(entity => Scene.TypeOf(sim, entity) == "generator")]);
        sim.SetStock(workshop, "metal", 4);
        sim.SetStock(workshop, "silicon", 2);
        sim.Send(1, new WorkOrder { Building = workshop, On = true });
        sim.Seconds(1);
        Assert.Equal([2, 1], [sim.Stock(workshop, "metal"), sim.Stock(workshop, "silicon")]);
        Assert.True(Progress(sim, workshop) > 0);
        sim.Seconds(Cycle("blocks") * 2);
        Assert.Equal([2, 1, 0], [sim.Stock(workshop, "metal"), sim.Stock(workshop, "silicon"), sim.Stock(workshop, "blocks")]);
    }

    [Fact(DisplayName = "новый завод выключен: сырья не заказывает; выключенный замирает посреди сборки, а сырьё увозят")]
    public void NewPlantIsOffAndFreezesMidAssembly()
    {
        using var @base = Make("ammoPlant");
        var (sim, x, y) = (@base.Sim, @base.X, @base.Y);
        var plant = @base.Buildings[0];
        @base.Stash(("metal", 30));
        Spawn(sim, "truck", 1, x + 2, y + 5);

        // Выключенный завод ничего не просит и не собирает.
        Assert.False(On(sim, plant));
        sim.Seconds(10);
        Assert.Equal(0, sim.Stock(plant, "metal"));
        Assert.Equal(0, Progress(sim, plant));

        // Чужой приказ не принимается, свой — включает.
        sim.Send(2, new WorkOrder { Building = plant, On = true });
        sim.Advance();
        Assert.False(On(sim, plant));
        sim.Send(1, new WorkOrder { Building = plant, On = true });
        Until(sim, () => Progress(sim, plant) > 0, 300);

        // Выключили — начатая сборка замирает, а лежащий у завода металл грузовик увозит в хранилище.
        sim.Send(1, new WorkOrder { Building = plant, On = false });
        sim.Advance();
        var paused = Progress(sim, plant);
        Until(sim, () => sim.Stock(plant, "metal") < 1e-6, 300);
        var made = Stored(sim, "ammo") + sim.Stock(plant, "ammo");
        sim.Seconds(10);
        Assert.Equal(paused, Progress(sim, plant));
        Assert.Equal(made, Stored(sim, "ammo") + sim.Stock(plant, "ammo"));
        // Включили — сборка продолжается с того же места.
        sim.Send(1, new WorkOrder { Building = plant, On = true });
        Until(sim, () => Progress(sim, plant) == 0, 300);
    }

    [Fact(DisplayName = "изделия не продаются: космопорт берёт только ресурсы")]
    public void ProductsAreNotSold()
    {
        using var @base = Make("spaceport");
        var sim = @base.Sim;
        var port = @base.Buildings[0];
        @base.Stash(("blocks", 30), ("metal", 30));
        sim.Send(1, new SellOrder { Port = port, Resource = "blocks", Amount = 10 });
        sim.Advance();
        Assert.False(sim.Has(port, Names.Trade.Name));
        sim.Send(1, new SellOrder { Port = port, Resource = "metal", Amount = 10 });
        sim.Advance();
        Assert.True(sim.Has(port, Names.Trade.Name));
    }

    [Fact(DisplayName = "турель строится пустой, тратит патроны на выстрелы и без них молчит")]
    public void TurretSpendsAmmo()
    {
        using var sim = Scene.Create();
        var defense = Combat.Arena.Place(sim, "turret", 0, 0, 1);
        var full = Tables.Buildings["turret"].Inventory!.Value;
        Assert.Equal(0, sim.Stock(defense, "ammo"));

        // Пока патронов не подвезли, турель молчит.
        var foe = Scene.Spawn(sim, "tank", 2, 4, 0);
        sim.Seconds(2);
        Assert.Equal(1, sim.HealthOf(foe));
        sim.SetStock(defense, "ammo", full);
        sim.Seconds(2);
        Assert.True(sim.Stock(defense, "ammo") < full);
        Assert.True(sim.HealthOf(foe) < 1);

        // Патроны кончились — новых выстрелов нет.
        sim.SetStock(defense, "ammo", Tables.All.Weapons["machinegun"].Ammo!.Value / 2);
        sim.Seconds(0.5);
        var stopped = sim.HealthOf(foe);
        sim.Seconds(2);
        Assert.Equal(stopped, sim.HealthOf(foe));
        // Юниты стреляют бесплатно: танк бьёт турель и без всяких патронов.
        Assert.True(sim.HealthOf(defense) < 1);
    }

    [Fact(DisplayName = "расстрелявшая запас турель заказывает патроны, и грузовик везёт их из хранилища")]
    public void EmptyTurretOrdersAmmo()
    {
        using var @base = Make("turret");
        var (sim, x, y) = (@base.Sim, @base.X, @base.Y);
        var defense = @base.Buildings[0];
        var full = Tables.Buildings["turret"].Inventory!.Value;
        @base.Stash(("ammo", 100));
        // Расстреляла меньше четверти — заказа ещё нет.
        sim.SetStock(defense, "ammo", full - 20);
        Spawn(sim, "truck", 1, x + 2, y + 5);
        sim.Seconds(20);
        Assert.Equal(full - 20, sim.Stock(defense, "ammo"));

        // Расстреляла больше — грузовики дозаряжают её кузовами, пока пустого места не станет меньше четверти.
        sim.SetStock(defense, "ammo", 10);
        Until(sim, () => sim.Stock(defense, "ammo") > full * 0.75, 300);
        sim.Seconds(20);
        var loaded = sim.Stock(defense, "ammo");
        Assert.True(loaded > full * 0.75);
        Assert.Equal(100 - (loaded - 10), sim.Stock(@base.Store("ammo"), "ammo"), 6);
    }

    [Fact(DisplayName = "компоненты нужны верхнему тиру: разрядник ждёт их на заводе")]
    public void PartsNeededByTopTier()
    {
        Assert.Equal(3, Tables.Units["tesla"].Materials["parts"]);
        Assert.Equal(1, Tables.Units["carrier"].Materials["parts"]);
        Assert.Equal(new Dictionary<string, double> { ["blocks"] = 10 }, Tables.Buildings["factory"].Materials);
        // Космопорт — исключение: с него начинаются деньги, и строится он из металла.
        Assert.Equal(new Dictionary<string, double> { ["metal"] = 20 }, Tables.Buildings["spaceport"].Materials);
    }

    [Fact(DisplayName = "закупка с орбиты: кредиты сразу, груз через полёт корабля, грузовики увозят его в хранилище")]
    public void OrbitalPurchase()
    {
        using var @base = Make("spaceport", "metalYard");
        var (sim, x, y) = (@base.Sim, @base.X, @base.Y);
        var port = @base.Buildings[0];
        Spawn(sim, "truck", 1, x + 2, y + 5);
        var credits = sim.Credits(1);
        var price = global::Base.Shared.Rules.TradeRules.BuyPrice(Tables.All.Resources["metal"].Price);

        // Чужой космопорт не закупает; дороже, чем есть кредитов, — тоже.
        sim.Send(2, new BuyOrder { Port = port, Resource = "metal", Amount = 10 });
        sim.Send(1, new BuyOrder { Port = port, Resource = "metal", Amount = 100000 });
        sim.Advance();
        Assert.Equal(credits, sim.Credits(1));

        sim.Send(1, new BuyOrder { Port = port, Resource = "metal", Amount = 50 });
        sim.Advance();
        Assert.Equal(credits - 50 * price, sim.Credits(1));
        // Пока корабль летит, космопорт занят: вторая закупка не принимается.
        sim.Send(1, new BuyOrder { Port = port, Resource = "silicon", Amount = 10 });
        sim.Advance();
        Assert.Equal("metal", sim.TradeOf(port)!.Resource);

        sim.Seconds(global::Base.Shared.Rules.TradeRules.BuySeconds);
        Assert.False(sim.Has(port, Names.Trade.Name));
        Until(sim, () => Stored(sim, "metal") >= 50 - 1e-6);
        Assert.True(sim.Stock(port, "metal") < 1e-6);
    }

    [Fact(DisplayName = "старое сохранение получает нынешние пределы склада переработки")]
    public void SavedRefineryKeepsLimits()
    {
        var (sim, buildings, _, _, _) = Row(["smelter"], truck: false);
        using var _ = sim;
        var smelter = buildings[0];
        using var loaded = sim.ThroughSave();
        Assert.Equal(60, loaded.Num(smelter, Names.Stock.Name, Names.Stock.Capacity));
        Assert.Equal(30, loaded.Num(smelter, Names.Limits.Name, "metalOre"));
        Assert.Equal(30, loaded.Num(smelter, Names.Limits.Name, "metal"));
    }

    [Fact(DisplayName = "готовое расходится по всем хранилищам, а не копится в ближайшем")]
    public void ProductSpreadsAcrossStores()
    {
        using var @base = Make("ammoPlant", "ammoBunker");
        var (sim, x, y) = (@base.Sim, @base.X, @base.Y);
        var (plant, bunker) = (@base.Buildings[0], @base.Buildings[1]);
        @base.Stash(("metal", 200));
        sim.Send(1, new WorkOrder { Building = plant, On = true });
        for (var i = 0; i < 3; i++)
            Spawn(sim, "truck", 1, x + 2 + i * 2, y + 5);
        sim.Seconds(90);
        // В обоих бункерах — ряд хранилищ base добавляет ещё один — что-то есть.
        var filled = sim.With(Names.Stock.Name).Where(entity => entity != plant && sim.Stock(entity, "ammo") > 0 && Scene.TypeOf(sim, entity) is { } type && Stores.Contains(type)).ToList();
        Assert.True(filled.Count >= 2);
        Assert.Contains(bunker, filled);
    }

    // ===== Сборка без труб: сырьё кладётся на склад цеха прямо (тесты не из TS: проверяют систему assemble сами) =====

    [Fact(DisplayName = "цех собирает изделие из сырья своего склада: сырьё уходит в начале сборки, готовое ложится по её окончании")]
    public void AssembleConsumesAtStartAndPutsAtEnd()
    {
        using var @base = Make(false, "ammoPlant");
        var (sim, plant) = (@base.Sim, @base.Buildings[0]);
        sim.SetStock(plant, "metal", 2);
        sim.Send(1, new WorkOrder { Building = plant, On = true });
        sim.Seconds(0.2);
        // Сборка началась: металл ушёл в работу, изделия ещё нет.
        Assert.Equal(1, sim.Stock(plant, "metal"));
        Assert.True(Progress(sim, plant) > 0);
        Assert.Equal(0, sim.Stock(plant, "ammo"));
        sim.Seconds(Cycle("ammo") + 0.3);
        Assert.Equal(Tables.All.Products["ammo"].Yield, sim.Stock(plant, "ammo"));
        // Второй металл уже в работе, потом цех встаёт: сырья больше нет.
        sim.Seconds(Cycle("ammo") + 0.3);
        Assert.Equal(0, sim.Stock(plant, "metal"));
        Assert.Equal(2 * Tables.All.Products["ammo"].Yield, sim.Stock(plant, "ammo"));
        Assert.Equal(0, Progress(sim, plant));
    }

    [Fact(DisplayName = "цех не начинает сборку, пока на складе не хватает сырья на неё или нет места под готовое")]
    public void AssembleWaitsForMaterialsAndRoom()
    {
        using var @base = Make(false, "blockPlant");
        var (sim, plant) = (@base.Sim, @base.Buildings[0]);
        sim.Send(1, new WorkOrder { Building = plant, On = true });
        // Не хватает кремния: металла вдоволь, но сборка не начинается.
        sim.SetStock(plant, "metal", 4);
        sim.Seconds(2);
        Assert.Equal(4, sim.Stock(plant, "metal"));
        Assert.Equal(0, Progress(sim, plant));
        // Сырья в обрез, но блоки уже набраны до предела: места под готовое нет.
        sim.SetStock(plant, "silicon", 2);
        sim.SetStock(plant, "blocks", Tables.Buildings["blockPlant"].Limits["blocks"]);
        Assert.False(sim.HasRoom(plant));
        sim.Seconds(2);
        Assert.Equal(4, sim.Stock(plant, "metal"));
        Assert.Equal(0, Progress(sim, plant));
        // Место нашлось — сборка пошла.
        sim.SetStock(plant, "blocks", 0);
        Assert.True(sim.HasRoom(plant));
        sim.Seconds(1);
        Assert.Equal(2, sim.Stock(plant, "metal"));
        Assert.True(Progress(sim, plant) > 0);
    }

    [Fact(DisplayName = "доделанному изделию некуда лечь: сборка ждёт на полном ходу, пока склад не освободится")]
    public void FinishedProductWaitsForRoom()
    {
        using var @base = Make(false, "ammoPlant");
        var (sim, plant) = (@base.Sim, @base.Buildings[0]);
        sim.SetStock(plant, "metal", 1);
        sim.Send(1, new WorkOrder { Building = plant, On = true });
        sim.Seconds(0.2);
        Assert.True(Progress(sim, plant) > 0);
        // Склад забит до предела посреди сборки.
        sim.SetStock(plant, "ammo", Tables.Buildings["ammoPlant"].Limits["ammo"]);
        sim.Seconds(Cycle("ammo") + 1);
        Assert.Equal(Cycle("ammo"), Progress(sim, plant), 6);
        sim.SetStock(plant, "ammo", 5);
        sim.Seconds(0.2);
        Assert.Equal(0, Progress(sim, plant));
        Assert.Equal(5 + Tables.All.Products["ammo"].Yield, sim.Stock(plant, "ammo"));
    }

    [Fact(DisplayName = "выключенный цех замирает посреди сборки и продолжает с того же места")]
    public void SwitchedOffWorkshopResumes()
    {
        using var @base = Make(false, "blockPlant");
        var (sim, plant) = (@base.Sim, @base.Buildings[0]);
        sim.SetStock(plant, "metal", 4);
        sim.SetStock(plant, "silicon", 2);
        // Новый цех выключен: ничего не собирает.
        Assert.False(On(sim, plant));
        sim.Seconds(2);
        Assert.Equal(0, Progress(sim, plant));
        Assert.Equal(4, sim.Stock(plant, "metal"));
        sim.Send(1, new WorkOrder { Building = plant, On = true });
        sim.Seconds(1);
        sim.Send(1, new WorkOrder { Building = plant, On = false });
        sim.Advance();
        var paused = Progress(sim, plant);
        Assert.True(paused > 0 && paused < Cycle("blocks"));
        sim.Seconds(10);
        Assert.Equal(paused, Progress(sim, plant));
        Assert.Equal(2, sim.Stock(plant, "metal"));
        sim.Send(1, new WorkOrder { Building = plant, On = true });
        sim.Seconds(Cycle("blocks"));
        Assert.Equal(1, sim.Stock(plant, "blocks"));
    }
}
