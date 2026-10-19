using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Rules;
using Kharos.Base.Tests.Kernel;
using static Kharos.Base.Tests.Researching.Lab;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Researching;

/// <summary>Уровни зданий: цена, работа строителей, отмена, что даёт уровень (packages/tests/upgrades.test.ts).</summary>
public sealed class UpgradesTests
{
    /// <summary>База игрока 1 в ряд на скале в одной сети труб: главное здание, здания из списка и все хранилища; рядом — строитель.</summary>
    private sealed record Yard(Sim Sim, int X, int Y, uint Core, List<uint> Buildings, List<uint> Shelves, uint Builder) : IDisposable
    {
        public uint Store(string good) => Shelves[Array.IndexOf(Stores, StoreFor(good))];

        public void Stash(params (string Good, double Amount)[] items)
        {
            foreach (var (good, amount) in items)
                Sim.Set(Store(good), "Stock." + good, amount);
        }

        public void Dispose() => Sim.Dispose();
    }

    private static readonly object Rules = new { techTree = false };

    private static Yard Make(params string[] extra)
    {
        var sim = Create(Rules);
        sim.SetCredits(1, 10000);
        var layout = new List<(string Type, int Dx)> { ("command", 0) };
        var at = 4;
        foreach (var type in extra.Concat(Stores))
        {
            layout.Add((type, at));
            at += Width(type) + 1;
        }
        for (var y = -100; y < 100; y++)
            for (var x = -100; x < 100; x++)
            {
                if (!layout.All(item => Scene.Rock(sim, x + item.Dx, y, Width(item.Type), Height(item.Type))) || !Scene.Rock(sim, x, y + 4, 7, 3) || !Scene.Rock(sim, x, y - 1, at, 1))
                    continue;
                if (!layout.All(item => Scene.CanPlace(sim, item.Type, x + item.Dx, y)))
                    continue;
                if (!Scene.CanPlace(sim, "command", x, y + 4) || !Scene.CanPlace(sim, "command", x + 4, y + 4))
                    continue;
                if (!Enumerable.Range(0, at).All(dx => Scene.CanPlace(sim, "pipe", x + dx, y - 1)))
                    continue;
                var buildings = layout.Select(item => sim.PutBuilding(item.Type, x + item.Dx, y, 1)).ToList();
                for (var dx = 0; dx < at; dx++)
                    sim.PutBuilding("pipe", x + dx, y - 1, 1);
                var builder = Scene.Spawn(sim, "builder", 1, x + 5, y + 4);
                return new Yard(sim, x, y, buildings[0], buildings.GetRange(1, extra.Length), buildings.GetRange(1 + extra.Length, Stores.Length), builder);
            }
        throw new InvalidOperationException("Не нашлось места под базу");
    }

    private static void Upgrade(Sim sim, uint building, params uint[] builders) => sim.Send(1, new UpgradeOrder { Building = building, Builders = builders });

    private static void Until(Sim sim, Func<bool> done, double limit = 300) => Assert.True(sim.Until(done, limit), "Не дождались");

    private static (double Credits, Dictionary<string, double> Materials) Cost(string type, double level) => BuildRules.UpgradeCost(Tables.Buildings[type], level);

    [Fact(DisplayName = "улучшение электростанции: цена списана сразу из сети, строитель поднимает уровень, выработка растёт в полтора раза")]
    public void PlantUpgrade()
    {
        using var yard = Make("generator");
        var (sim, plant, builder) = (yard.Sim, yard.Buildings[0], yard.Builder);
        var (price, materials) = Cost("generator", 1);
        Assert.Equal(150, price);
        Assert.Equal(new Dictionary<string, double> { ["blocks"] = 10 }, materials);
        yard.Stash(("blocks", 25));
        Assert.Equal(10, sim.Economy(1).Produced);
        var credits = sim.Credits(1);

        Upgrade(sim, plant, builder);
        sim.Advance();
        Assert.True(Upgrading(sim, plant));
        Assert.Equal(150, credits - sim.Credits(1));
        Assert.Equal(15, Stocked(sim, "blocks"));
        // Пока улучшают, здание работает и второй заказ не берёт.
        Assert.Equal(10, sim.Economy(1).Produced);
        Assert.False(sim.Upgradable(1, plant));

        Until(sim, () => sim.LevelOf(plant) == 2);
        Assert.False(Upgrading(sim, plant));
        Assert.Equal(15, sim.Economy(1).Produced);
        // Работа — как стройка на ту же сумму: 150 кредитов одному строителю — 7,5 секунды и дорога.
        Assert.True(sim.Time > 7.5);
        // Разбор возвращает половину цены и вложенного в уровень; уровень переживает сохранение.
        Assert.Equal(225, BuildRules.RefundOf(Tables.Buildings["generator"], 2));
        using var copy = sim.ThroughSave();
        Assert.Equal(2, copy.LevelOf(plant));
        Assert.Equal(15, copy.Economy(1).Produced);
    }

