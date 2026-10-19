using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Rules;
using Kharos.Base.Tests.Kernel;
using Kharos.Plugins.Game;
using static Kharos.Base.Tests.Researching.Lab;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Researching;

/// <summary>Исследования: темы и цены, лаборатории, план, открытия (packages/tests/research.test.ts).</summary>
public sealed class ResearchTests
{
    private static IReadOnlyDictionary<string, global::Base.Shared.Data.TopicSpec> Topics => Tables.All.Research;

    private static TopicPrice Price(string topic) => ResearchRules.Price(Topics, topic, Tables.All.Rules);

    [Fact(DisplayName = "темы ссылаются на существующие темы, а тратят то, что даёт открытое раньше")]
    public void TopicsReferenceExistingTopics()
    {
        foreach (var (topic, spec) in Topics)
            foreach (var before in spec.After)
                Assert.True(Topics.ContainsKey(before), $"{topic}: {before}");
        foreach (var (type, spec) in Tables.Buildings)
            if (spec.Research is { } topic)
                Assert.True(Topics.ContainsKey(topic), $"{type}: {topic}");
        foreach (var (type, spec) in Tables.Units)
            if (spec.Research is { } topic)
                Assert.True(Topics.ContainsKey(topic), $"{type}: {topic}");
        foreach (var (resource, spec) in Tables.All.Resources)
            if (spec.Research is { } topic)
                Assert.True(Topics.ContainsKey(topic), $"{resource}: {topic}");
        // Где игрок возьмёт то, что тратит тема: руду дают шахты всегда, ресурс — переработка его руды, изделие — его цех.
        string? Makes(string good) => Tables.All.Ores.ContainsKey(good) ? null : Tables.Buildings.FirstOrDefault(pair =>
            (pair.Value.Refines is { } ore && Tables.All.Ores.GetValueOrDefault(ore)?.Resource == good) || pair.Value.Assembles == good).Key;
        HashSet<string> Ancestors(string topic) => [.. ResearchRules.Before(Topics, topic).SelectMany(before => Ancestors(before).Append(before))];
        foreach (var (topic, spec) in Topics)
        {
            var known = Ancestors(topic);
            foreach (var good in spec.Uses.Keys)
            {
                if (Makes(good) is not { } maker)
                    continue;
                Assert.True(Tables.Buildings[maker].Research is { } opens && known.Contains(opens), $"{topic}: {good} ({maker})");
            }
        }
        // Каждое изделие и каждая переработка открываются исследованием.
        foreach (var product in Tables.All.Products.Keys)
            Assert.Contains(Tables.Buildings, pair => pair.Value.Research is not null && pair.Value.Assembles == product);
    }

    [Fact(DisplayName = "чем глубже тема, тем она дороже и дольше")]
    public void DeeperTopicsCostMore()
    {
        Assert.Equal(1, ResearchRules.Tier(Topics, "metalOre"));
        Assert.Equal(2, ResearchRules.Tier(Topics, "blocks"));
        Assert.Equal(6, ResearchRules.Tier(Topics, "warheads"));
        var ore = Price("metalOre");
        Assert.Equal((ResearchRules.TierCredits, ResearchRules.TierSeconds), (ore.Cost, ore.Seconds));
        Assert.Equal([("metalOre", 10.0)], ore.Materials);
        var growth = Tables.All.Rules.ResearchGrowth;
        var blocks = Price("blocks");
        Assert.Equal((BuildRules.Round(ResearchRules.TierCredits * growth), ResearchRules.TierSeconds * growth), (blocks.Cost, blocks.Seconds));
        Assert.Equal([("metal", BuildRules.Round(6 * growth)), ("silicon", BuildRules.Round(3 * growth))], blocks.Materials);
        foreach (var topic in Topics.Keys)
            foreach (var before in ResearchRules.Before(Topics, topic))
            {
                Assert.True(Price(topic).Cost > Price(before).Cost, $"{topic} дороже {before}");
                Assert.True(Price(topic).Seconds > Price(before).Seconds, $"{topic} дольше {before}");
            }
        // Партия считает так же.
        using var sim = Create();
        var priced = sim.TopicPrice("blocks");
        Assert.Equal((blocks.Cost, blocks.Seconds), (priced.Cost, priced.Seconds));
        Assert.Equal(blocks.Materials.ToDictionary(item => item.Good, item => item.Amount), priced.Materials);
    }

