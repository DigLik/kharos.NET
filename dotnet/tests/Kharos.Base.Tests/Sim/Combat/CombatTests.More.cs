using System.Diagnostics;
using Base.Shared;
using Base.Shared.Orders;
using Kharos.Base.Tests.Kernel;
using static Kharos.Base.Tests.Combat.Arena;

namespace Kharos.Base.Tests.Combat;

// Бой: производство армии, показательный бой, починка, носители (packages/tests/combat.test.ts, часть 2).
public sealed partial class CombatTests
{
    /// <summary>Пехота сама поправляется на такую долю прочности в секунду (INFANTRY_REGEN).</summary>
    private const double InfantryRegen = 0.02;

    /// <summary>Пауза ремонта после попадания по умолчанию (REPAIR_PAUSE), секунды.</summary>
    private const double RepairPause = 3;

    /// <summary>Случайный ряд теста с зерном (Math.random в beforeEach combat.test.ts).</summary>
    private static Func<double> Seeded(long seed = 7) => () => (seed = seed * 16807 % 2147483647) / 2147483647.0;

    [Fact(DisplayName = "пехоту выпускают казармы, технику — машинный завод, летающих — аэродром, космопорт только торгует; всем им нужна энергия")]
    public void ProducersNeedPower()
    {
        using var sim = Create(new { techTree = false });
        sim.SetCredits(1, 10000);
        (string Type, int Dx)[] layout = [("command", 0), ("barracks", 4), ("factory", 7), ("airfield", 10), ("generator", 15), ("generator", 18), ("spaceport", 21)];
        (int X, int Y)? spot = null;
        for (var y = -80; y < 80 && spot is null; y++)
            for (var x = -80; x < 80 && spot is null; x++)
            {
                // Над рядом — труба: все здания в одной сети.
                if (!Enumerable.Range(0, 24).All(dx => Scene.Rock(sim, x + dx, y - 1, 1, 1)) || !layout.All(item => Scene.Rock(sim, x + item.Dx, y, (int)Kernel.Data.Buildings[item.Type].Width, (int)Kernel.Data.Buildings[item.Type].Height)))
                    continue;
                if (Enumerable.Range(0, 24).All(dx => Scene.CanPlace(sim, "pipe", x + dx, y - 1)) && layout.All(item => Scene.CanPlace(sim, item.Type, x + item.Dx, y)))
                    spot = (x, y);
            }
        var (sx, sy) = spot!.Value;
        var placed = layout.Select(item => Place(sim, item.Type, sx + item.Dx, sy, 1)).ToArray();
        var (barracks, factory, port, plant, spare, trader) = (placed[1], placed[2], placed[3], placed[4], placed[5], placed[6]);
        for (var dx = 0; dx < 24; dx++)
            Place(sim, "pipe", sx + dx, sy - 1, 1);
        Assert.Equal(["infantry", "rocketeer", "flamer"], Scene.Producible(sim, barracks));
        Assert.Equal(["buggy", "flak", "lancer", "supplier", "tank", "artillery", "tesla", "carrier", "mcv"], Scene.Producible(sim, factory));
        Assert.Equal(["airTruck", "drone", "gunship", "bomber"], Scene.Producible(sim, port));
        Assert.Empty(Scene.Producible(sim, trader));
        // В работе просят 2 + 5 + 4 + 5 из 20, а пока заказов нет — 1 + 2 + 2 + 2.
        var economy = sim.ZoneEconomies(1)[0];
        Assert.Equal((20, 16, 7), (economy.Produced, economy.Peak, economy.Demand));
        // Материалы на заказы уже на месте.
        void Materials(uint producer, string unit)
        {
            foreach (var (good, amount) in Unit(unit).Materials)
                sim.SetStock(producer, good, amount);
        }
        Materials(factory, "buggy");
        Materials(port, "drone");

        sim.Send(1, new ProduceOrder { Producer = barracks, Unit = "buggy" });
        sim.Send(1, new ProduceOrder { Producer = barracks, Unit = "rocketeer" });
        sim.Send(1, new ProduceOrder { Producer = factory, Unit = "buggy" });
        sim.Send(1, new ProduceOrder { Producer = port, Unit = "drone" });
        sim.Send(2, new ProduceOrder { Producer = port, Unit = "gunship" });
        sim.Advance();
        Assert.Equal(["rocketeer"], Scene.Queue(sim, barracks));
        Assert.Equal(["buggy"], Scene.Queue(sim, factory));
        Assert.Equal(["drone"], Scene.Queue(sim, port));
        sim.Seconds(Unit("buggy").BuildTime + 1);
        var made = sim.With(Names.Unit.Name).Select(entity => Scene.TypeOf(sim, entity)!).Order().ToArray();
        Assert.Equal(["buggy", "drone", "rocketeer"], made);

        // Осталась одна электростанция на 10. Занят один цех, остальные стоят и просят меньше: энергии хватает.
        sim.Erase(spare);
        sim.Seconds(3);
        sim.Send(1, new ProduceOrder { Producer = barracks, Unit = "infantry" });
        sim.Seconds(1);
        // Станция при этом изнашивается: перегруз считается по полной нагрузке.
        economy = sim.ZoneEconomies(1)[0];
        Assert.Equal((16, 8), (economy.Peak, economy.Demand));
        Assert.False(sim.PowerStates().ContainsKey(barracks));
        Assert.Equal("overload", sim.PowerStates()[plant]);
        sim.Seconds(Unit("infantry").BuildTime);
        Assert.Empty(Scene.Queue(sim, barracks));

        // Заняты все три: 2 + 5 + 4 + 2 = 13 из 10 — производство идёт на 10/13 скорости.
        Materials(factory, "buggy");
        Materials(port, "drone");
        sim.Send(1, new ProduceOrder { Producer = factory, Unit = "buggy" });
        sim.Send(1, new ProduceOrder { Producer = port, Unit = "drone" });
        sim.Seconds(1);
        sim.Send(1, new ProduceOrder { Producer = barracks, Unit = "infantry" });
        sim.Seconds(Unit("infantry").BuildTime + 0.1);
        economy = sim.ZoneEconomies(1)[0];
        Assert.Equal((16, 13), (economy.Peak, economy.Demand));
        Assert.Equal("starved", sim.PowerStates()[barracks]);
        Assert.Equal(["infantry"], Scene.Queue(sim, barracks));
        sim.Seconds(Unit("infantry").BuildTime * 0.5);
        Assert.Empty(Scene.Queue(sim, barracks));
        // Машинный завод и аэродром доделывают своё.
        sim.Seconds(20);
        Assert.Empty(Scene.Queue(sim, factory));

        // Без энергии совсем производство стоит.
        sim.Erase(plant);
        sim.Send(1, new ProduceOrder { Producer = barracks, Unit = "infantry" });
        sim.Seconds(10);
        Assert.Equal(["infantry"], Scene.Queue(sim, barracks));
        Assert.Equal(0, sim.Num(barracks, "Producer.progress"));

        // Здание под разбором не производит.
        sim.Send(1, new DemolishOrder { Building = factory, Builders = [] });
        sim.Send(1, new ProduceOrder { Producer = factory, Unit = "buggy" });
        sim.Advance(2);
        Assert.Empty(Scene.Producible(sim, factory));
        Assert.Empty(Scene.Queue(sim, factory));
    }