    [Fact(DisplayName = "без готового в сети здания и без кредитов улучшение не заказать; резерв хранилища не трогают")]
    public void NoMaterialsNoCredits()
    {
        using var yard = Make("generator");
        var (sim, plant, builder) = (yard.Sim, yard.Buildings[0], yard.Builder);
        Assert.Equal("materials", sim.UpgradeLack(1, plant));
        Upgrade(sim, plant, builder);
        sim.Advance();
        Assert.False(Upgrading(sim, plant));

        yard.Stash(("blocks", 12));
        Assert.Null(sim.UpgradeLack(1, plant));
        sim.Set(yard.Store("blocks"), "Stock.reserve", 5);
        Assert.Equal("materials", sim.UpgradeLack(1, plant));
        sim.Set(yard.Store("blocks"), "Stock.reserve", 0);

        sim.SetCredits(1, 100);
        Assert.Equal("credits", sim.UpgradeLack(1, plant));
        Upgrade(sim, plant, builder);
        sim.Advance();
        Assert.False(Upgrading(sim, plant));
        Assert.Equal(12, Stocked(sim, "blocks"));
        // Главное здание, заводы и турели не улучшают.
        Assert.True(sim.Upgradable(1, plant));
        Assert.False(sim.Upgradable(1, yard.Core));
    }

    [Fact(DisplayName = "электростанцию без трубы улучшают с готового её зоны: энергосеть та же, а дальнюю — нет")]
    public void PlantWithoutPipeUsesItsGrid()
    {
        using var yard = Make();
        var (sim, builder) = (yard.Sim, yard.Builder);
        // Под главным зданием, без трубы: своя сеть из одного здания, но круг главного здания её задевает.
        var plant = sim.PutBuilding("generator", yard.X, yard.Y + 4, 1);
        yard.Stash(("blocks", 12));
        Assert.Null(sim.UpgradeLack(1, plant));
        Upgrade(sim, plant, builder);
        sim.Advance();
        Assert.True(Upgrading(sim, plant));
        Assert.Equal(2, Stocked(sim, "blocks"));
        // Отмена возвращает готовое в хранилища зоны.
        sim.Send(1, new CancelUpgradeOrder { Building = plant });
        sim.Advance();
        Assert.Equal(12, Stocked(sim, "blocks"));

        // Станция вдали от базы — в своей энергосети, готового там нет.
        uint far = 0;
        for (var dy = 40; dy < 200 && far == 0; dy++)
            if (Scene.CanPlace(sim, "generator", yard.X, yard.Y + dy))
                far = sim.PutBuilding("generator", yard.X, yard.Y + dy, 1);
        Assert.Equal("materials", sim.UpgradeLack(1, far));
    }

    [Fact(DisplayName = "отмена улучшения возвращает кредиты и готовое; разбор отменяет его сам")]
    public void CancelAndDemolishRefund()
    {
        using var yard = Make("generator", "generator");
        var (sim, plant, other, builder) = (yard.Sim, yard.Buildings[0], yard.Buildings[1], yard.Builder);
        yard.Stash(("blocks", 20));
        var credits = sim.Credits(1);
        Upgrade(sim, plant);
        Upgrade(sim, other);
        sim.Advance();
        Assert.Equal(0, Stocked(sim, "blocks"));
        Assert.Equal(300, credits - sim.Credits(1));

        // Щелчок строителем по улучшаемому зданию — приказ улучшать его.
        var (x, y) = sim.At(plant);
        var resolved = Assert.Single(sim.Resolve(1, [builder], x + 0.5, y + 0.5));
        Assert.Equal((AssistOrder.Name, plant), (resolved.Name, resolved.Target));
        Assert.Equal([builder], resolved.Units);

        sim.Send(1, new CancelUpgradeOrder { Building = plant });
        sim.Advance();
        Assert.False(Upgrading(sim, plant));
        Assert.Equal(10, Stocked(sim, "blocks"));
        Assert.Equal(150, credits - sim.Credits(1));

        sim.Send(1, new DemolishOrder { Building = other, Builders = [builder] });
        sim.Advance();
        Assert.False(Upgrading(sim, other));
        Assert.True(sim.Num(other, "Site.demolish") != 0);
        Assert.Equal(20, Stocked(sim, "blocks"));
        Assert.Equal(credits, sim.Credits(1));
        Assert.Equal(1, sim.LevelOf(other));
    }

