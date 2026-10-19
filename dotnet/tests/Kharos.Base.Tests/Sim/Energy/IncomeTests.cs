using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Base.Shared.Rules;
using Kharos.Base.Tests.Kernel;
using static Kharos.Base.Tests.Energy.Grid;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Energy;

/// <summary>Доход и хозяйства энергосетей (packages/tests/income.test.ts).</summary>
public sealed class IncomeTests
{
    /// <summary>Стартовые кредиты (STARTING_CREDITS).</summary>
    private const double StartingCredits = 1500;

    private static double Reward(string key) => Tables.All.Rewards[key].Credits;

    private static double IncomeOf(string type) => Tables.Buildings[type].Income ?? 0;

    /// <summary>Хозяйство кортежем — для сравнения целиком (toEqual).</summary>
    private static (double Produced, double Demand, double Peak, double Spare, double Income, double Crowd, double Stored) All(EconomyInfo e) =>
        (e.Produced, e.Demand, e.Peak, e.Spare, e.Income, e.Crowd, e.Stored);

    private static void Close(double expected, double actual, int digits = 2) =>
        Assert.True(Math.Abs(expected - actual) < Math.Pow(10, -digits) / 2, $"ждали {expected}, вышло {actual}");

    private static uint Mcv(Sim sim) => Scene.UnitsOf(sim, "mcv")[0];

    private static void Destroy(Sim sim, IEnumerable<uint> entities) => sim.Erase([.. entities]);

    private static double Health(Sim sim, uint entity) => sim.Num(entity, "Health.value");

    [Fact(DisplayName = "развёрнутое главное здание приносит награду и понемногу кредиты")]
    public void DeployedCoreEarns()
    {
        var (sim, _, _) = Start();
        using var _sim = sim;
        Assert.Equal(["deploy"], Scene.Rewards(sim, 1));
        Assert.Equal(StartingCredits + Reward("deploy"), sim.Credits(1));
        Assert.Equal((0.0, 0.0, 0.0, 0.0, IncomeOf("command"), 0.0, 0.0), All(sim.Economy(1)));

        sim.Seconds(50);
        Assert.Equal(StartingCredits + Reward("deploy") + 10, sim.Credits(1));
    }

    [Fact(DisplayName = "награда выдаётся один раз: свернуть и развернуть заново ничего не даёт")]
    public void RewardOnce()
    {
        var (sim, _, _) = Start();
        using var _sim = sim;
        sim.Send(1, new PackOrder { Building = Core(sim) });
        sim.Seconds(10.1);
        Assert.Equal(0, sim.Economy(1).Income);
        var credits = sim.Credits(1);
        sim.Send(1, new DeployOrder { Unit = Mcv(sim) });
        sim.Seconds(3.1);
        Assert.True(sim.Has(Core(sim), Names.Building.Name));
        Assert.Equal(credits, sim.Credits(1));
        Assert.Equal(["deploy"], Scene.Rewards(sim, 1));
    }

    [Fact(DisplayName = "генератор материи даёт кредиты за энергию, а при её нехватке — меньше")]
    public void MatterNeedsPower()
    {
        var (sim, x, y) = Start();
        using var _sim = sim;
        Put(sim, "matter", x + 6, y);
        // Без электростанции генератор материи стоит.
        Assert.Equal((0.0, 3.0, 3.0, 0.0, 0.2, 1.0, 0.0), All(sim.Economy(1)));

        Put(sim, "generator", x + 6, y + 4);
        Assert.Equal((10.0, 3.0, 3.0, 0.0, 1.7, 1.0, 0.0), All(sim.Economy(1)));
        var credits = sim.Credits(1);
        sim.Seconds(10);
        Assert.Equal(credits + 17, sim.Credits(1));
    }

