using Base.Shared;
using Base.Shared.Rules;
using Kharos.Base.Tests.Kernel;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Energy;

/// <summary>Метка Busy занятых потребителей (packages/tests/busy.test.ts).</summary>
public sealed class BusyTests
{
    /// <summary>Здание случая и что задать ему перед каждым тиком.</summary>
    private sealed record Case(string Name, string Type, Action<Sim, uint> Set, bool Busy);

    /// <summary>Весь груз склада — ровно это (остальное — ноль).</summary>
    private static void Items(Sim sim, uint entity, params (string Good, double Amount)[] goods)
    {
        var stock = sim.World.Schema[sim.Component(Names.Stock.Name)];
        foreach (var field in stock.Fields)
            if (field.Name is not (Names.Stock.Capacity or Names.Stock.Reserve))
                sim.Set(entity, "Stock." + field.Name, goods.FirstOrDefault(good => good.Good == field.Name).Amount);
    }

    private static double Full(string type) => Tables.Buildings[type].Inventory!.Value;

    private static (string, double)[] Recipe(string product) => [.. Tables.All.Products[product].Recipe.Select(pair => (pair.Key, pair.Value))];

    /// <summary>Сколько секунд идёт сборка изделия (cycleSeconds).</summary>
    private static double Cycle(string product) => Tables.All.Products[product].Recipe.Values.Sum();

    private static void Ensure(Sim sim, uint entity, string component)
    {
        if (!sim.Has(entity, component))
            sim.Add(entity, component);
    }

    private static void Assembly(Sim sim, uint entity, double progress)
    {
        sim.Set(entity, "Assembly.on", 1);
        sim.Set(entity, "Assembly.progress", progress);
    }

    private static void Produce(Sim sim, uint entity, double progress, params string[] queue)
    {
        var ids = queue.Select(unit => Scene.Proto(sim, "units/" + unit)).ToArray();
        sim.Set(entity, "Producer.queue", Scene.Ids(ids));
        sim.Set(entity, "Producer.current", ids.Length > 0 ? ids[0] : -1);
        sim.Set(entity, "Producer.progress", progress);
    }

    private static void Research(Sim sim, uint entity, double speed, string lack)
    {
        Ensure(sim, entity, Names.Research.Name);
        sim.Set(entity, "Research.speed", speed);
        sim.Set(entity, "Research.lack", lack);
    }

    private static void Armed(Sim sim, uint entity, uint target, double cooldown)
    {
        foreach (var turret in sim.With(Names.Mount.Name, Names.Armed.Name).Where(turret => sim.Num(turret, "Mount.parent") == entity))
        {
            sim.Set(turret, "Armed.target", target);
            sim.Set(turret, "Armed.cooldown", cooldown);
        }
    }

    private static void Silo(Sim sim, uint entity, double ticks, bool ready, double start)
    {
        sim.Set(entity, "Silo.ticks", ticks);
        sim.Set(entity, "Silo.ready", ready ? 1 : 0);
        sim.Set(entity, "Silo.start", start);
    }

    private static readonly Case[] Cases =
    [
        new("переработка с рудой", "smelter", (sim, e) => Items(sim, e, ("metalOre", 5)), true),
        new("переработка без руды", "smelter", (sim, e) => Items(sim, e), false),
        new("переработка, готовому некуда лечь", "smelter", (sim, e) => Items(sim, e, ("metalOre", 1), ("metal", Full("smelter") - 1)), false),
        new("выключенная переработка", "smelter", (sim, e) =>
        {
            Items(sim, e, ("metalOre", 5));
            Ensure(sim, e, Names.Off.Name);
        }, false),
        new("переработка под разбором", "smelter", (sim, e) =>
        {
            Items(sim, e, ("metalOre", 5));
            if (!sim.Has(e, Names.Site.Name))
            {
                sim.Add(e, Names.Site.Name);
                sim.Set(e, "Site.progress", 1000);
                sim.Set(e, "Site.demolish", 1);
            }
        }, false),
        new("цех посреди сборки", "blockPlant", (sim, e) =>
        {
            Assembly(sim, e, Cycle("blocks") / 2);
            Items(sim, e, ("blocks", Full("blockPlant")));
        }, true),
        new("цех собрал, готовому есть место", "blockPlant", (sim, e) =>
        {
            Assembly(sim, e, Cycle("blocks"));
            Items(sim, e);
        }, true),
        new("цех собрал, готовому некуда лечь", "blockPlant", (sim, e) =>
        {
            Assembly(sim, e, Cycle("blocks"));
            Items(sim, e, ("blocks", Full("blockPlant")));
        }, false),
        new("цех с сырьём", "blockPlant", (sim, e) =>
        {
            Assembly(sim, e, 0);
            Items(sim, e, Recipe("blocks"));
        }, true),
        new("цех с сырьём, но без места", "blockPlant", (sim, e) =>
        {
            Assembly(sim, e, 0);
            Items(sim, e, [.. Recipe("blocks"), ("blocks", Full("blockPlant"))]);
        }, false),
        new("цех без сырья", "blockPlant", (sim, e) =>
        {
            Assembly(sim, e, 0);
            Items(sim, e);
        }, false),
        new("завод с материалами заказа", "factory", (sim, e) =>
        {
            Produce(sim, e, 0, "buggy");
            Items(sim, e, [.. Tables.Units["buggy"].Materials.Select(pair => (pair.Key, pair.Value))]);
        }, true),
        new("завод без материалов", "factory", (sim, e) =>
        {
            Produce(sim, e, 0, "buggy");
            Items(sim, e);
        }, false),
        new("завод с начатым заказом", "factory", (sim, e) =>
        {
            Produce(sim, e, 1, "buggy");
            Items(sim, e);
        }, true),
        new("завод без заказов", "factory", (sim, e) =>
        {
            Items(sim, e, [.. Tables.Units["buggy"].Materials.Select(pair => (pair.Key, pair.Value))]);
            Produce(sim, e, 0);
        }, false),
        new("лаборатория ведёт тему", "lab", (sim, e) => Research(sim, e, 1, ""), true),
        new("лаборатория без энергии", "lab", (sim, e) => Research(sim, e, 0, "power"), true),
        new("лаборатория без темы", "lab", (sim, e) => Research(sim, e, 0, "topic"), false),
        new("ремонтная станция чинит", "repairStation", (sim, e) => sim.Set(e, "Repair.target", e), true),
        new("ремонтной станции чинить некого", "repairStation", (sim, e) => sim.Set(e, "Repair.target", 0), false),
        new("шахта готовит боеголовку", "silo", (sim, e) => Silo(sim, e, 10, false, -1), true),
        new("шахта с готовой боеголовкой", "silo", (sim, e) => Silo(sim, e, 10, true, -1), false),
        new("шахта запускает", "silo", (sim, e) => Silo(sim, e, 10, true, 3), true),
        new("перехватчик готовит ракету", "interceptor", (sim, e) => sim.Set(e, "Interceptor.ticks", 5), true),
        new("перехватчик готов", "interceptor", (sim, e) => sim.Set(e, "Interceptor.ticks", 0), false),
        new("космопорт с рейсом", "spaceport", (sim, e) => Ensure(sim, e, Names.Trade.Name), true),
        new("космопорт с подарком", "spaceport", (sim, e) => Ensure(sim, e, Names.Transfer.Name), true),
        new("космопорт без дел", "spaceport", (_, _) => { }, false),
        new("щит заряжается", "shieldGenerator", (sim, e) => sim.Set(e, "Shield.charge", 0), true),
        new("щит полон", "shieldGenerator", (sim, e) => sim.Set(e, "Shield.charge", Tables.Buildings["shieldGenerator"].Shield!.Capacity), false),
        new("турель с целью", "laserTurret", (sim, e) => Armed(sim, e, e, 0), true),
        new("турель перезаряжается", "laserTurret", (sim, e) => Armed(sim, e, 0, 1), true),
        new("турель без дела", "laserTurret", (sim, e) => Armed(sim, e, 0, 0), false),
    ];