    /// <summary>Месторождение вида kind в ячейках у начала мира, рядом с которым fits ставит ряд; нет — исключение.</summary>
    private static (DepositSpot Spot, T Found) Deposit<T>(Sim sim, string kind, Func<DepositSpot, T?> fits) where T : class
    {
        for (var cellY = -6; cellY < 6; cellY++)
            for (var cellX = -6; cellX < 6; cellX++)
                if (sim.DepositIn(cellX, cellY) is { } spot && spot.Kind == kind && fits(spot) is { } found)
                    return (spot, found);
        throw new InvalidOperationException($"Не нашлось месторождения {kind} с местом рядом");
    }

    [Fact(DisplayName = "плавильню открывает исследование руды металла: лаборатория берёт руду из шахты своей сети и кредиты по ходу работы")]
    public void SmelterUnlockedByMetalOre()
    {
        using var sim = Create();
        sim.SetCredits(1, 1000);
        var (_, buildings) = Deposit(sim, "metal", spot => Row(sim, ["mine", "lab", "generator"], (spot.X, spot.Y)));
        var (mine, lab) = (buildings[0], buildings[1]);
        Assert.False(sim.Unlocked(1, "smelter"));
        Assert.True(sim.Unlocked(1, "lab"));

        // Плавку не поставить в план, пока не случились открытия: научный метод — достроенная лаборатория, металл найден —
        // его месторождение в обзоре (здесь на нём стоит шахта). Их не исследуют, они открываются сами, проверка раз в секунду.
        Plan(sim, "metalOre");
        Assert.Empty(sim.PlanOf(1));
        sim.Seconds(1);
        Assert.True(sim.HasResearch(1, "science"));
        Assert.True(sim.HasResearch(1, "prospecting"));
        Assert.True(sim.HasResearch(1, "metalFound"));
        Plan(sim, "metalOre");
        Assert.Equal(["metalOre"], sim.PlanOf(1));
        // Шахта ещё не накопала руды: лаборатория стоит и ничего не тратит.
        sim.Set(mine, "Stock.metalOre", 0);
        sim.Advance();
        Assert.Equal((0.0, "metalOre"), sim.LabOf(lab));
        sim.Set(mine, "Stock.metalOre", 0);
        var credits = sim.Credits(1);
        sim.Advance();
        Assert.Equal(credits, sim.Credits(1));

        // Шахта копает быстрее, чем лаборатория тратит, и тема идёт без остановок; кредитов уходит ровно её цена.
        var price = sim.TopicPrice("metalOre");
        var start = sim.Credits(1);
        for (var i = 0; i < Sim.TicksIn(price.Seconds + 1) && !sim.HasResearch(1, "metalOre"); i++)
            sim.Advance();
        Assert.True(sim.HasResearch(1, "metalOre"));
        Assert.InRange(start - sim.Credits(1), price.Cost - 1, price.Cost + 1);
        Assert.True(sim.Unlocked(1, "smelter"));
        Assert.Empty(sim.PlanOf(1));
        sim.Advance();
        Assert.Equal("idle", sim.LabOf(lab).Lack);
    }

    [Fact(DisplayName = "руду в лабораторию без труб до шахты везут грузовики, и она исследует плавку")]
    public void TrucksBringOreToLab()
    {
        using var sim = Create();
        sim.SetCredits(1, 1000);
        // Лаборатория со своей электростанцией — в стороне, трубы до шахты нет.
        var (spot, lab) = Deposit(sim, "metal", spot =>
            Row(sim, ["mine"], (spot.X, spot.Y)) is not null && Row(sim, ["generator", "lab"], (spot.X + 5, spot.Y)) is { } row ? row : null);
        Scene.Spawn(sim, "truck", 1, spot.X + 3, spot.Y + 3);
        sim.Learn(1, "science", "prospecting", "metalFound");
        Plan(sim, "metalOre");
        // Лаборатория заказала руду из шахты (заявка лаборатории на руду — из шахт): грузовик её везёт.
        sim.Advance();
        Assert.Contains(sim.Requests(1), request => request.To == lab[1] && request.Good == "metalOre");
        sim.Seconds(sim.TopicPrice("metalOre").Seconds + 40);
        Assert.True(sim.HasResearch(1, "metalOre"));
        // Лишнего лаборатория не набрала: запас — не больше, чем теме оставалось потратить.
        Assert.True(sim.Stock(lab[1], "metalOre") <= 10);
    }