    [Fact(DisplayName = "каждый следующий генератор материи в зоне просит больше энергии")]
    public void CrowdingGrows()
    {
        var (sim, x, y) = Start();
        using var _sim = sim;
        Put(sim, "generator", x + 6, y + 4);
        Put(sim, "matter", x + 6, y);
        // Второму генератору тесно: он просит две нормы вместо одной.
        Assert.Equal(-6, PowerRules.PowerOf(Tables.Buildings["matter"], sim.Economy(1).Crowd));
        Assert.Equal(10, PowerRules.PowerOf(Tables.Buildings["generator"], sim.Economy(1).Crowd));

        // Два просят 3 + 6 = 9 энергии — одной электростанции пока хватает.
        Put(sim, "matter", x + 9, y);
        Assert.Equal(9, sim.Economy(1).Demand);
        Close(0.2 + 2 * 1.5, sim.Economy(1).Income);

        // Третий просит ещё 9: 18 при 10, и общий доход от него только падает.
        Put(sim, "matter", x + 9, y + 4);
        Assert.Equal(18, sim.Economy(1).Demand);
        Close(0.2 + 3 * 1.5 * (10.0 / 18), sim.Economy(1).Income);
    }

    [Fact(DisplayName = "здания держат зону и работают и без главного здания, но денег без него не приносят; оторванное от базы — без энергии")]
    public void NoCoreNoMoney()
    {
        var (sim, x, y) = Start();
        using var _sim = sim;
        Put(sim, "generator", x + 6, y);
        Put(sim, "matter", x + 6, y + 3);
        // Далёкий генератор материи — своя зона без электростанции: энергии ему нет, и дохода он не даёт.
        var (fx, fy) = Away(sim, x + 60, y, 2, 2);
        Put(sim, "matter", fx, fy);
        Assert.Equal((10.0, 6.0, 6.0, 0.0, 1.7, 2.0, 0.0), All(sim.Economy(1)));

        sim.Send(1, new PackOrder { Building = Core(sim) });
        sim.Seconds(10.1);
        // Свёрнуто главное здание: энергия та же, а дохода нет ни с него, ни с генераторов материи.
        Assert.Equal((10.0, 6.0, 6.0, 0.0, 0.0, 2.0, 0.0), All(sim.Economy(1)));
    }

    [Fact(DisplayName = "готовые здания расширяют зону строительства по цепочке, стройки — нет")]
    public void ReadyBuildingsExpandZone()
    {
        var (sim, x, y) = Start();
        using var _sim = sim;
        // Главное здание стоит у (x + 3.5, y + 3.5): радиус 12 кончается около x + 15.
        var far = x + 17;
        Assert.Single(sim.Zones(1));
        Assert.True(Scene.CanPlace(sim, "khariteVault", far, y + 3));
        Assert.False(sim.CanBuild(1, "khariteVault", far, y + 3));

        // Площадка у края зоны её не расширяет, готовое здание — расширяет.
        sim.Send(1, new BuildOrder { Building = "khariteVault", X = x + 13, Y = y + 3 });
        sim.Advance();
        Assert.False(sim.CanBuild(1, "khariteVault", far, y + 3));
        Put(sim, "khariteVault", x + 13, y + 5);
        Assert.Single(sim.Zones(1));
        Assert.True(sim.CanBuild(1, "khariteVault", far, y + 3));

        // Здание без трубы — своя, отдельная сеть; протянут трубу — сети сольются.
        Scene.Place(sim, "khariteVault", x + 22, y + 5, 1);
        Assert.Equal(2, sim.Zones(1).Length);
        ConnectAll(sim, 1);
        Assert.Single(sim.Zones(1));

        // Без главного здания зона остаётся: её держат сами здания и трубы.
        sim.Send(1, new PackOrder { Building = Core(sim) });
        sim.Seconds(10.1);
        Assert.NotEmpty(sim.Zones(1).SelectMany(zone => zone.Circles));
        Assert.True(sim.CanBuild(1, "khariteVault", far, y + 3));
    }

