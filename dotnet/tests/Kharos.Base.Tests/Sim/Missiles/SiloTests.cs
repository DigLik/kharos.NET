using Base.Shared;
using Base.Shared.Data;
using Base.Shared.Orders;
using Base.Shared.Rules;
using Kharos.Base.Tests.Kernel;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Missiles;

/// <summary>Ядерный удар: шахты, пуск, ракета в полёте, метки и вспышка (packages/tests/silo.test.ts).</summary>
public sealed class SiloTests
{
    private static WarheadSpec Warhead => Pit.Warhead;

    /// <summary>База игрока 1 с шахтой; sim — партия (silo base).</summary>
    private static Camp Base(Sim? sim = null)
    {
        sim ??= Pit.Create();
        return Pit.Base(sim, 1, "silo", -200, 0);
    }

    /// <summary>База с готовой боеголовкой в шахте (armed).</summary>
    private static Camp Armed()
    {
        var camp = Base();
        camp.Sim.Set(camp.Building, "Silo.ready", 1);
        return camp;
    }

    private static void Launch(Camp camp, int x, int y) => camp.Sim.Send(1, new LaunchOrder { Silo = camp.Building, X = x, Y = y });

    private static void Close(double expected, double actual, double tolerance = 0.5) =>
        Assert.True(Math.Abs(expected - actual) < tolerance, $"ждали {expected}, вышло {actual}");

    [Fact(DisplayName = "шахта собирает боеголовку за кредиты, стержни и компоненты из сети и за время при полной энергии")]
    public void AssemblesWarhead()
    {
        var camp = Base();
        using var sim = camp.Sim;
        var silo = camp.Building;
        Assert.Equal("materials", sim.WarheadLack(1, silo));
        camp.Stash(("rods", 25), ("parts", 12));
        Assert.Null(sim.WarheadLack(1, silo));
        var credits = sim.Credits(1);
        sim.Send(1, new WarheadOrder { Silo = silo });
        sim.Advance();
        Close(credits - Warhead.Cost, sim.Credits(1));
        Assert.Equal(5, camp.Stock("rods"));
        Assert.Equal(2, camp.Stock("parts"));
        Assert.Equal("busy", sim.WarheadLack(1, silo));
        sim.Seconds(Warhead.Seconds - 1);
        Assert.Equal(0, sim.Num(silo, "Silo.ready"));
        sim.Seconds(2);
        Assert.Equal(1, sim.Num(silo, "Silo.ready"));
        // Одна за раз: с готовой новую не начать.
        Assert.Equal("busy", sim.WarheadLack(1, silo));
    }

    [Fact(DisplayName = "выключенная шахта сборку не ведёт, отмена возвращает цену")]
    public void SwitchedOffSiloHoldsAndCancelRefunds()
    {
        var camp = Base();
        using var sim = camp.Sim;
        var silo = camp.Building;
        camp.Stash(("rods", 20), ("parts", 10));
        var credits = sim.Credits(1);
        sim.Send(1, new WarheadOrder { Silo = silo });
        sim.Send(1, new WorkOrder { Building = silo, On = false });
        sim.Seconds(5);
        Assert.True(sim.Has(silo, Names.Off.Name));
        Assert.Equal(0, sim.Num(silo, "Silo.progress"));
        sim.Send(1, new CancelWarheadOrder { Silo = silo });
        sim.Advance();
        Assert.Equal(0, sim.Num(silo, "Silo.ticks"));
        // Главное здание за эти секунды принесло кредит-другой.
        Assert.True(Math.Abs(sim.Credits(1) - credits) < 5);
        Assert.Equal(20, camp.Stock("rods"));
        Assert.Equal(10, camp.Stock("parts"));
    }