    [Fact(DisplayName = "руду из шахты грузовики везут во все лаборатории, а не в одну")]
    public void TrucksBringOreToEveryLab()
    {
        using var sim = Create();
        sim.SetCredits(1, 1000);
        var (spot, row) = Deposit(sim, "metal", spot =>
            Row(sim, ["mine"], (spot.X, spot.Y)) is not null && Row(sim, ["generator", "lab", "lab"], (spot.X + 5, spot.Y)) is { } found ? found : null);
        var labs = row.Skip(1).ToList();
        Scene.Spawn(sim, "truck", 1, spot.X + 3, spot.Y + 3);
        Scene.Spawn(sim, "truck", 1, spot.X + 3, spot.Y + 4);
        sim.Learn(1, "science", "prospecting", "metalFound");
        Plan(sim, "metalOre");
        // Обе лаборатории в работе хоть раз: каждой привезли её долю руды.
        var worked = new HashSet<uint>();
        for (var i = 0; i < Sim.TicksIn(60) && !sim.HasResearch(1, "metalOre"); i++)
        {
            sim.Advance();
            foreach (var lab in labs)
                if (sim.LabOf(lab).Speed > 0)
                    worked.Add(lab);
        }
        Assert.True(sim.HasResearch(1, "metalOre"));
        Assert.Equal(2, worked.Count);
    }

    [Fact(DisplayName = "лаборатория тратит столько, сколько сделала; открытое остаётся у игрока и переживает сохранение")]
    public void LabSpendsWhatItDid()
    {
        using var @base = Make(["lab"]);
        var (sim, lab) = (@base.Sim, @base.Buildings[0]);
        sim.Learn(1, "metalOre", "siliconOre");
        var price = sim.TopicPrice("blocks");
        @base.Stash(("metal", 100), ("silicon", 100));
        var credits = sim.Credits(1);
        Plan(sim, "blocks");
        sim.Seconds(price.Seconds / 2);
        // Полпути — полцены.
        Close(0.5, sim.ProgressOf(1, "blocks"), 1);
        Assert.InRange(credits - sim.Credits(1), price.Cost * 0.45, price.Cost * 0.55);
        // Потрачено — сколько нет ни в хранилищах, ни в запасе лаборатории, ни в трубах.
        double Held(string good) => Stocked(sim, good) + sim.Stock(lab, good) + sim.Batches().Where(batch => batch.Resource == good).Sum(batch => batch.Amount);
        Close(price.Materials["metal"] * sim.ProgressOf(1, "blocks"), 100 - Held("metal"), 3);
        // Запас лаборатории — не больше, чем теме осталось потратить.
        Assert.True(sim.Stock(lab, "metal") <= price.Materials["metal"] * (1 - sim.ProgressOf(1, "blocks")) + 1);
        sim.Seconds(price.Seconds / 2 + 1);
        Assert.True(sim.HasResearch(1, "blocks"));
        Close(price.Materials["metal"], 100 - Held("metal"), 3);
        Close(price.Materials["silicon"], 100 - Held("silicon"), 3);
        // Остатки запаса трубы увозят обратно в хранилища.
        sim.Seconds(5);
        Assert.True(sim.Stock(lab, "metal") < 1e-6);
        Close(price.Materials["metal"], 100 - Stocked(sim, "metal"), 3);
        Assert.True(Math.Abs(credits - sim.Credits(1) - price.Cost) <= 1);
        // Чужому игроку тема не открыта.
        Assert.False(sim.HasResearch(2, "blocks"));

        using var copy = sim.ThroughSave();
        Assert.Contains("blocks", copy.ResearchOf(1));
        // Снесли лабораторию — открытое остаётся.
        sim.Erase(lab);
        Assert.True(sim.HasResearch(1, "blocks"));
    }