    /// <summary>Сколько сторон с юнитами.</summary>
    private static int Sides(Sim sim) => sim.With(Names.Unit.Name, Names.Owner.Name).Select(sim.OwnerOf).Distinct().Count();

    [Fact(DisplayName = "показательный бой: армии сходятся сами, и бой кончается")]
    public void ShowcaseEnds()
    {
        using var sim = Create();
        var army = Sim.RandomArmy(3500, null, () => 0.37);
        // Нулевой вес убирает тип из армии, а когда запрещены все — типы снова равноправны.
        var noTanks = new Dictionary<string, double>();
        foreach (var type in new[] { "infantry", "rocketeer", "flamer", "buggy", "flak", "lancer", "artillery", "tesla", "carrier", "drone", "gunship", "bomber" })
            noTanks[type] = 0;
        Assert.Equal(Enumerable.Repeat("tank", 5), Sim.RandomArmy(3500, noTanks, Seeded()));
        var none = new Dictionary<string, double>();
        foreach (var type in new[] { "infantry", "rocketeer", "flamer", "buggy", "flak", "lancer", "tank", "tesla", "drone", "gunship" })
            none[type] = 0;
        Assert.NotEmpty(Sim.RandomArmy(3500, none, Seeded()));
        Assert.True(army.Sum(type => Unit(type).Cost) > 3500 - 60);
        Assert.All(army, type => Assert.True(Sim.IsFighter(type)));
        sim.SpawnBattle(1, 0, 0, random: Seeded());
        Assert.Equal(2, Sides(sim));
        double time = 0;
        while (sim.DriveBattle(1) && time < 600)
        {
            sim.Seconds(0.5);
            time += 0.5;
        }
        Assert.True(time < 600);
        Assert.True(Sides(sim) < 2);
    }