    [Fact(DisplayName = "пуск: ракета стартует через срок пуска, отменённый пуск оставляет боеголовку в шахте")]
    public void LaunchStartsAfterDelayAndAbortKeepsWarhead()
    {
        var camp = Armed();
        using var sim = camp.Sim;
        var silo = camp.Building;
        Launch(camp, camp.X + 100, camp.Y);
        sim.Seconds(Warhead.Launch / 2);
        sim.Send(1, new AbortLaunchOrder { Silo = silo });
        sim.Seconds(Warhead.Launch);
        Assert.Empty(sim.Missiles());
        Assert.Equal(1, sim.Num(silo, "Silo.ready"));
        Assert.Equal(-1, sim.Num(silo, "Silo.start"));

        Launch(camp, camp.X + 100, camp.Y);
        sim.Seconds(Warhead.Launch - 1);
        Assert.Empty(sim.Missiles());
        sim.Seconds(1.1);
        Assert.Single(sim.Missiles());
        Assert.Equal(0, sim.Num(silo, "Silo.ready"));
        // Цель вне карты не принимается.
        sim.Set(silo, "Silo.ready", 1);
        Launch(camp, 100000, camp.Y);
        sim.Advance();
        Assert.Equal(-1, sim.Num(silo, "Silo.start"));
    }

    [Fact(DisplayName = "место пуска видят игроки с радаром, и только LAUNCH_MARK секунд")]
    public void LaunchSiteIsSeenByRadarOwners()
    {
        var camp = Armed();
        using var sim = camp.Sim;
        // У игрока 2 далеко есть радар, у игрока 3 — только электростанция.
        var far = Pit.Spot(sim, camp.X + 300, camp.Y, "radar");
        sim.PutBuilding("radar", far.X, far.Y, 2);
        var other = Pit.Spot(sim, camp.X - 300, camp.Y);
        sim.PutBuilding("generator", other.X, other.Y, 3);
        var neutral = Pit.Spot(sim, camp.X, camp.Y + 300, "radar");
        sim.PutBuilding("radar", neutral.X, neutral.Y, 0);
        Launch(camp, camp.X + 200, camp.Y);
        sim.Seconds(Warhead.Launch + 0.1);
        var marks = sim.Marks("launch");
        Assert.Single(marks);
        var mark = marks[0].Entity;
        Assert.True(sim.ShownTo(2, mark));
        Assert.False(sim.ShownTo(3, mark));
        Assert.True(sim.ShownTo(1, mark));
        // Ничьему радару место пуска не показывают.
        Assert.Equal([2u], sim.MarkOf(mark).Viewers);
        sim.Seconds(MissileRules.LaunchMark - 1);
        Assert.Equal([mark], sim.Marks("launch").Select(found => found.Entity));
        sim.Seconds(1);
        Assert.Empty(sim.Marks("launch"));
    }