    /// <summary>Партия на пустой земле: здания случаев — каждое на своём месте, вне сетей с энергией.</summary>
    private static Sim Create() => Grid.Blank();

    [Fact(DisplayName = "метку Busy получает потребитель, у которого есть дело: каждый вид — по-своему")]
    public void EachKindIsBusyItsOwnWay()
    {
        using var sim = Create();
        var buildings = Cases.Select((item, i) => Grid.Place(sim, item.Type, -120 + i * 6, -120)).ToArray();
        for (var tick = 0; tick < PowerRules.BusyTicks; tick++)
        {
            for (var i = 0; i < Cases.Length; i++)
                Cases[i].Set(sim, buildings[i]);
            sim.Advance();
        }
        Assert.Equal(Cases.Select(item => (item.Name, item.Busy)), Cases.Select((item, i) => (item.Name, sim.Has(buildings[i], Names.Busy.Name))));
    }

    [Fact(DisplayName = "метку ставят раз в BUSY_TICKS; кончив дело, здание держит её HOLD секунд, а выключенное теряет сразу")]
    public void MarkedEveryBusyTicks()
    {
        using var sim = Create();
        uint[] pair = [Grid.Place(sim, "smelter", -120, -120), Grid.Place(sim, "smelter", -114, -120)];
        var (idle, off) = (pair[0], pair[1]);
        var ore = true;
        var changes = new Dictionary<uint, List<int>> { [idle] = [], [off] = [] };
        for (var i = 0; i < 20 * 10; i++)
        {
            if (i == 20)
            {
                ore = false;
                sim.Add(off, Names.Off.Name);
            }
            foreach (var entity in pair)
            {
                if (ore || entity == off)
                    Items(sim, entity, ("metalOre", 5));
                else
                    Items(sim, entity);
            }
            var before = pair.Select(entity => sim.Has(entity, Names.Busy.Name)).ToArray();
            sim.Advance();
            for (var j = 0; j < pair.Length; j++)
                if (before[j] != sim.Has(pair[j], Names.Busy.Name))
                    changes[pair[j]].Add(i);
        }
        // Оба получили метку на первой разметке и потеряли по разу.
        Assert.Equal(2, changes[idle].Count);
        Assert.Equal(2, changes[off].Count);
        var on = changes[idle][0];
        Assert.True(on < PowerRules.BusyTicks);
        // Перемены — только в тики разметки.
        foreach (var tick in changes[idle].Concat(changes[off]))
            Assert.Equal(0, (tick - on) % PowerRules.BusyTicks);
        // Выключенный теряет метку на первой же разметке после выключения.
        Assert.True(changes[off][1] - 20 < PowerRules.BusyTicks);
        // Оставшийся без руды держит её HOLD секунд после последней разметки за работой.
        var lastSeen = 20 - ((20 - on) % PowerRules.BusyTicks is var rest && rest != 0 ? rest : PowerRules.BusyTicks);
        Assert.Equal((int)Math.Ceiling(PowerRules.BusyHold / Sim.Dt / PowerRules.BusyTicks) * PowerRules.BusyTicks, changes[idle][1] - lastSeen);
    }
}