    [Fact(DisplayName = "показательный бой кончается, когда ни одна сторона не может достать другую: бомбардировщики против бомбардировщиков")]
    public void ShowcaseEndsWhenNobodyReaches()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        Spawn(sim, "bomber", 1, x, y);
        Spawn(sim, "bomber", 2, x + 6, y);
        Assert.False(sim.DriveBattle(1));
        // Зенитка по земле не бьёт, но бомбардировщика достаёт: такой бой идёт.
        Spawn(sim, "flak", 2, x + 8, y + 2);
        Assert.True(sim.DriveBattle(1));
    }

    [Fact(DisplayName = "показательный бой не перебивает приказ идти, ни стрелку, ни носителю турелей")]
    public void ShowcaseKeepsMoveOrders()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var units = new[] { "infantry", "lancer", "buggy" }.Select((type, i) => Spawn(sim, type, 1, x + 8, y - 2 + i * 2)).ToArray();
        Spawn(sim, "infantry", 2, x + 12, y);
        Spawn(sim, "infantry", 2, x + 13, y + 1);
        sim.Seconds(1);
        sim.Send(1, new MoveOrder { Units = units, X = x, Y = y });
        sim.Advance();
        for (var i = 0; i < 8; i++)
        {
            sim.DriveBattle(1);
            sim.Seconds(0.25);
        }
        foreach (var entity in units)
            Assert.True(sim.At(entity).X < x + 6.5, $"{Scene.TypeOf(sim, entity)}: {sim.At(entity).X} ≥ {x + 6.5}");
    }

    /// <summary>Показательный бой: армии двух сторон по 60 тыс. кредитов; каждая строится целиком и на своей половине поля.</summary>
    private static Sim ShowcaseArmy()
    {
        var sim = Create();
        var army = Sim.RandomArmy(60000, null, () => 0.37);
        sim.SpawnBattle(1, 0, 0, army, army);
        var (own, foe) = (0, 0);
        foreach (var entity in sim.With(Names.Position.Name, Names.Unit.Name, Names.Owner.Name))
        {
            if (sim.OwnerOf(entity) == 1)
            {
                own++;
                Assert.True(sim.At(entity).X < 0);
            }
            else
            {
                foe++;
                Assert.True(sim.At(entity).X > 0);
            }
        }
        Assert.Equal(army.Count, own);
        Assert.Equal(army.Count, foe);
        return sim;
    }

    [Fact(DisplayName = "показательный бой: большая армия строится целиком, стороны не перемешаны, и бой идёт без ошибок")]
    public void ShowcaseLargeArmy()
    {
        using var sim = ShowcaseArmy();
        // Пять секунд боя: сотни юнитов ищут путь разом; ошибка системы или мода роняет Advance. Время тика меряет замер
        // ниже (ShowcaseLargeArmyTickBench), не этот тест: на загруженной машине оно говорит о соседях, а не о коде.
        for (var tick = 0; tick < 100; tick++)
        {
            if (tick % 10 == 0)
                sim.DriveBattle(1);
            sim.Advance();
        }
        var sides = sim.With(Names.Unit.Name, Names.Owner.Name).Select(sim.OwnerOf).Distinct().Count();
        Assert.Equal(2, sides);
    }

    [Fact(Explicit = true, DisplayName = "замер: показательный бой — тик остаётся коротким")]
    [Trait("Category", "Bench")]
    public void ShowcaseLargeArmyTickBench()
    {
        using var sim = ShowcaseArmy();
        // Десять секунд боя: сотни юнитов ищут путь разом, и ни один тик не должен превращаться в заметную запинку.
        // Первый тик новой партии в режиме CLR компилирует код мода (своя AssemblyLoadContext у каждой партии) — это не тик
        // боя: в TS процесс к этому тесту уже разогрет.
        sim.Advance();
        double worst = 0;
        var slowest = "";
        for (var tick = 0; tick < 200; tick++)
        {
            if (tick % 10 == 0)
                sim.DriveBattle(1);
            var before = sim.Host.Scheduler.Timings().ToDictionary(t => t.Name, t => t.Gather + t.Run + t.Scatter + t.Commands);
            var from = Stopwatch.GetTimestamp();
            sim.Advance();
            var spent = Stopwatch.GetElapsedTime(from).TotalMilliseconds;
            if (spent <= worst)
                continue;
            worst = spent;
            // Что съело самый долгий тик — в сообщение, если он выйдет за предел.
            slowest = string.Join(", ", sim.Host.Scheduler.Timings()
                .Select(t => (t.Name, Ms: t.Gather + t.Run + t.Scatter + t.Commands - before.GetValueOrDefault(t.Name)))
                .OrderByDescending(t => t.Ms).Take(5).Select(t => $"{t.Name} {t.Ms:F1}")) + $" (тик {tick})";
        }
        Assert.True(worst < 100, $"самый долгий тик — {worst:F1} мс: {slowest}");
    }

    [Fact(DisplayName = "строитель чинит повреждённую технику — по приказу и сам; пехоту не чинит, она поправляется сама")]
    public void BuilderRepairsVehicles()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        sim.SetCredits(1, 1000);
        var builder = Spawn(sim, "builder", 1, x + 2, y);
        var tank = Spawn(sim, "tank", 1, x + 8, y);
        var soldier = Spawn(sim, "infantry", 1, x + 4, y + 2);

        // Целый юнит работой не считается, раненый пехотинец — тоже.
        sim.Set(soldier, "Health.value", 0.5);
        sim.Send(1, new AssistOrder { Units = [builder], Site = tank });
        sim.Send(1, new AssistOrder { Units = [builder], Site = soldier });
        sim.Seconds(2);
        Assert.False(sim.Has(builder, Names.Builds.Name));
        Assert.False(sim.Has(builder, Names.Path.Name));
        Assert.Equal(0.5 + InfantryRegen * 2, Health(sim, soldier), 2);

        // Повреждённый танк свободный строитель находит сам, подъезжает и чинит; починка стоит денег.
        sim.Set(tank, "Health.value", 0.5);
        sim.Seconds(1.5);
        Assert.Equal(tank, (uint)sim.Num(builder, "Builds.site"));
        sim.Seconds(30);
        Assert.Equal(1, Health(sim, tank));
        Assert.False(sim.Has(builder, Names.Builds.Name));
        var distance = Sim.Distance(sim.At(builder), sim.At(tank));
        // Вплотную строитель не подъезжает: ему достаточно дотянуться.
        Assert.True(distance > 3);
        Assert.True(distance <= Unit("builder").Repair!.Value + Unit("tank").Radius);

        // Сам себя строитель не чинит, не своего — тоже.
        sim.Set(builder, "Health.value", 0.5);
        var (nearX, nearY) = sim.At(builder);
        var foe = Spawn(sim, "builder", 0, (int)Math.Floor(nearX) + 1, (int)Math.Floor(nearY));
        sim.Set(foe, "Health.value", 0.5);
        sim.Seconds(3);
        Assert.Equal(0.5, Health(sim, builder));
        Assert.Equal(0.5, Health(sim, foe));
        sim.Seconds(30);
        Assert.Equal(1, Health(sim, soldier));
    }

    /// <summary>Ремонтная станция для теста: здание, которому выдан Repair (радиус).</summary>
    private static uint Station(Sim sim, int x, int y, double radius)
    {
        var station = Place(sim, "turret", x, y, 1);
        sim.Add(station, Names.Repair.Name);
        sim.Set(station, "Repair.radius", radius);
        return station;
    }

    [Fact(DisplayName = "всё, у чего есть Repair, чинит своих в радиусе — по одному, ближнего первым; не дотягивается — не чинит")]
    public void EverythingWithRepairRepairs()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        sim.SetCredits(1, 5000);
        Station(sim, x + 6, y, 3);
        var near = Spawn(sim, "tank", 1, x + 4, y);
        var other = Spawn(sim, "buggy", 1, x + 8, y + 1);
        var far = Spawn(sim, "tank", 1, x + 14, y);
        foreach (var entity in new[] { near, other, far })
            sim.Set(entity, "Health.value", 0.5);
        sim.Seconds(1);
        Assert.True(Health(sim, near) > 0.5);
        Assert.Equal(0.5, Health(sim, other));
        sim.Seconds(15);
        Assert.Equal(1, Health(sim, near));
        Assert.True(Health(sim, other) > 0.5);
        Assert.Equal(0.5, Health(sim, far));
    }

    [Fact(DisplayName = "строитель чинит, только повернувшись к цели")]
    public void BuilderRepairsFacingTarget()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        sim.SetCredits(1, 1000);
        var builder = Spawn(sim, "builder", 1, x + 2, y);
        var tank = Spawn(sim, "tank", 1, x + 5, y);
        // Смотрит в обратную сторону.
        sim.Set(builder, "Facing.angle", Math.PI);
        sim.Set(tank, "Health.value", 0.5);
        sim.Advance();
        Assert.Equal(0.5, Health(sim, tank));
        sim.Seconds(1);
        Assert.True(Math.Abs(Facing(sim, builder)) < 0.2);
        Assert.True(Health(sim, tank) > 0.5);
    }

    [Fact(DisplayName = "носитель везёт турели: они едут с ним, ракетные бьют врага и на ходу, ремонтная чинит соседей, но не его")]
    public void CarrierCarriesTurrets()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        sim.SetCredits(1, 5000);
        var carrier = Spawn(sim, "carrier", 1, x + 2, y);
        var turrets = sim.TurretsOf(carrier);
        Assert.Equal(["rocket", "rocket", "rocket", "repair"], turrets.Select(turret => Scene.TypeOf(sim, turret)!));
        // Турели — не юниты: их не выделить и не подстрелить, у них нет своей прочности.
        foreach (var turret in turrets)
            Assert.False(sim.Has(turret, Names.Unit.Name) || sim.Has(turret, Names.Health.Name));

        // Едут вместе с носителем.
        sim.Send(1, new MoveOrder { Units = [carrier], X = x + 10, Y = y });
        sim.Seconds(1.5);
        var at = sim.At(carrier);
        foreach (var turret in turrets)
            Assert.True(Sim.Distance(sim.At(turret), at) < 0.5);

        // Ракетные турели сами бьют врага в пределах выстрела, даже пока носитель едет; урон считается за носителем.
        var foe = Spawn(sim, "tank", 2, x + 14, y + 2);
        sim.Seconds(3);
        Assert.True(Health(sim, foe) < 1);
        Assert.Equal(foe, sim.TargetOf(turrets[0]));

        // Ремонтная чинит свой юнит рядом, а свой носитель — нет.
        sim.Erase(foe);
        var tank = Spawn(sim, "tank", 1, (int)Math.Floor(at.X) + 3, (int)Math.Floor(at.Y) - 2);
        sim.Set(tank, "Health.value", 0.5);
        sim.Set(carrier, "Health.value", 0.5);
        sim.Seconds(3);
        Assert.True(Health(sim, tank) > 0.5);
        Assert.Equal(0.5, Health(sim, carrier));

        // Переживает сохранение, а с гибелью носителя турели исчезают.
        using var copy = sim.ThroughSave();
        Assert.Equal(turrets, copy.TurretsOf(carrier));
        copy.Erase(carrier);
        copy.Advance();
        foreach (var turret in turrets)
            Assert.False(copy.Alive(turret));
    }

    [Fact(DisplayName = "приказ атаковать носителю: он подъезжает на выстрел турелей, и они бьют цель")]
    public void CarrierAttackOrder()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var carrier = Spawn(sim, "carrier", 1, x + 1, y);
        var foe = Spawn(sim, "tesla", 2, x + 18, y);
        sim.Send(1, new AttackOrder { Units = [carrier], Target = foe });
        sim.Seconds(1);
        Assert.True(sim.Has(carrier, Names.Path.Name));
        sim.Seconds(8);
        Assert.True((sim.Alive(foe) ? Health(sim, foe) : 0) < 1);
        Assert.True(Sim.Distance(sim.At(carrier), (x + 18.5, y + 0.5)) < Weapon("launcher").Range + 1);
    }

    [Fact(DisplayName = "скорость и цена ремонта берутся из правил симуляции и меняются на ходу")]
    public void RepairRulesFromSettings()
    {
        // Правила партии здесь — настройки при создании, а не поле на ходу: каждая скорость — своя партия.
        (double Gain, double Spent) Repaired(object rules)
        {
            var (sim, x, y) = Field(rules: rules);
            using var _sim = sim;
            sim.SetCredits(1, 5000);
            Station(sim, x + 6, y, 3);
            var tank = Spawn(sim, "tank", 1, x + 4, y);
            sim.Set(tank, "Health.value", 0.2);
            sim.Seconds(2);
            return (Health(sim, tank) - 0.2, 5000 - sim.Credits(1));
        }
        var slow = Repaired(new { repairSpeed = 2 });
        Assert.Equal(slow.Gain * 2, Repaired(new { repairSpeed = 4 }).Gain, 2);
        Assert.Equal(0, Repaired(new { repairSpeed = 0 }).Gain);
        // Цена за ту же работу: вдвое дороже при repairCost 1, чем при 0,5 по умолчанию.
        var dear = Repaired(new { repairCost = 1 });
        Assert.Equal(slow.Gain, dear.Gain, 6);
        Assert.Equal(slow.Spent * 2, dear.Spent, 0);
    }

    [Fact(DisplayName = "под огнём не чинят: три секунды после попадания работа стоит")]
    public void NoRepairUnderFire()
    {
        (Sim Sim, uint Tank) Scene(object? rules)
        {
            var (sim, x, y) = Field(rules: rules);
            sim.SetCredits(1, 5000);
            Station(sim, x + 6, y, 3);
            var tank = Spawn(sim, "tank", 1, x + 4, y);
            // Попадание отмечает тик: ремонтник рядом, но пока не работает.
            sim.Set(tank, "Health.value", 0.2);
            sim.Set(tank, "Health.hit", sim.Tick);
            return (sim, tank);
        }
        var (sim, tank) = Scene(null);
        using (sim)
        {
            sim.Seconds(RepairPause - 0.5);
            Assert.Equal(0.2, Health(sim, tank));
            // Пауза вышла — чинят как обычно.
            sim.Seconds(1);
            Assert.True(Health(sim, tank) > 0.2);
        }
        // Правило нулевое — чинят и под огнём.
        var (free, other) = Scene(new { repairPause = 0 });
        using (free)
        {
            free.Seconds(1);
            Assert.True(Health(free, other) > 0.2);
        }
    }

    [Fact(DisplayName = "бесплатная починка идёт и без кредитов")]
    public void FreeRepairWithoutCredits()
    {
        var (sim, x, y) = Field(rules: new { repairCost = 0 });
        using var _sim = sim;
        var builder = Spawn(sim, "builder", 1, x + 2, y);
        var tank = Spawn(sim, "tank", 1, x + 5, y);
        sim.Set(tank, "Health.value", 0.5);
        sim.Seconds(2);
        Assert.True(Health(sim, tank) > 0.5);
        Assert.Equal([builder], Repairing(sim));
    }

    /// <summary>Кто сейчас чинит (activeRepairs): ремонтники с целью (Repair.target).</summary>
    internal static List<uint> Repairing(Sim sim) => [.. sim.With(Names.Repair.Name).Where(entity => sim.Num(entity, "Repair.target") != 0)];
}
