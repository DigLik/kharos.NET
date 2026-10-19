using Base.Shared.Orders;
using static Kharos.Base.Tests.Kernel.Scene;

namespace Kharos.Base.Tests.Kernel;

/// <summary>
/// Армия (packages/tests/army.test.ts): требования и темы юнитов, лимит армии, марш армии приказом move. Бой
/// (артиллерия, бомбардировщик, зенитка, огнемётчик) — раздел Combat (С5а).
/// </summary>
public sealed class ArmyTests
{
    /// <summary>Здания игрока 1 в ряд на скале через тайл (row).</summary>
    private static (Sim Sim, uint[] Buildings) Row(string[] types, object? rules = null)
    {
        var sim = Create(rules: rules);
        sim.SetCredits(1, 10000);
        for (var y = -100; y < 100; y++)
            for (var x = -100; x < 100; x++)
            {
                var at = x;
                var spots = new int[types.Length];
                for (var i = 0; i < types.Length; i++)
                {
                    spots[i] = at;
                    at += (int)Data.Buildings[types[i]].Width + 1;
                }
                if (!types.Select((type, i) => (type, i)).All(item => Rock(sim, spots[item.i], y, (int)Data.Buildings[item.type].Width, (int)Data.Buildings[item.type].Height)))
                    continue;
                if (!types.Select((type, i) => (type, i)).All(item => CanPlace(sim, item.type, spots[item.i], y)))
                    continue;
                return (sim, [.. types.Select((type, i) => Place(sim, type, spots[i], y, 1))]);
            }
        throw new InvalidOperationException("Не нашлось места");
    }

    [Fact(DisplayName = "требования юнитов называют существующие здания")]
    public void UnitRequirementsNameExistingBuildings()
    {
        foreach (var (_, unit) in Data.Units)
            foreach (var type in unit.Requires)
                Assert.True(Data.Buildings.ContainsKey(type), type);
    }

    [Fact(DisplayName = "тяжёлую технику не заказать без исследования, с ним — можно")]
    public void HeavyVehiclesNeedResearch()
    {
        var (sim, buildings) = Row(["command", "generator", "generator", "factory"]);
        using var _sim = sim;
        var factory = buildings[3];
        Assert.Equal("armor", MissingResearch(sim, 1, "tank"));
        Assert.Null(MissingResearch(sim, 1, "buggy"));
        sim.Send(1, new ProduceOrder { Producer = factory, Unit = "tank" });
        sim.Send(1, new ProduceOrder { Producer = factory, Unit = "buggy" });
        sim.Advance();
        Assert.Equal(["buggy"], Queue(sim, factory));

        sim.Set(Account(sim, 1), "Science.done", Ids(Proto(sim, "research/armor")));
        Assert.Null(MissingResearch(sim, 1, "tank"));
        Assert.Equal("siege", MissingResearch(sim, 1, "artillery"));
        sim.Send(1, new ProduceOrder { Producer = factory, Unit = "tank" });
        sim.Advance();
        Assert.Equal(["buggy", "tank"], Queue(sim, factory));
        // Исследование своё у каждого игрока.
        Assert.Equal("armor", MissingResearch(sim, 2, "tank"));
    }

    [Fact(DisplayName = "лимит армии: юниты и заказы в очередях занимают место по весу, сверх лимита заказ не принимается")]
    public void ArmyLimitCountsUnitsAndQueues()
    {
        // Лимит — правило партии (в TS его меняют на ходу: sim.rules.armyLimit).
        var (sim, buildings) = Row(["command", "generator", "generator", "factory", "barracks"], new { armyLimit = 10, techTree = false });
        using var _sim = sim;
        var (factory, barracks) = (buildings[3], buildings[4]);
        // Стартовых юнитов нет: занято только то, что поставим. MCV в счёт не идёт.
        var tiles = Builders.FreeTilesNear(sim, 0, 0, 4);
        Spawn(sim, "mcv", 1, tiles[0].X, tiles[0].Y);
        for (var i = 1; i < 4; i++)
            Spawn(sim, "infantry", 1, tiles[i].X, tiles[i].Y);
        Assert.Equal(3 * Data.Units["infantry"].Supply, Army(sim, 1));
        // Две багги в очереди — ещё 4: занято 7.
        sim.Send(1, new ProduceOrder { Producer = factory, Unit = "buggy" });
        sim.Send(1, new ProduceOrder { Producer = factory, Unit = "buggy" });
        sim.Advance();
        Assert.Equal(7, Army(sim, 1));
        // Третья багги (2) влезает впритык — 9, а за ней и пехотинец (1) — 10; дальше — ничего.
        sim.Send(1, new ProduceOrder { Producer = factory, Unit = "buggy" });
        sim.Send(1, new ProduceOrder { Producer = barracks, Unit = "infantry" });
        sim.Send(1, new ProduceOrder { Producer = barracks, Unit = "infantry" });
        sim.Advance();
        Assert.Equal(["buggy", "buggy", "buggy"], Queue(sim, factory));
        Assert.Equal(["infantry"], Queue(sim, barracks));
        Assert.True(OverLimit(sim, 1, "infantry"));
        // Ноль — лимита нет.
        using var free = Create(rules: new { armyLimit = 0 });
        Assert.False(OverLimit(free, 1, "tank"));
    }

