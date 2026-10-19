using Base.Shared;
using Base.Shared.Rules;
using Kharos.Base.Tests.Kernel;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Energy;

/// <summary>Реакторы: стержни, выключатель, авария (packages/tests/reactor.test.ts).</summary>
public sealed class ReactorTests
{
    private static double Power => Tables.Buildings["reactor"].Power!.Value;

    private static double RodSeconds => Tables.Buildings["reactor"].Burns!.Seconds;

    /// <summary>Партия и левый верхний тайл ровной скалы (field): реактор (3×3) и радар (−3) рядом — без реактора энергии взять неоткуда.</summary>
    private static (Sim Sim, int X, int Y, uint Reactor, uint Radar) Base(double rods = 0)
    {
        var sim = Grid.Create(new { protection = 0, techTree = false });
        var (x, y) = Grid.Field(sim, 24, 12);
        var reactor = Scene.Place(sim, "reactor", x + 4, y + 2, 1);
        var radar = Scene.Place(sim, "radar", x + 8, y + 2, 1);
        if (rods > 0)
            sim.SetStock(reactor, "rods", rods);
        return (sim, x, y, reactor, radar);
    }

    private static double Rods(Sim sim, uint reactor) => sim.Stock(reactor, "rods");

    private static double Produced(Sim sim) => sim.ZoneEconomies(1)[0].Produced;

    [Fact(DisplayName = "реактор без стержней энергии не даёт")]
    public void NoRodsNoPower()
    {
        var (sim, _, _, _, radar) = Base();
        using var _sim = sim;
        sim.Advance();
        Assert.Equal(0, Produced(sim));
        Assert.Equal(0, sim.PowerSupply()[radar]);
    }

    [Fact(DisplayName = "реактор жжёт стержень в минуту и, пока тот горит, даёт полную выработку")]
    public void BurnsRodPerMinute()
    {
        var (sim, _, _, reactor, radar) = Base(2);
        using var _sim = sim;
        sim.Advance();
        // Первый стержень взят в работу сразу.
        Assert.Equal(1, Rods(sim, reactor));
        Assert.Equal(Power, Produced(sim));
        Assert.Equal(1, sim.PowerSupply()[radar]);
        // Догорел первый — без перерыва горит второй.
        sim.Seconds(RodSeconds);
        Assert.Equal(0, Rods(sim, reactor));
        Assert.Equal(Power, Produced(sim));
        // Догорел и второй — реактор встал.
        sim.Seconds(RodSeconds);
        Assert.Equal(0, sim.Num(reactor, "Reactor.left"));
        Assert.Equal(0, Produced(sim));
    }

    [Fact(DisplayName = "выключенный реактор не вырабатывает и стержень не жжёт, включённый — продолжает")]
    public void SwitchedOffReactorPauses()
    {
        var (sim, _, _, reactor, _) = Base(1);
        using var _sim = sim;
        sim.Advance();
        var left = sim.Num(reactor, "Reactor.left");
        Assert.True(sim.SetWorking(1, reactor, false));
        sim.Seconds(5);
        Assert.True(sim.Has(reactor, Names.Off.Name));
        Assert.Equal(left, sim.Num(reactor, "Reactor.left"));
        Assert.Equal(0, Produced(sim));
        Assert.True(sim.SetWorking(1, reactor, true));
        sim.Advance();
        Assert.Equal(Power, Produced(sim));
    }

    [Fact(DisplayName = "реактор сам заказывает стержни у зоны, когда склад не полон")]
    public void ReactorOrdersRods()
    {
        var (sim, _, _, reactor, _) = Base(1);
        using var _sim = sim;
        var wanted = sim.Requests(1).Where(request => request.To == reactor).ToList();
        Assert.Equal(["rods"], wanted.Select(request => request.Good));
        Assert.Equal(Tables.Buildings["reactor"].Inventory!.Value - 1, wanted[0].Amount);
    }