    [Fact(DisplayName = "ракета летит со скоростью вида, её видят все, точку удара весь полёт — те, чьё под ней, и взрыв бьёт всех")]
    public void MissileFliesAndBlastHitsEveryone()
    {
        var camp = Armed();
        using var sim = camp.Sim;
        // Цель — в 190 тайлах: 24 секунды полёта. У цели — здание и юнит игрока 2, на краю радиуса — его же здание.
        var target = Pit.Spot(sim, camp.X + 200, camp.Y);
        var near = sim.PutBuilding("generator", target.X, target.Y, 2);
        var unit = sim.PutUnit("tank", 2, target.X + 3.5, target.Y + 1.5);
        var edge = Pit.Spot(sim, target.X + 10, target.Y);
        var rim = sim.PutBuilding("generator", edge.X, edge.Y, 2);
        var away = Pit.Spot(sim, target.X, target.Y + 60);
        sim.PutBuilding("generator", away.X, away.Y, 3);
        int toX = target.X + 1, toY = target.Y + 1;
        Launch(camp, toX, toY);
        sim.Seconds(Warhead.Launch + 0.1);
        var missile = sim.Missiles()[0];
        double Field(string field) => sim.Num(missile, "Missile." + field);
        var flight = Math.Sqrt(Math.Pow(toX + 0.5 - Field("fromX"), 2) + Math.Pow(toY + 0.5 - Field("fromY"), 2)) / Warhead.Speed;
        Assert.True(flight > MissileRules.Warning + 2);

        // Чужому ракета уходит без точки удара, только с курсом.
        var foreign = new Kharos.Host.VisionView(sim.Host, 2);
        var own = new Kharos.Host.VisionView(sim.Host, 1);
        var component = sim.Component(Names.Missile.Name);
        Assert.NotEqual(0UL, foreign.Hidden(sim.World, missile, component));
        Assert.Equal(0UL, own.Hidden(sim.World, missile, component));
        Close(1, Math.Sqrt(Math.Pow(Field("courseX"), 2) + Math.Pow(Field("courseY"), 2)), 0.05);
        Assert.Equal(toX + 0.5, Field("toX"));

        // Ракету видят все, где бы она ни была; точку удара — со старта, но только тот, чьё под ней.
        var strike = sim.Marks("strike")[0].Entity;
        foreach (var player in new uint[] { 1, 2, 3 })
            Assert.True(sim.ShownTo(player, missile));
        Assert.False(sim.Sees(3, Field("x"), Field("y")));
        Assert.True(sim.ShownTo(2, strike));
        Assert.False(sim.ShownTo(3, strike));
        Assert.False(sim.Sees(3, toX, toY));

        // Игрок 3 завёл юнит под удар — через секунду точка видна и ему; увёл — пропала.
        var scout = sim.PutUnit("tank", 3, target.X + 4.5, target.Y + 4.5);
        sim.Seconds(1.1);
        Assert.True(sim.ShownTo(3, strike));
        sim.Erase(scout);
        sim.Seconds(1.1);
        Assert.False(sim.ShownTo(3, strike));

        sim.Seconds(flight - 2.2);
        Assert.Empty(sim.Missiles());
        Assert.Empty(sim.Marks("strike"));
        sim.Advance();
        Assert.True(Pit.Gone(sim, near));
        Assert.True(Pit.Gone(sim, unit));
        // На краю радиуса урон меньше полного, но есть.
        var left = Pit.Health(sim, rim);
        var hp = Tables.Buildings["generator"].Hp ?? Tables.Buildings["generator"].Cost * 2;
        Assert.True(left < 1);
        Assert.True(1 - left < Warhead.Blast.Damage / hp);
        Assert.Equal(0, sim.Count(Names.Burst.Name));
        // Вспышка открывает круг взрыва всем, даже игроку 3, чьё далеко, — и гаснет через GLARE секунд (обзор считается раз в несколько тиков).
        sim.Seconds(0.3);
        Assert.True(sim.Sees(3, toX, toY));
        Assert.True(sim.Sees(3, toX + Warhead.Blast.Radius - 1, toY));
        sim.Seconds(MissileRules.Glare - 1.3);
        Assert.True(sim.Sees(3, toX, toY));
        sim.Seconds(1.5);
        Assert.False(sim.Sees(3, toX, toY));
        Assert.Equal(0, sim.Count(Names.Reveal.Name));
    }

    [Fact(DisplayName = "точку удара видят чужие, чьё под ней, — без хозяина ракеты и ничьих; метка живёт до удара")]
    public void StrikeMarkIsSeenByThoseUnderIt()
    {
        var camp = Armed();
        using var sim = camp.Sim;
        var target = Pit.Spot(sim, camp.X + 200, camp.Y);
        sim.PutBuilding("generator", target.X, target.Y, 2);
        sim.PutBuilding("wall", target.X + 4, target.Y, 1);
        sim.PutBuilding("wall", target.X + 5, target.Y, 0);
        Launch(camp, target.X + 1, target.Y + 1);
        sim.Seconds(Warhead.Launch + 0.1);
        var strike = sim.Marks("strike")[0].Entity;
        Assert.Equal([2u], sim.MarkOf(strike).Viewers);
        sim.Seconds(1.1);
        Assert.Equal([2u], sim.MarkOf(strike).Viewers);
        var missile = sim.Missiles()[0];
        double Field(string field) => sim.Num(missile, "Missile." + field);
        var left = Math.Ceiling(Math.Sqrt(Math.Pow(Field("toX") - Field("x"), 2) + Math.Pow(Field("toY") - Field("y"), 2)) / (Warhead.Speed * Pit.Tick));
        var until = sim.MarkOf(strike).Until - sim.Tick;
        Assert.True(until >= left);
        Assert.True(until <= left + 2);
    }