    [Fact(DisplayName = "здание разбирают строители в полтора раза быстрее стройки; половина цены возвращается в конце")]
    public void DemolishRefundsHalf()
    {
        var (sim, x, y) = Start();
        using var _sim = sim;
        var plant = Put(sim, "generator", x + 6, y + 6);
        var link = Put(sim, "khariteVault", x + 13, y + 5);
        var core = Core(sim);
        // Строители — ближний к станции первым: в TS запрос мира отдаёт их в таком порядке, и остаётся ближний.
        var builders = Scene.UnitsOf(sim, "builder").OrderBy(unit => Sim.Distance(sim.At(unit), (x + 7, y + 7))).ToList();
        Assert.Equal(150, Cost("generator") * BuildRules.DemolishRefund);
        Assert.True(sim.CanBuild(1, "khariteVault", x + 17, y + 3));

        // Чужое, главное и недостроенное под разбор не идут.
        sim.Send(1, new BuildOrder { Building = "khariteVault", X = x + 9, Y = y + 9 });
        sim.Advance();
        var site = sim.SiteAt(x + 9, y + 9);
        sim.Send(2, new DemolishOrder { Building = plant });
        sim.Send(1, new DemolishOrder { Building = core });
        sim.Send(1, new DemolishOrder { Building = site });
        sim.Advance();
        Assert.False(sim.Has(plant, Names.Site.Name) || sim.Has(core, Names.Site.Name));
        Assert.Equal(0, sim.Num(site, "Site.demolish"));

        // Пока строитель не подошёл, здание стоит целым, но уже не работает и зону не расширяет.
        sim.Erase(builders[1]);
        sim.Send(1, new DemolishOrder { Building = plant });
        sim.Send(1, new DemolishOrder { Building = link });
        sim.Advance();
        Assert.Equal("generator", TypeOf(sim, plant));
        Assert.Equal((300.0, 1.0, 0.0), (sim.Num(plant, "Site.progress"), sim.Num(plant, "Site.demolish"), sim.Num(plant, "Site.undo")));
        Assert.Equal(0, sim.Economy(1).Produced);
        Assert.False(sim.CanBuild(1, "khariteVault", x + 17, y + 3));

        // Отмена разбора возвращает здание в строй, денег при этом не даёт.
        var credits = sim.Credits(1);
        sim.Send(1, new CancelBuildOrder { Site = link });
        sim.Advance();
        Assert.False(sim.Has(link, Names.Site.Name));
        Assert.True(sim.CanBuild(1, "khariteVault", x + 17, y + 3));
        Assert.True(sim.Credits(1) - credits < 2);

        // Свободный строитель рядом берётся за разбор сам. Один разбирает электростанцию за 15 / 1,5 = 10 секунд, не считая дороги.
        var ticks = 0;
        var working = 0;
        while (sim.Alive(plant) && ticks++ < 1200)
        {
            if (sim.Num(plant, "Site.progress") < 300)
                working++;
            sim.Advance();
        }
        Assert.False(sim.Alive(plant));
        Assert.Equal(199, working);
        Assert.Equal(0u, sim.OccupantAt(x + 6, y + 6));
        // Строитель освобождается на следующем тике.
        sim.Advance();
        Assert.False(sim.Has(builders[0], Names.Builds.Name));
        var gained = sim.Credits(1) - credits;
        Assert.True(gained >= 150, $"{gained}");
        Assert.True(gained < 150 + 20, $"{gained}");
    }