    [Fact(DisplayName = "несколько лабораторий складывают скорость там, где у них есть сырьё, и вместе тратят не больше цены")]
    public void LabsAddUpWhereSupplied()
    {
        using var @base = Make(["lab", "lab", "lab", "generator"]);
        var (sim, first, third) = (@base.Sim, @base.Buildings[0], @base.Buildings[2]);
        sim.Learn(1, "metalOre");
        // Третья лаборатория — в своей сети без хранилищ: металла у неё нет, и она стоит.
        var lonely = Row(sim, ["generator", "lab"])!;
        var price = sim.TopicPrice("ammo");
        @base.Stash(("metal", 100));
        Plan(sim, "ammo");
        sim.Seconds(price.Seconds / 6);
        Assert.Equal((1.0, ""), sim.LabOf(first));
        Assert.Equal((1.0, ""), sim.LabOf(third));
        Assert.Equal((0.0, "metal"), sim.LabOf(lonely[1]));
        // Три лаборатории — втрое быстрее одной.
        Close(0.5, sim.ProgressOf(1, "ammo"), 1);
        sim.Seconds(price.Seconds / 6 + 1);
        Assert.True(sim.HasResearch(1, "ammo"));
        // Доля каждой лаборатории округляется вверх: неистраченное трубы увозят обратно.
        sim.Seconds(5);
        Close(price.Materials["metal"], 100 - Stocked(sim, "metal"), 3);
    }

    [Fact(DisplayName = "очередь ведёт тему вместе с предшественниками; брошенная тема помнит прогресс; без энергии лаборатория стоит")]
    public void PlanCarriesPredecessors()
    {
        using var @base = Make(["lab"]);
        var (sim, lab) = (@base.Sim, @base.Buildings[0]);
        @base.Stash(("metal", 200), ("silicon", 200));
        sim.Learn(1, "science", "prospecting", "metalFound", "siliconFound");
        Plan(sim, "vehicles");
        Assert.Equal(["metalOre", "siliconOre", "blocks", "vehicles"], sim.PlanOf(1));
        // Руды в сети нет: лаборатория ждёт её на первой теме.
        sim.Advance();
        Assert.Equal("metalOre", sim.LabOf(lab).Lack);
        sim.Learn(1, "metalOre", "siliconOre");
        sim.Seconds(sim.TopicPrice("blocks").Seconds / 2);
        Assert.Equal(["blocks", "vehicles"], sim.PlanOf(1));

        sim.Send(1, new CancelResearchOrder());
        sim.Advance();
        var half = sim.ProgressOf(1, "blocks");
        Assert.True(half > 0.3);
        sim.Seconds(2);
        Assert.Empty(sim.PlanOf(1));
        Assert.Equal(half, sim.ProgressOf(1, "blocks"));
        Assert.Equal("idle", sim.LabOf(lab).Lack);
        // Выключенная лаборатория стоит: пока собирается очередь, работа не идёт.
        sim.Add(lab, Names.Off.Name);

        // Щелчок ставит в конец очереди, повтор ничего не меняет, Shift — в начало.
        Plan(sim, "ammo");
        Plan(sim, "blocks");
        Plan(sim, "blocks");
        Assert.Equal(["ammo", "blocks"], sim.PlanOf(1));
        Plan(sim, "vehicles", true);
        Assert.Equal(["blocks", "vehicles", "ammo"], sim.PlanOf(1));
        // Убрать из очереди — вместе с тем, чему тема нужна.
        sim.Send(1, new DequeueOrder { Topic = "blocks" });
        sim.Advance();
        Assert.Equal(["ammo"], sim.PlanOf(1));
        // Тему, которой не хватает неслучившегося открытия, в очередь не поставить.
        Plan(sim, "khariteOre");
        Assert.Equal(["ammo"], sim.PlanOf(1));

        Plan(sim, "blocks", true);
        sim.Seconds(2);
        Assert.Equal(half, sim.ProgressOf(1, "blocks"));
        // Стоит, потому что выключена, а не от нехватки энергии.
        Assert.Equal("off", sim.LabOf(lab).Lack);
        sim.Remove(lab, Names.Off.Name);
        // Без электростанций энергии нет вовсе.
        sim.Erase([.. @base.Plants]);
        sim.Seconds(2);
        Assert.Equal(half, sim.ProgressOf(1, "blocks"));
        Assert.Equal("power", sim.LabOf(lab).Lack);
    }