    [Fact(DisplayName = "артиллерия бьёт дальше любой турели и разносит её, не получив ответа")]
    public void ArtilleryOutrangesTurrets()
    {
        var weapons = Data.All.Weapons;
        Assert.True(weapons["artillery"].Range > Math.Max(weapons["cannon"].Range, Math.Max(weapons["launcher"].Range, weapons["machinegun"].Range)) + 2);
        using var sim = Create();
        var turret = Combat.Arena.Place(sim, "cannonTurret", 0, 0, 2);
        var gun = Spawn(sim, "artillery", 1, 11, 0);
        sim.Seconds(60);
        Assert.False(sim.Alive(turret));
        Assert.Equal(1, sim.HealthOf(gun));
    }

    [Fact(DisplayName = "в мёртвой зоне артиллерия молчит")]
    public void ArtilleryDeadZone()
    {
        using var sim = Create();
        var foe = Spawn(sim, "infantry", 2, 0, 0);
        Spawn(sim, "artillery", 1, 2, 0);
        sim.Seconds(10);
        Assert.Equal(1, sim.HealthOf(foe));
    }

    [Fact(DisplayName = "бомбардировщик сносит здание, а по летающим не бьёт")]
    public void BomberHitsBuildingsNotFlyers()
    {
        using var sim = Create();
        var target = Combat.Arena.Place(sim, "generator", 0, 0, 2);
        var bomber = Spawn(sim, "bomber", 1, 8, 0);
        var drone = Spawn(sim, "drone", 2, 9, 3);
        sim.Send(1, new AttackOrder { Units = [bomber], Target = target });
        sim.Seconds(60);
        Assert.False(sim.Alive(target));
        // Дрон бомбардировщика клюёт, а тот ему не отвечает.
        Assert.Equal(1, sim.HealthOf(drone));
    }

    [Fact(DisplayName = "зенитка сбивает летающих, а по земле не стреляет")]
    public void FlakHitsFlyersOnly()
    {
        using var sim = Create();
        var flak = Spawn(sim, "flak", 1, 0, 0);
        var ground = Spawn(sim, "infantry", 2, 5, 0);
        var drone = Spawn(sim, "drone", 2, 0, 6);
        sim.Seconds(8);
        Assert.False(sim.Alive(drone));
        Assert.Equal(1, sim.HealthOf(ground));
        // Приказ атаковать наземного зенитке не принимается.
        sim.OrderNow(1, new AttackOrder { Units = [flak], Target = ground });
        Assert.NotEqual(ground, sim.TargetOf(flak));
    }

    [Fact(DisplayName = "огнемётчик выжигает пехоту, и огонь перекидывается на соседа")]
    public void FlamerSpreadsFire()
    {
        using var sim = Create();
        Spawn(sim, "flamer", 1, 0, 0);
        var first = Spawn(sim, "infantry", 2, 2, 0);
        var second = Spawn(sim, "infantry", 2, 3, 1);
        sim.Seconds(1);
        // Когда огнемётчик заметит цель, решает номер сущности (onTurn): здесь он замечает раньше, чем в TS, и за секунду
        // успевает выжечь первого целиком — это тоже «меньше единицы».
        Assert.True(!sim.Alive(first) || sim.HealthOf(first) < 1);
        Assert.True(sim.HealthOf(second) < 1);
    }

    [Fact(DisplayName = "армия доходит по пересечённой местности вся: никто не бросает цель оттого, что по дороге мешали")]
    public void ArmyReachesTheGoalAcrossRoughLand()
    {
        using var sim = Create(1024, fog: false);
        // Место, где до правки вставала четверть армии: у выхода с плато техника упиралась в соседей и в скалу.
        var start = (X: 300, Y: -200);
        var goal = (X: 500, Y: -200);
        for (var y = 0; y < 5; y++)
            for (var x = 0; x < 8; x++)
                Assert.True(sim.Host.Tools.Navigation!.IsWalkable(start.X + x, start.Y + y));
        string[] kinds = ["tank", "tank", "tank", "buggy", "lancer", "flak", "artillery", "infantry", "rocketeer", "tank"];
        var units = new List<uint>();
        for (var i = 0; i < 40; i++)
            units.Add(Spawn(sim, kinds[i % kinds.Length], 1, start.X + i % 8, start.Y + i / 8));
        sim.Advance();
        sim.Send(1, new MoveOrder { Units = [.. units], X = goal.X, Y = goal.Y });
        sim.Advance(20 * 180);
        var far = units.Where(unit => Sim.Distance(sim.At(unit), (goal.X, goal.Y)) > 16).ToList();
        Assert.Empty(far);
        Assert.DoesNotContain(units, unit => sim.Has(unit, global::Base.Shared.Names.Path.Name));
    }
}