    [Fact(DisplayName = "энергия у каждой зоны своя: электростанция одной зоны не питает другую")]
    public void PowerPerZone()
    {
        var (sim, x, y) = Start();
        using var _sim = sim;
        Put(sim, "generator", x + 6, y);
        Put(sim, "generator", x + 6, y + 3);
        Put(sim, "matter", x + 9, y);
        Assert.Equal([(20.0, 3.0, 3.0, 0.0, 1.7, 1.0, 0.0)], sim.ZoneEconomies(1).Select(All));

        // Второе главное здание далеко от первого — вторая зона. Её генератор материи без своей электростанции стоит.
        // Трубы там кладутся руками; местность .NET другая — место ищется на скале (TS: x + 300).
        var (far, fy) = Away(sim, x + 300, y, 6, 6);
        Scene.Place(sim, "command", far, fy, 1);
        Scene.Place(sim, "matter", far + 4, fy, 1);
        Scene.Place(sim, "pipe", far + 3, fy, 1);
        Assert.Equal(2, sim.Zones(1).Length);
        Assert.Equal((0.0, 3.0, 3.0, 0.0, 0.2, 1.0, 0.0), All(sim.ZoneEconomies(1)[1]));
        // Теснота тоже считается по зонам: в каждой генератор материи первый.
        Close(1.9, sim.Economy(1).Income);

        Scene.Place(sim, "generator", far + 4, fy + 3, 1);
        Scene.Place(sim, "pipe", far + 4, fy + 2, 1);
        Close(1.7, sim.ZoneEconomies(1)[1].Income);

        // Второе главное здание, подключённое трубой к первой базе, — та же сеть.
        Put(sim, "command", x + 10, y + 6);
        Assert.Equal(2, sim.Zones(1).Length);
    }

    [Fact(DisplayName = "в чужой зоне не строят и не разворачиваются")]
    public void ForeignZone()
    {
        var (sim, x, y) = Start();
        using var _sim = sim;
        // Главное здание игрока 2 — справа, зоны перекрываются между x + 11 и x + 15.
        Scene.Place(sim, "command", x + 22, y + 2, 2);
        Assert.True(sim.CanBuild(1, "khariteVault", x + 6, y + 2));
        // В перекрытии зон не строит ни один.
        Assert.False(sim.CanBuild(1, "khariteVault", x + 12, y + 3));
        Assert.False(sim.CanBuild(2, "khariteVault", x + 12, y + 3));
        Assert.True(sim.CanBuild(2, "khariteVault", x + 18, y + 3));

        Scene.Spawn(sim, "mcv", 2, x + 8, y + 8);
        Scene.Spawn(sim, "mcv", 2, x + 20, y + 9);
        sim.Advance();
        var mcvs = Scene.UnitsOf(sim, "mcv").Where(mcv => sim.OwnerOf(mcv) == 2).ToList();
        Assert.Equal([false, true], mcvs.Select(mcv => Scene.CanDeploy(sim, 2, mcv)));
    }

    [Fact(DisplayName = "перегруженная электростанция теряет прочность и разрушается; сама не восстанавливается")]
    public void OverloadWearsPlant()
    {
        var (sim, x, y) = Start();
        using var _sim = sim;
        // Строители починили бы станцию сами; здесь проверяется, что без них она не восстанавливается.
        Destroy(sim, Scene.UnitsOf(sim, "builder"));
        var plant = Put(sim, "generator", x + 6, y + 4);
        var first = Put(sim, "matter", x + 6, y);
        // Энергии хватает: никто не страдает.
        sim.Seconds(5);
        Assert.Equal(1, Health(sim, plant));
        Assert.Empty(sim.PowerStates());

        // Просят 18 при 10: перегруз 0.8, станция под ударом, потребители замедлены.
        var second = Put(sim, "matter", x + 9, y);
        var third = Put(sim, "matter", x + 9, y + 4);
        Assert.Equal(new Dictionary<uint, string> { [plant] = "overload", [first] = "starved", [second] = "starved", [third] = "starved" }, sim.PowerStates());
        sim.Advance();
        Close(1 - PowerRules.OverloadDamage * 0.8 * Sim.Dt, Health(sim, plant), 10);
        // Повреждённая станция даёт меньше энергии, поэтому перегруз растёт сам и урон ускоряется.
        sim.Seconds(20);
        Assert.True(Health(sim, plant) < 1 - PowerRules.OverloadDamage * 0.8 * 20);
        Close(10 * Health(sim, plant), sim.Economy(1).Produced);

        // Перегруз сняли — урон прекратился, но сама станция не чинится.
        // Снимать надо весь перегруз: битая станция даёт меньше десяти, и двух генераторов материи ей уже много.
        sim.Erase(second, third);
        var damaged = Health(sim, plant);
        sim.Seconds(4);
        Assert.Equal(damaged, Health(sim, plant));
        Assert.Empty(sim.PowerStates());

        // Сильный перегруз доводит станцию до разрушения; дальше потребители стоят без энергии.
        Put(sim, "matter", x + 9, y);
        Put(sim, "matter", x + 9, y + 4);
        Put(sim, "matter", x + 12, y);
        sim.Seconds(1 / PowerRules.OverloadDamage + 5);
        Assert.False(sim.Alive(plant) && sim.Has(plant, Names.Building.Name));
        Assert.Equal(0, sim.Economy(1).Produced);
        Close(0.2, sim.Economy(1).Income);
    }