    [Fact(DisplayName = "план не встаёт на теме без сырья: лаборатории берутся за следующую и возвращаются, когда сырьё появится")]
    public void StalledHeadYieldsToSuppliedTopic()
    {
        using var @base = Make(["lab"]);
        var (sim, lab) = (@base.Sim, @base.Buildings[0]);
        sim.Learn(1, "metalOre", "fuelOre", "infantry");
        @base.Stash(("metal", 100));
        Plan(sim, "rockets");
        Plan(sim, "ammo");
        Assert.Equal(["rockets", "ammo"], sim.PlanOf(1));
        // Топлива нет нигде. Сразу план не двигается: грузовикам и трубам нужно время привезти.
        sim.Seconds(ResearchRules.StallSeconds - 2);
        Assert.Equal("rockets", sim.CurrentTopic(1));
        Assert.Equal((0.0, "fuel"), sim.LabOf(lab));
        Assert.Equal(0, sim.ProgressOf(1, "ammo"));
        // Простояла — лаборатория ведёт следующую тему, у которой сырьё есть. План при этом тот же.
        sim.Seconds(4);
        Assert.Equal("ammo", sim.CurrentTopic(1));
        Assert.Equal((1.0, ""), sim.LabOf(lab));
        Assert.True(sim.ProgressOf(1, "ammo") > 0);
        Assert.Equal(["rockets", "ammo"], sim.PlanOf(1));
        // Сырьё головной темы появилось — лаборатория возвращается к ней, а начатое в брошенной теме помнится.
        @base.Stash(("fuel", 100));
        sim.Seconds(2);
        Assert.Equal("rockets", sim.CurrentTopic(1));
        var paused = sim.ProgressOf(1, "ammo");
        Assert.True(paused > 0);
        sim.Seconds(3);
        Assert.True(sim.ProgressOf(1, "rockets") > 0);
        Assert.Equal(paused, sim.ProgressOf(1, "ammo"));
        // Обе доводятся до конца, по очереди плана.
        sim.Seconds(sim.TopicPrice("rockets").Seconds + sim.TopicPrice("ammo").Seconds + 5);
        Assert.True(sim.HasResearch(1, "rockets"));
        Assert.True(sim.HasResearch(1, "ammo"));
        Assert.Empty(sim.PlanOf(1));
    }

    [Fact(DisplayName = "без сырья у всех тем плана лаборатории ждут головную, а нехватка кредитов план не двигает")]
    public void NoSupplyWaitsForHead()
    {
        using var @base = Make(["lab"]);
        var (sim, lab) = (@base.Sim, @base.Buildings[0]);
        sim.Learn(1, "metalOre", "fuelOre", "infantry");
        Plan(sim, "rockets");
        Plan(sim, "ammo");
        // Ни топлива, ни металла: вести нечего, ждут первую.
        sim.Seconds(ResearchRules.StallSeconds + 3);
        Assert.Equal("rockets", sim.CurrentTopic(1));
        Assert.Equal((0.0, "fuel"), sim.LabOf(lab));
        // Сырьё есть, кредитов нет: следующая тема стоила бы тех же кредитов, и план стоит на первой.
        @base.Stash(("fuel", 100), ("metal", 100));
        SetPurse(sim, 1, 0, 0);
        sim.Seconds(ResearchRules.StallSeconds + 3);
        Assert.Equal("rockets", sim.CurrentTopic(1));
        Assert.Equal("credits", sim.LabOf(lab).Lack);
        Assert.Equal(0, sim.ProgressOf(1, "ammo"));
    }