    [Fact(DisplayName = "без энергии шахта боеголовку не собирает")]
    public void NoPowerNoWarhead()
    {
        var camp = Base();
        using var sim = camp.Sim;
        var silo = camp.Building;
        sim.Erase([.. sim.Of(1, Names.Building.Name).Where(entity => Scene.TypeOf(sim, entity) == "generator")]);
        camp.Stash(("rods", 20), ("parts", 10));
        sim.Send(1, new WarheadOrder { Silo = silo });
        sim.Seconds(5);
        Assert.True(sim.Num(silo, "Silo.ticks") > 0);
        Assert.Equal(0, sim.Num(silo, "Silo.progress"));
    }

    [Fact(DisplayName = "шахта с готовой боеголовкой при разрушении рвётся, как реактор; пустая — нет")]
    public void LoadedSiloExplodesWhenDestroyed()
    {
        var camp = Armed();
        using var sim = camp.Sim;
        var own = sim.PutBuilding("wall", camp.X + 9, camp.Y + 1, 1);
        Doom(sim, camp.Building);
        sim.Advance();
        Assert.Equal(1, sim.Count(Names.Burst.Name));
        sim.Advance();
        Assert.True(sim.HealthOf(own) < 1 || Pit.Gone(sim, own));

        var empty = Base();
        using var emptySim = empty.Sim;
        Doom(emptySim, empty.Building);
        emptySim.Advance();
        Assert.Equal(0, emptySim.Count(Names.Burst.Name));
    }

    private static void Doom(Sim sim, uint entity)
    {
        sim.Add(entity, Names.Doomed.Name);
        sim.Set(entity, "Doomed.left", 1);
    }

    [Fact(DisplayName = "пуск снимает с игрока защиту новичка")]
    public void LaunchRemovesNewbieProtection()
    {
        var sim = Pit.Create(new { protection = 10, techTree = false });
        var camp = Base(sim);
        using var _sim = sim;
        sim.Set(camp.Building, "Silo.ready", 1);
        var wallet = sim.Wallet(1);
        sim.Set(wallet, "Player.safe", sim.Tick + 200);
        Assert.True(sim.Num(wallet, "Player.safe") > sim.Tick);
        Launch(camp, camp.X + 50, camp.Y);
        sim.Advance();
        Assert.Equal(0, sim.Num(wallet, "Player.safe"));
    }

    [Fact(DisplayName = "шахту открывают техцентр, завод стержней и «Боеголовки» после «Атома»")]
    public void SiloIsUnlockedByResearch()
    {
        Assert.Equal(12, Warhead.Blast.Radius);
        var sim = Pit.Create(new { protection = 0, techTree = true });
        using var _sim = sim;
        Assert.False(sim.Unlocked(1, "silo"));
    }

    [Fact(DisplayName = "сборка, пуск и ракета в полёте переживают сохранение")]
    public void AssemblyAndFlightSurviveSave()
    {
        var camp = Armed();
        using var sim = camp.Sim;
        Launch(camp, camp.X + 200, camp.Y);
        sim.Seconds(Warhead.Launch + 2);
        var missile = sim.Missiles()[0];
        string[] fields = ["fromX", "fromY", "toX", "toY", "born", "x", "y", "player", "mark", "hunted"];
        var before = fields.Select(field => sim.Num(missile, "Missile." + field)).ToArray();
        using var loaded = sim.ThroughSave();
        Assert.Equal(before, fields.Select(field => loaded.Num(missile, "Missile." + field)).ToArray());
        Assert.Single(loaded.Marks("launch"));
        Assert.True(sim.SameWorld(loaded));
    }
}