    [Fact(DisplayName = "строители чинят повреждённое здание — по приказу и сами; перегруженную станцию — только по приказу")]
    public void BuildersRepair()
    {
        var (sim, x, y) = Start();
        using var _sim = sim;
        var plant = Put(sim, "generator", x + 6, y + 4);
        Put(sim, "matter", x + 6, y);
        var builders = Scene.UnitsOf(sim, "builder");
        var rules = new global::Base.Shared.Data.RulesSpec();

        // Целое здание работой не считается.
        sim.Send(1, new AssistOrder { Units = [.. builders], Site = plant });
        sim.Advance();
        Assert.False(sim.Has(builders[0], Names.Builds.Name));

        // Свободные строители рядом сами берутся за починку. Она идёт вдвое быстрее стройки и стоит половину цены за целое здание.
        sim.Set(plant, "Health.value", 0.2);
        var credits = sim.Credits(1);
        var repairTime = 5 + 0.8 * (Cost("generator") / 20) / rules.RepairSpeed;
        sim.Seconds(1);
        Assert.True(sim.Has(builders[0], Names.Builds.Name));
        sim.Seconds(repairTime - 1);
        Close(1, Health(sim, plant));
        // Починка 80% станции стоила 120. Доход за это время — около 1,2 в секунду: чуть меньше, пока битая станция
        // недодавала энергии, — отсюда допуск.
        var paid = credits + 1.2 * repairTime - sim.Credits(1);
        Assert.True(Math.Abs(paid - 0.8 * Cost("generator") * rules.RepairCost) < 15, $"{paid}");

        // Денег нет — починка идёт только на то, что капает доходом зоны (2,5 в секунду: без денег главное здание даёт
        // подмогу), а секунда полной работы стоит 20 кредитов: за десять секунд наберётся восьмая часть работы, не больше.
        sim.Set(plant, "Health.value", 0.5);
        Construction.Yard.Wallet(sim, 1, 0);
        sim.Seconds(10);
        Assert.True(Health(sim, plant) > 0.5);
        Assert.True(Health(sim, plant) < 0.7);
        sim.Set(plant, "Health.value", 1);
        sim.Advance();
        Assert.False(sim.Has(builders[0], Names.Builds.Name));

        // Перегруженную станцию строители сами не чинят: она только теряет прочность.
        sim.Set(sim.Wallet(1), "Player.credits", 500);
        Put(sim, "matter", x + 9, y);
        Put(sim, "matter", x + 9, y + 4);
        sim.Seconds(10);
        var worn = Health(sim, plant);
        Assert.True(worn < 1);
        Assert.False(sim.Has(builders[0], Names.Builds.Name));
        // По приказу — чинят, и починка обгоняет урон.
        sim.Send(1, new AssistOrder { Units = [.. builders], Site = plant });
        sim.Seconds(5);
        Assert.True(sim.Has(builders[0], Names.Builds.Name));
        Assert.True(Health(sim, plant) > worn);
    }