    [Fact(DisplayName = "открытия случаются сами: шахта на месторождении — его руда найдена, переработанная руда — космопорт")]
    public void TriggersHappenThemselves()
    {
        using var @base = Make(["lab", "smelter"]);
        var sim = @base.Sim;
        Assert.False(sim.Unlocked(1, "spaceport"));
        // Месторождение кремния вне обзора базы (в TS — первое попавшееся: там оно и так вдали).
        var vision = sim.Host.Vision!;
        var (spot, _) = Deposit(sim, "silicon", spot =>
            Scene.CanPlace(sim, "mine", spot.X, spot.Y) && !vision.Sees(1, spot.X + 0.5, spot.Y + 0.5) ? spot : null);
        sim.Seconds(1.1);
        Assert.False(sim.HasResearch(1, "siliconFound"));
        // Переработку кремния не поставить в план, пока его руда не найдена.
        Plan(sim, "siliconOre");
        Assert.Empty(sim.PlanOf(1));
        sim.PutBuilding("mine", spot.X, spot.Y, 1);
        sim.Seconds(1.1);
        Assert.True(sim.HasResearch(1, "siliconFound"));
        Plan(sim, "siliconOre");
        Assert.Equal(["siliconOre"], sim.PlanOf(1));
        Assert.False(sim.HasResearch(1, "trade"));
        @base.Stash(("metal", 5));
        sim.Seconds(1.1);
        Assert.True(sim.Unlocked(1, "spaceport"));
    }

    [Fact(DisplayName = "темы не зовут раньше, чем от них толк: трубы — после металла, пехота — после патронов, накопитель — после лития")]
    public void TopicsComeWhenUseful()
    {
        Assert.Contains("metalOre", ResearchRules.Under(Topics, "pipes"));
        Assert.Contains("ammo", ResearchRules.Under(Topics, "infantry"));
        Assert.Contains("lithiumOre", ResearchRules.Under(Topics, "accumulators"));
        foreach (var ore in new[] { "metalOre", "siliconOre", "fuelOre", "khariteOre", "lithiumOre", "uraniumOre" })
            Assert.Contains(ResearchRules.Under(Topics, ore), before => ResearchRules.IsTrigger(Topics, before) && before != "science");
    }

    [Fact(DisplayName = "кончились кредиты — лаборатория встаёт, не уходя в долг")]
    public void LabStopsWithoutCredits()
    {
        using var @base = Make(["lab"]);
        var (sim, lab) = (@base.Sim, @base.Buildings[0]);
        SetPurse(sim, 1, 10, 0);
        @base.Stash(("metal", 100));
        sim.Learn(1, "metalOre");
        Plan(sim, "ammo");
        var price = sim.TopicPrice("ammo");
        sim.Seconds(price.Seconds);
        Assert.Equal(0, sim.Credits(1));
        Assert.Equal("credits", sim.LabOf(lab).Lack);
        Close(10 / price.Cost, sim.ProgressOf(1, "ammo"), 2);
        sim.SetCredits(1, 1000);
        sim.Seconds(price.Seconds);
        Assert.True(sim.HasResearch(1, "ammo"));
    }

    [Fact(DisplayName = "исследования открывают армию и уровни зданий")]
    public void ResearchUnlocksArmyAndLevels()
    {
        using var @base = Make(["lab"]);
        var (sim, plant) = (@base.Sim, @base.Plants[0]);
        PlaceAnywhere(sim, "radar");
        @base.Stash(("blocks", 100));
        Assert.False(sim.Unlocked(1, "barracks"));
        Assert.False(sim.Unlocked(1, "laserTurret"));
        Assert.Equal("research", sim.UpgradeLack(1, plant));
        sim.Learn(1, "infantry", "lasers");
        Assert.True(sim.Unlocked(1, "laserTurret"));
        Assert.False(sim.Unlocked(1, "arcTurret"));
        sim.Learn(1, "levels2");
        Assert.Null(sim.UpgradeLack(1, plant));
        sim.Send(1, new UpgradeOrder { Building = plant, Builders = [] });
        sim.Advance();
        Assert.True(Upgrading(sim, plant));
    }