    [Fact(DisplayName = "разрушенный реактор с топливом взрывается и бьёт всех вокруг, своих тоже")]
    public void ReactorExplodes()
    {
        var (sim, x, y, reactor, _) = Base(2);
        using var _sim = sim;
        var (radius, damage) = (Tables.Buildings["reactor"].Meltdown!.Radius, Tables.Buildings["reactor"].Meltdown!.Damage);
        sim.Advance();
        // Свой генератор вплотную и чужой юнит рядом, а дальняя стена — за радиусом взрыва. Генератор — под реактором, а
        // не справа, как в TS: там его основание легло бы на радар (правка здания место проверяет); от центра реактора
        // до края — те же полтора тайла.
        var own = Scene.Place(sim, "generator", x + 4, y + 5, 1);
        var enemy = Scene.Spawn(sim, "infantry", 2, x + 3, y + 3);
        var far = Scene.Place(sim, "wall", x + 5 + (int)radius + 3, y + 3, 1);
        Doom(sim, reactor);
        sim.Advance();
        Assert.False(sim.Has(reactor, Names.Building.Name));
        // Топливо сгорело в аварии, а не упало на землю.
        Assert.Single(sim.With(Names.Burst.Name));
        sim.Advance();
        Assert.Empty(sim.With(Names.Burst.Name));
        Assert.False(sim.Has(enemy, Names.Health.Name));
        var hp = Tables.Buildings["generator"].Hp ?? Tables.Buildings["generator"].Cost * 2;
        Assert.True(sim.HealthOf(own) < 1 - damage * 0.3 / hp);
        Assert.Equal(1, sim.HealthOf(far));
    }

    /// <summary>Обречённый: погибнет в ближайшем бою (Doomed.left = 1).</summary>
    private static void Doom(Sim sim, uint entity)
    {
        sim.Add(entity, Names.Doomed.Name);
        sim.Set(entity, "Doomed.left", 1);
    }

    [Fact(DisplayName = "реактор с топливом, добитый перегрузом, тоже взрывается")]
    public void OverloadedReactorExplodes()
    {
        var (sim, x, y, reactor, _) = Base(2);
        using var _sim = sim;
        // Восемь мельниц урана просят 64 при 60 горящего стержня: перегруз, а прочности у реактора — на один тик износа.
        for (var i = 0; i < 8; i++)
            Scene.Place(sim, "uraniumMill", x + 8 + i % 4 * 2, y + 5 + i / 4 * 2, 1);
        sim.Advance();
        sim.Set(reactor, "Health.value", 1e-9);
        sim.Advance();
        Assert.False(sim.Alive(reactor));
        Assert.Single(sim.With(Names.Burst.Name));
    }

    [Fact(DisplayName = "пустой реактор при разрушении не взрывается")]
    public void EmptyReactorDoesNotExplode()
    {
        var (sim, x, y, reactor, _) = Base();
        using var _sim = sim;
        var own = Scene.Place(sim, "generator", x + 4, y + 5, 1);
        Doom(sim, reactor);
        sim.Seconds(0.5);
        Assert.False(sim.Has(reactor, Names.Building.Name));
        Assert.Equal(1, sim.HealthOf(own));
    }

    [Fact(DisplayName = "реактор не улучшают, а без дерева технологий — открыт; с деревом нужен «Атом»")]
    public void ReactorUnlockAndUpgrade()
    {
        Assert.Null(BuildRules.LevelGain(Tables.Buildings["reactor"]));
        using var sim = Grid.Create(new { protection = 0, techTree = true });
        Assert.False(sim.Unlocked(1, "reactor"));
        using var free = Grid.Create(new { protection = 0, techTree = false });
        Assert.True(free.Unlocked(1, "reactor"));
    }

    [Fact(DisplayName = "горящий стержень переживает сохранение")]
    public void BurningRodSurvivesSave()
    {
        var (sim, _, _, reactor, _) = Base(1);
        using var _sim = sim;
        sim.Seconds(10);
        var left = sim.Num(reactor, "Reactor.left");
        Assert.True(left > 0);
        using var loaded = sim.ThroughSave();
        Assert.Equal(left, loaded.Num(reactor, "Reactor.left"));
        Assert.True(loaded.Burning(reactor));
    }
}