    [Fact(DisplayName = "игроку не в сети — пятая часть дохода и не больше потолка за отсутствие; вернулся — снова полный")]
    public void OfflineIncome()
    {
        var rules = new global::Base.Shared.Data.RulesSpec();
        var (sim, _, _) = Start();
        using var _sim = sim;
        var income = sim.Economy(1).Income;
        Assert.True(income > 0);
        Close(income * 20, Earned(sim, 20), 1);
        // Ушёл: доход — пятая часть.
        sim.SetOnline();
        Close(income * 20 * rules.OfflineIncome, Earned(sim, 20), 1);
        // Вернулся — полный доход, а потолок обнулён.
        sim.SetOnline(1);
        Close(income * 20, Earned(sim, 20), 1);
        Assert.Equal(0, sim.Num(sim.Wallet(1), "Player.away"));

        // Потолок — секунда обычного дохода. Правила партии на ходу не меняются (в TS — sim.rules.offlineMinutes = 1 / 60):
        // потолок проверяется на второй партии с этим правилом с начала.
        var (brief, _, _) = Start(new { techTree = false, offlineMinutes = 1.0 / 60 });
        using var _brief = brief;
        brief.SetOnline();
        Close(income, Earned(brief, 20), 1);
        // Набран — больше не капает.
        Close(0, Earned(brief, 20), 6);
        brief.SetOnline(1);
        Close(income * 20, Earned(brief, 20), 1);
        Assert.Equal(0, brief.Num(brief.Wallet(1), "Player.away"));
    }

    /// <summary>Сколько игрок 1 заработал за time секунд, с долями (earned).</summary>
    private static double Earned(Sim sim, double time)
    {
        double Total() => sim.Credits(1) + sim.Num(sim.Wallet(1), "Player.earned");
        var before = Total();
        sim.Seconds(time);
        return Total() - before;
    }

    [Fact(DisplayName = "выключенный потребитель энергии не просит её и не работает; электростанцию не выключить")]
    public void SwitchedOffConsumer()
    {
        var (sim, x, y) = Start();
        using var _sim = sim;
        var matter = Put(sim, "matter", x + 6, y);
        var radar = Put(sim, "radar", x + 9, y);
        var generator = Put(sim, "generator", x + 6, y + 4);
        Assert.Equal(6, sim.Economy(1).Demand);

        sim.Send(1, new WorkOrder { Building = matter, On = false });
        sim.Send(1, new WorkOrder { Building = generator, On = false });
        sim.Seconds(0.1);
        Assert.True(sim.Has(matter, Names.Off.Name));
        Assert.False(sim.Has(generator, Names.Off.Name));
        Assert.Equal((3.0, 0.2), (sim.Economy(1).Demand, sim.Economy(1).Income));
        Assert.False(sim.PowerSupply().ContainsKey(matter));
        Assert.Equal(1, sim.PowerSupply()[radar]);

        sim.Send(1, new WorkOrder { Building = matter, On = true });
        sim.Seconds(0.1);
        Assert.Equal((6.0, 1.7), (sim.Economy(1).Demand, sim.Economy(1).Income));
    }

    [Fact(DisplayName = "генератор материи приносит деньги, только если в его зоне есть главное здание")]
    public void MatterNeedsCore()
    {
        using var sim = Blank();
        Put(sim, "generator", 0, 0);
        var matter = Put(sim, "matter", 3, 0);
        Assert.Equal(0, sim.Economy(1).Income);
        Assert.Contains(matter, sim.Coreless());
        Put(sim, "command", 6, 0);
        // В TS игрока до этого нет, и addCredits заводит его с RELIEF_BELOW.
        sim.SetCredits(1, PowerRules.ReliefBelow);
        Close(IncomeOf("command") + IncomeOf("matter"), sim.Economy(1).Income);
        Assert.Empty(sim.Coreless());
    }