    [Fact(DisplayName = "космопорт до третьего уровня: выше — ещё и компоненты, трюм вдвое больше, рейс вдвое короче, дальше не поднять")]
    public void SpaceportToThirdLevel()
    {
        using var yard = Make("generator", "spaceport");
        var (sim, port) = (yard.Sim, yard.Buildings[1]);
        var (price, materials) = Cost("spaceport", 2);
        Assert.Equal(450, price);
        Assert.Equal(new Dictionary<string, double> { ["blocks"] = 30, ["parts"] = 8 }, materials);
        yard.Stash(("blocks", 45));
        // Заказы без строителя в списке: свободный рядом берётся за улучшение сам.
        Upgrade(sim, port);
        Until(sim, () => sim.LevelOf(port) == 2);
        var hold = Tables.Buildings["spaceport"].Inventory!.Value;
        Assert.Equal(hold * 1.5, sim.Num(port, "Stock.capacity"));
        // Блоков на третий уровень хватает, компонентов нет.
        Assert.Equal("materials", sim.UpgradeLack(1, port));
        yard.Stash(("parts", 8));
        Upgrade(sim, port);
        Until(sim, () => sim.LevelOf(port) == BuildRules.MaxLevel);
        Assert.Equal(0, Stocked(sim, "parts"));
        Assert.Equal(hold * 2, sim.Num(port, "Stock.capacity"));
        Assert.False(sim.Upgradable(1, port));
        Upgrade(sim, port);
        sim.Advance();
        Assert.False(Upgrading(sim, port));

        sim.Send(1, new BuyOrder { Port = port, Resource = "metal", Amount = 10 });
        sim.Advance();
        Assert.Equal(Math.Round(TradeRules.BuySeconds / 2 / Tick), sim.TradeOf(port)!.Total);
    }

    [Fact(DisplayName = "уровень ускоряет переработку и добычу и расширяет хранилище")]
    public void LevelSpeedsRefiningMiningAndStorage()
    {
        using var yard = Make("generator", "smelter");
        var (sim, smelter, builder) = (yard.Sim, yard.Buildings[1], yard.Builder);
        // Переработка второго уровня берёт руду в полтора раза быстрее.
        sim.Set(smelter, "Building.level", 2);
        sim.Set(smelter, "Stock.metalOre", 20);
        sim.Seconds(2);
        // REFINE_RATE — 1,5 руды в секунду.
        Close(20 - 1.5 * BuildRules.LevelFactor(2) * 2, sim.Stock(smelter, "metalOre"), 5);

        // Хранилище поднимают по-настоящему: ёмкость растёт, груз на месте.
        var store = yard.Store("metal");
        yard.Stash(("blocks", 10), ("metal", 100));
        Upgrade(sim, store, builder);
        Until(sim, () => sim.LevelOf(store) == 2);
        Assert.Equal(Tables.Buildings["metalYard"].Inventory!.Value * 1.5, sim.Num(store, "Stock.capacity"));
        Assert.True(sim.Stock(store, "metal") >= 100);

        // Шахта: добыча с месторождения — по уровню.
        static double Mined(int level)
        {
            using var world = Create(Rules);
            world.SetCredits(1, 0);
            for (var cellY = -5; cellY < 5; cellY++)
                for (var cellX = -5; cellX < 5; cellX++)
                {
                    if (world.DepositIn(cellX, cellY) is not { Kind: "metal" } spot || !Scene.CanPlace(world, "mine", spot.X, spot.Y))
                        continue;
                    var mine = world.PutBuilding("mine", spot.X, spot.Y, 1);
                    world.Set(mine, "Building.level", level);
                    world.Seconds(10);
                    return world.Stock(mine, "metalOre");
                }
            throw new InvalidOperationException("В мире не нашлось месторождения");
        }
        var rate = Tables.All.Deposits["metal"].Rate;
        Close(rate * 10, Mined(1), 5);
        Close(rate * 2 * 10, Mined(3), 5);
    }
}