    [Fact(DisplayName = "правила темпа множат цену и срок, а уровни зданий дорожают своим правилом")]
    public void PaceRulesScalePriceAndTime()
    {
        using var @base = Make(["generator", "lab"], new { techTree = false, researchScale = 2, researchSlow = 3, researchGrowth = 2, upgradeScale = 3 });
        var (sim, plant) = (@base.Sim, @base.Buildings[0]);
        var blocks = sim.TopicPrice("blocks");
        Assert.Equal((ResearchRules.TierCredits * 2 * 2, ResearchRules.TierSeconds * 2 * 3), (blocks.Cost, blocks.Seconds));
        Assert.Equal(new Dictionary<string, double> { ["metal"] = 6 * 2 * 2, ["silicon"] = 3 * 2 * 2 }, blocks.Materials);

        // Электростанция в матче — 150 кредитов и 10 стройблоков; здесь втрое.
        var generator = Tables.Buildings["generator"];
        var (cost, materials) = BuildRules.UpgradeCost(generator, 1, 3);
        Assert.Equal(450, cost);
        Assert.Equal(new Dictionary<string, double> { ["blocks"] = 30 }, materials);
        @base.Stash(("blocks", 100));
        var credits = sim.Credits(1);
        sim.Send(1, new UpgradeOrder { Building = plant, Builders = [] });
        sim.Advance();
        Assert.Equal(3, sim.Num(plant, "Upgrade.scale"));
        Assert.Equal(450, Math.Round(credits - sim.Credits(1)));
        Assert.Equal(70, Stocked(sim, "blocks"));
        Assert.Equal(BuildRules.UpgradeTicks(generator, 1, Tick) * 3, BuildRules.UpgradeTicks(generator, 1, Tick, 3));
        // В TS правила меняют на ходу (upgradeScale = 1) — отмена всё равно возвращает цену заказа; здесь правила партии
        // неизменны, и цену заказа помнит Upgrade.scale.
        sim.Send(1, new CancelUpgradeOrder { Building = plant });
        sim.Advance();
        Assert.False(Upgrading(sim, plant));
        Assert.Equal(0, Math.Round(sim.Credits(1) - credits));
        Assert.Equal(100, Stocked(sim, "blocks"));
    }

    [Fact(DisplayName = "старое сохранение: техцентр становится лабораторией, игроку засчитано то, что у него стоит",
        Skip = "устарело: сохранений TS версии 22 (техцентр) у .NET нет — adoptLabs не переносится")]
    public void OldSaveAdoptsLabs()
    {
    }

    /// <summary>Доля темы за тик одной лаборатории при полной энергии и сколько металла и кредитов она за неё тратит.</summary>
    private static (double Share, double Metal, double Cost) PerTick(Sim sim, string topic)
    {
        var price = sim.TopicPrice(topic);
        var share = Tick / price.Seconds;
        return (share, price.Materials["metal"] * share, price.Cost * share);
    }

    [Fact(DisplayName = "лаборатория берёт сырьё своей сети: готовое переработки и хранилищ, из хранилищ — раньше и сверх резерва; сырьё цеха и нужное другой лаборатории не трогает")]
    public void LabTakesFromItsNetwork()
    {
        using var @base = Make(["lab", "lab", "smelter", "blockPlant"]);
        var sim = @base.Sim;
        var (lab, other, smelter, plant) = (@base.Buildings[0], @base.Buildings[1], @base.Buildings[2], @base.Buildings[3]);
        sim.Learn(1, "metalOre");
        sim.Add(other, Names.Off.Name);
        sim.Set(plant, "Assembly.on", 1);
        var store = @base.Store("metal");
        Plan(sim, "ammo");
        // Кладёт металл, где сказано (остальным — ноль), и делает тик; отвечает, чего лаборатории не хватило.
        string Step(double store_ = 0, double reserve = 0, double smelter_ = 0, double plant_ = 0, double other_ = 0)
        {
            foreach (var (entity, amount) in new[] { (lab, 0.0), (store, store_), (smelter, smelter_), (plant, plant_), (other, other_) })
                Only(sim, entity, "metal", amount);
            sim.Set(store, "Stock.reserve", reserve);
            sim.Advance();
            return sim.LabOf(lab).Lack;
        }
        Assert.Equal("metal", Step(plant_: 50));
        Assert.Equal("metal", Step(other_: 50));
        Assert.Equal("metal", Step(store_: 50, reserve: 50));
        Assert.Equal("", Step(store_: 50, reserve: 49));
        Assert.Equal("", Step(smelter_: 50));
        Assert.Equal("", Step(store_: 50, smelter_: 50));
        Close(50 - PerTick(sim, "ammo").Metal, sim.Stock(store, "metal"), 10);
    }