    [Fact(DisplayName = "подмога: пока кредитов мало, главное здание даёт больше — одно на игрока и только пока оно стоит")]
    public void Relief()
    {
        using var sim = Blank();
        // addCredits(sim, 1, 0) в TS заводит игрока без денег.
        sim.SetCredits(1, 0);
        // Без главного здания подмоги нет: спасать нечего.
        Assert.Equal(0, sim.Economy(1).Income);
        Put(sim, "command", 0, 0);
        Close(PowerRules.ReliefIncome, sim.Economy(1).Income);
        // Второе главное здание, в своей сети, подмогу не удваивает: оно даёт свой обычный доход.
        Put(sim, "command", 60, 60);
        Assert.Equal(2, sim.ZoneEconomies(1).Length);
        Close(PowerRules.ReliefIncome + IncomeOf("command"), sim.Economy(1).Income);
        // Накопил — доход прежний; потратил — подмога вернулась.
        sim.Seconds(5);
        Assert.Equal(Math.Floor(5 * (PowerRules.ReliefIncome + IncomeOf("command"))), sim.Credits(1));
        sim.SetCredits(1, sim.Credits(1) + PowerRules.ReliefBelow);
        Close(IncomeOf("command") * 2, sim.Economy(1).Income);
        sim.SetCredits(1, sim.Credits(1) - PowerRules.ReliefBelow);
        Close(PowerRules.ReliefIncome + IncomeOf("command"), sim.Economy(1).Income);
    }

    [Fact(DisplayName = "радар разведывает вспышками в своём радиусе; уровень растит радиус и потребление, выключенный не разведывает", Skip = "ждёт С7: радар (scan)")]
    public void RadarScans()
    {
    }

    [Fact(DisplayName = "радар без энергии видит не дальше обычного здания, с энергией — снова далеко", Skip = "ждёт С7: радар (scan)")]
    public void RadarWithoutPower()
    {
    }

    [Fact(DisplayName = "потребитель без дела просит меньше: казармы без заказа — 1 вместо 2, с заказом — все 2, после него — снова 1")]
    public void IdleConsumerAsksLess()
    {
        var (sim, x, y) = Start();
        using var _sim = sim;
        Put(sim, "generator", x + 6, y + 4);
        var barracks = Put(sim, "barracks", x + 6, y);
        Assert.Equal(-1, PowerRules.IdlePower(-2));
        Assert.Equal(-2, PowerRules.IdlePower(-5));
        Assert.Equal(-4, PowerRules.IdlePower(-10));
        sim.Seconds(1);
        Assert.False(sim.Has(barracks, Names.Busy.Name));
        Assert.Equal((10.0, 1.0, 2.0), (sim.Economy(1).Produced, sim.Economy(1).Demand, sim.Economy(1).Peak));

        sim.Send(1, new ProduceOrder { Producer = barracks, Unit = "infantry" });
        sim.Seconds(1);
        Assert.True(sim.Has(barracks, Names.Busy.Name));
        Assert.Equal((2.0, 2.0), (sim.Economy(1).Demand, sim.Economy(1).Peak));
        // Заказ готов — казармы ещё пару секунд значатся занятыми, потом снова стоят.
        sim.Seconds(3);
        Assert.Empty(Scene.Queue(sim, barracks));
        sim.Seconds(3);
        Assert.False(sim.Has(barracks, Names.Busy.Name));
        Assert.Equal(1, sim.Economy(1).Demand);

        // Выключенные не просят ничего и занятыми не значатся, даже с заказом.
        sim.Send(1, new ProduceOrder { Producer = barracks, Unit = "infantry" });
        sim.Send(1, new WorkOrder { Building = barracks, On = false });
        sim.Seconds(1);
        Assert.False(sim.Has(barracks, Names.Busy.Name));
        Assert.Equal((0.0, 0.0), (sim.Economy(1).Demand, sim.Economy(1).Peak));
    }

    [Fact(DisplayName = "плавильня занята, пока есть руда и место под металл; генератор материи и радар заняты всегда")]
    public void SmelterBusyWithOre()
    {
        var (sim, x, y) = Start();
        using var _sim = sim;
        Put(sim, "generator", x + 6, y + 4);
        var smelter = Put(sim, "smelter", x + 6, y);
        var power = -Tables.Buildings["smelter"].Power!.Value;
        sim.Seconds(1);
        Assert.Equal((-PowerRules.IdlePower(-power), power), (sim.Economy(1).Demand, sim.Economy(1).Peak));
        sim.Set(smelter, "Stock.metalOre", 20);
        sim.Seconds(1);
        Assert.True(sim.Has(smelter, Names.Busy.Name));
        Assert.Equal(power, sim.Economy(1).Demand);

        Put(sim, "matter", x + 9, y);
        Put(sim, "radar", x + 9, y + 4);
        Assert.Equal((power + 6, power + 6), (sim.Economy(1).Demand, sim.Economy(1).Peak));
    }

    [Fact(DisplayName = "перегруз станций считается по полной нагрузке: простой бережёт энергию, но не станции; заряженный накопитель — бережёт")]
    public void OverloadByPeak()
    {
        var (sim, x, y) = Start();
        using var _sim = sim;
        // Строители чинили бы станцию.
        Destroy(sim, Scene.UnitsOf(sim, "builder"));
        var plant = Put(sim, "generator", x + 6, y + 4);
        // Три машинных завода: в работе 15 при выработке 10, без дела — 6.
        uint[] factories = [Put(sim, "factory", x + 6, y), Put(sim, "factory", x + 10, y), Put(sim, "factory", x + 14, y)];
        sim.Seconds(1);
        var economy = sim.ZoneEconomies(1)[0];
        Assert.Equal((6.0, 15.0), (economy.Demand, economy.Peak));
        // Перегруз — около половины; станция под ним слабеет, и он растёт.
        Close(0.5, PowerRules.OverloadOf(economy.Produced, economy.Peak, economy.Spare), 1);
        // Потребителям энергии хватает, а станция изнашивается.
        Assert.Equal(1, sim.PowerSupply()[factories[0]]);
        Assert.Equal("overload", sim.PowerStates()[plant]);
        Assert.False(sim.PowerStates().ContainsKey(factories[0]));
        Assert.True(Health(sim, plant) < 1);

        // Заряженный накопитель мог бы отдать недостающее: пока заряд есть, станция цела.
        var worn = Health(sim, plant);
        var cell = Put(sim, "accumulator", x + 10, y + 4);
        sim.Set(cell, "Accumulator.charge", 100);
        sim.Seconds(1);
        var after = sim.ZoneEconomies(1)[0];
        Assert.Equal(0, PowerRules.OverloadOf(after.Produced, after.Peak, after.Spare));
        Assert.False(sim.PowerStates().ContainsKey(plant));
        Assert.Equal(worn, Health(sim, plant));
    }

    [Fact(DisplayName = "под нагрузкой столько электростанций, сколько нужно на спрос зоны, — ближайшие к потребителям; остальные в запасе")]
    public void LoadedPlantsNearest()
    {
        var (sim, x, y) = Start();
        using var _sim = sim;
        var near = Put(sim, "generator", x + 6, y + 4);
        var far = Put(sim, "generator", x + 14, y + 4);
        // Потребителей нет — питать нечего.
        Assert.Empty(sim.LoadedPlants());
        // Радар у первой станции просит 3: хватает её одной.
        Put(sim, "radar", x + 6, y);
        Assert.Equal([near], sim.LoadedPlants());
        // Ещё три радара — 12: нужны обе.
        foreach (var dx in new[] { 9, 12, 15 })
            Put(sim, "radar", x + dx, y);
        Assert.Equal(new[] { near, far }.Order(), sim.LoadedPlants().Order());
        Assert.Equal((20.0, 12.0), (sim.Economy(1).Produced, sim.Economy(1).Demand));
    }

    /// <summary>Ближайший к (x, y) левый верхний тайл скалы w×h без подножий обрыва, где встанет здание.</summary>
    private static (int X, int Y) Away(Sim sim, int x, int y, int w, int h)
    {
        for (var radius = 0; radius < 200; radius++)
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == radius && Rock(sim, x + dx, y + dy, w, h) && Scene.CanPlace(sim, "command", x + dx, y + dy))
                        return (x + dx, y + dy);
        throw new InvalidOperationException("Не нашлось скалы");
    }
}