    [Fact(DisplayName = "лаборатория делает ту долю работы, на какую хватило сырья и кредитов; две в одной сети делят сырьё; скорость — до сотых")]
    public void LabDoesWhatItCanPay()
    {
        using var @base = Make(["lab", "lab"]);
        var (sim, first, second) = (@base.Sim, @base.Buildings[0], @base.Buildings[1]);
        sim.Learn(1, "metalOre");
        var store = @base.Store("metal");
        Plan(sim, "ammo");
        var (share, metal, cost) = PerTick(sim, "ammo");
        double Step(double stock)
        {
            foreach (var lab in new[] { first, second })
                Only(sim, lab, "metal", 0);
            Only(sim, store, "metal", stock);
            var before = sim.ProgressOf(1, "ammo");
            sim.Advance();
            return sim.ProgressOf(1, "ammo") - before;
        }
        // Металла на одну лабораторию и треть второй: первая работает вся, вторая — на треть.
        Close(share * (4.0 / 3), Step(metal * (4.0 / 3)), 12);
        Assert.Equal((1.0, ""), sim.LabOf(first));
        Assert.Equal((0.33, "metal"), sim.LabOf(second));

        // Кредитов — на половину работы одной лаборатории.
        sim.Add(second, Names.Off.Name);
        SetPurse(sim, 1, 0, cost / 2);
        Close(share / 2, Step(100), 12);
        Assert.Equal((0.5, "credits"), sim.LabOf(first));
    }

    [Fact(DisplayName = "при нехватке энергии лаборатория работает медленнее и говорит, что ей не хватает энергии")]
    public void LabSlowsWithoutPower()
    {
        using var @base = Make(["lab", "lab", "lab", "lab"]);
        var (sim, lab) = (@base.Sim, @base.Buildings[0]);
        sim.Learn(1, "metalOre");
        sim.Erase(@base.Plants[1]);
        Only(sim, @base.Store("metal"), "metal", 100);
        Plan(sim, "ammo");
        sim.Seconds(1);
        // Четыре занятые лаборатории просят 12 при 10.
        Assert.Equal((0.83, "power"), sim.LabOf(lab));
    }

    [Fact(DisplayName = "правила темпа замедляют исследование и делают его дороже")]
    public void PaceRulesSlowResearch()
    {
        using var @base = Make(["lab"], new { researchScale = 2, researchSlow = 3 });
        var sim = @base.Sim;
        sim.Learn(1, "metalOre");
        Only(sim, @base.Store("metal"), "metal", 100);
        var price = sim.TopicPrice("ammo");
        var credits = sim.Credits(1);
        Plan(sim, "ammo");
        sim.Seconds(price.Seconds / 2);
        Close(0.5, sim.ProgressOf(1, "ammo"), 1);
        Assert.InRange(credits - sim.Credits(1), price.Cost * 0.45, price.Cost * 0.55);
    }

    [Fact(DisplayName = "исследования и уровни в CLR и на host.wasm дают тот же мир до байта")]
    public void ClrAndWasmAgree()
    {
        Assert.SkipWhen(Sim.Unavailable(ModMode.Wasm) is not null || Sim.Unavailable(ModMode.Clr) is not null, "нужны и Base.Host.dll, и host.wasm");
        Lab.Base Run(ModMode mode)
        {
            var @base = Make(["lab", "lab"], null, mode);
            var sim = @base.Sim;
            sim.Learn(1, "metalOre", "fuelOre", "infantry", "siliconOre", "levels2");
            @base.Stash(("metal", 100), ("silicon", 30), ("blocks", 40));
            Plan(sim, "rockets");
            Plan(sim, "blocks");
            sim.Send(1, new UpgradeOrder { Building = @base.Plants[0], Builders = [] });
            sim.Seconds(ResearchRules.StallSeconds + 5);
            @base.Stash(("fuel", 50));
            sim.Seconds(20);
            return @base;
        }
        using var clr = Run(ModMode.Clr);
        using var wasm = Run(ModMode.Wasm);
        Assert.True(clr.Sim.ProgressOf(1, "blocks") > 0);
        Assert.True(clr.Sim.SameWorld(wasm.Sim));
    }
}
