using Base.Shared;
using Base.Shared.Data;
using Base.Shared.Orders;
using Base.Shared.Rules;
using Kharos.Base.Tests.Kernel;

namespace Kharos.Base.Tests.Missiles;

/// <summary>Перехватчики: сборка ракет, перехват в воздухе, залпы (packages/tests/interceptor.test.ts).</summary>
public sealed class InterceptorTests
{
    private static readonly string[] Shelves = ["partsLocker", "fuelTank", "rodRack"];

    private static InterceptSpec Intercept => Pit.Intercept;

    private static WarheadSpec Warhead => Pit.Warhead;

    private sealed record Standoff(Sim Sim, Camp Attacker, Camp Defender, (double X, double Y) Center) : IDisposable
    {
        public void Dispose() => Sim.Dispose();
    }

    /// <summary>Игрок 1 с готовой боеголовкой и игрок 2 с перехватчиком, в котором loaded ракет, в distance тайлах восточнее (standoff).</summary>
    private static Standoff Face(int distance = 260, int loaded = 1)
    {
        var sim = Pit.Create();
        var attacker = Pit.Base(sim, 1, "silo", -120, 0, Shelves);
        sim.Set(attacker.Building, "Silo.ready", 1);
        var defender = Pit.Base(sim, 2, "interceptor", attacker.X + distance, attacker.Y, Shelves);
        sim.Set(defender.Building, "Interceptor.missiles", loaded);
        // Перехватчик не собирает новую ракету сам: в сети нет ни компонентов, ни топлива.
        return new Standoff(sim, attacker, defender, (defender.X + 11, defender.Y + 1));
    }

    /// <summary>Пускает ракету игрока 1 в тайл (x, y) и ждёт старта (fire).</summary>
    private static void Fire(Sim sim, uint silo, int x, int y)
    {
        sim.Send(1, new LaunchOrder { Silo = silo, X = x, Y = y });
        sim.Seconds(Warhead.Launch + 0.1);
        Assert.Single(sim.Missiles());
    }

    private static double Missiles(Sim sim, uint building) => sim.Num(building, "Interceptor.missiles");

    [Fact(DisplayName = "перехватчик сам собирает ракеты по одной, пока их меньше четырёх, — из компонентов и топлива сети")]
    public void BuildsMissilesOneByOne()
    {
        var sim = Pit.Create();
        using var _sim = sim;
        var camp = Pit.Base(sim, 1, "interceptor", 0, 0, Shelves);
        var building = camp.Building;
        sim.Advance();
        Assert.Equal("materials", sim.InterceptorLack(building));
        camp.Stash(("parts", 6), ("fuel", 15));
        var credits = sim.Credits(1);
        sim.Advance();
        Assert.True(Math.Abs(credits - Intercept.Cost - sim.Credits(1)) < 0.5);
        Assert.Equal(2, camp.Stock("parts"));
        Assert.Equal(5, camp.Stock("fuel"));
        Assert.Null(sim.InterceptorLack(building));
        sim.Seconds(Intercept.Seconds - 1);
        Assert.Equal(0, Missiles(sim, building));
        sim.Seconds(2);
        Assert.Equal(1, Missiles(sim, building));
        // Собирает дальше, пока не наберёт четыре, — и тогда встаёт.
        camp.Stash(("parts", 20), ("fuel", 50));
        sim.Seconds(Intercept.Seconds * (Intercept.Capacity - 1) + 3);
        Assert.Equal(4, Intercept.Capacity);
        Assert.Equal(4, Missiles(sim, building));
        Assert.Null(sim.InterceptorLack(building));
        Assert.Equal(20 - 4 * (Intercept.Capacity - 1), camp.Stock("parts"));
        sim.Seconds(Intercept.Seconds);
        Assert.Equal(20 - 4 * (Intercept.Capacity - 1), camp.Stock("parts"));
        Assert.Equal(0, sim.Num(building, "Interceptor.ticks"));
    }

    [Fact(DisplayName = "выключенный перехватчик ракету не собирает и не стреляет")]
    public void SwitchedOffInterceptorDoesNothing()
    {
        using var face = Face();
        var (sim, attacker, defender, center) = (face.Sim, face.Attacker, face.Defender, face.Center);
        sim.Send(2, new WorkOrder { Building = defender.Building, On = false });
        sim.Advance();
        Assert.True(sim.Has(defender.Building, Names.Off.Name));
        Fire(sim, attacker.Building, (int)center.X, (int)center.Y);
        sim.Seconds(40);
        Assert.Empty(sim.Missiles());
        Assert.True(Pit.Health(sim, defender.Core) < 1);

        var sim2 = Pit.Create();
        using var _sim2 = sim2;
        var empty = Pit.Base(sim2, 1, "interceptor", 0, 0, Shelves);
        empty.Stash(("parts", 4), ("fuel", 10));
        sim2.Send(1, new WorkOrder { Building = empty.Building, On = false });
        sim2.Seconds(1);
        Assert.Equal("off", sim2.InterceptorLack(empty.Building));
        Assert.Equal(4, empty.Stock("parts"));
    }

    [Fact(DisplayName = "ракета, которая метит в круг перехватчика, сбита в воздухе: удара нет, метка точки удара пропадает")]
    public void MissileAimedAtCircleIsShotDown()
    {
        using var face = Face();
        var (sim, attacker, defender, center) = (face.Sim, face.Attacker, face.Defender, face.Center);
        Fire(sim, attacker.Building, (int)center.X, (int)center.Y);
        var missile = sim.Missiles()[0];
        double Field(string field) => sim.Num(missile, "Missile." + field);
        var flight = Math.Sqrt(Math.Pow(Field("toX") - Field("fromX"), 2) + Math.Pow(Field("toY") - Field("fromY"), 2)) / Warhead.Speed;
        var chased = false;
        var marked = false;
        (double Size, double Lift)? burst = null;
        for (var i = 0; i < Math.Round(flight / Pit.Tick) + 40; i++)
        {
            sim.Advance();
            foreach (var blast in sim.With(Names.Blast.Name))
                burst ??= (sim.Num(blast, "Blast.size"), sim.Num(blast, "Blast.lift"));
            chased |= sim.Chasers().Count > 0;
            marked |= sim.Marks("strike").Count > 0;
        }
        Assert.True(chased);
        Assert.Empty(sim.Missiles());
        Assert.Empty(sim.Chasers());
        Assert.Empty(sim.Marks("strike"));
        Assert.Equal(0, sim.Count(Names.Burst.Name));
        Assert.Equal(1, sim.HealthOf(defender.Core));
        Assert.True(marked);
        Assert.Equal(0, Missiles(sim, defender.Building));
        // Сбитая далеко от цели гибнет на высоте полёта.
        Assert.Equal((MissileRules.Airburst, MissileRules.Cruise), burst);
    }

    [Fact(DisplayName = "сбивает, когда ракета входит в круг, — и на краю круга успевает до удара")]
    public void ShootsAtTheEdgeInTime()
    {
        using var face = Face();
        var (sim, attacker, defender, center) = (face.Sim, face.Attacker, face.Defender, face.Center);
        // Точка удара — на краю круга со стороны пуска: ракета входит в круг почти над ней.
        var toX = (int)Math.Floor(center.X - Intercept.Radius + 2);
        var toY = (int)Math.Floor(center.Y);
        var near = sim.PutBuilding("wall", toX, toY + 1, 2);
        Fire(sim, attacker.Building, toX, toY);
        long entered = -1, downed = -1;
        double lift = -1;
        for (var i = 0; i < 1200 && downed < 0; i++)
        {
            foreach (var blast in sim.With(Names.Blast.Name))
                lift = sim.Num(blast, "Blast.lift");
            var found = sim.Missiles();
            if (found.Count == 0)
                downed = (long)sim.Tick;
            else if (entered < 0 && Sim.Distance((sim.Num(found[0], "Missile.x"), sim.Num(found[0], "Missile.y")), center) <= Intercept.Radius)
                entered = (long)sim.Tick;
            sim.Advance();
        }
        Assert.True(entered > 0);
        Assert.True(downed - entered < Math.Round(0.5 / Pit.Tick));
        sim.Advance();
        foreach (var blast in sim.With(Names.Blast.Name))
            lift = sim.Num(blast, "Blast.lift");
        // Над самой целью ракета уже снижается: сбитая гибнет ниже высоты полёта.
        Assert.True(lift > 0);
        Assert.True(lift < MissileRules.Cruise);
        Assert.Equal(1, sim.HealthOf(near));
        Assert.Equal(0, Missiles(sim, defender.Building));
    }

    [Fact(DisplayName = "из двух перехватчиков, до которых ракета достаёт, стреляет ближний")]
    public void NearestInterceptorShoots()
    {
        // Шахта — в кругах обоих перехватчиков: ракета в них с первого тика.
        using var face = Face(40);
        var (sim, attacker, defender, center) = (face.Sim, face.Attacker, face.Defender, face.Center);
        var second = Pit.Base(sim, 2, "interceptor", defender.X + 30, defender.Y, Shelves);
        sim.Set(second.Building, "Interceptor.missiles", 1);
        var guards = new[] { defender, second }.Select(guard => (Guard: guard, X: guard.X + 11.0, Y: guard.Y + 1.0)).ToList();
        var silo = (X: attacker.X + 11.5, Y: attacker.Y + 1.5);
        foreach (var guard in guards)
            Assert.True(Sim.Distance((guard.X, guard.Y), silo) < Intercept.Radius);
        var ordered = guards.OrderBy(guard => Sim.Distance((guard.X, guard.Y), silo)).ToList();
        var (near, far) = (ordered[0], ordered[1]);
        Fire(sim, attacker.Building, (int)center.X, (int)center.Y);
        sim.Seconds(30);
        Assert.Empty(sim.Missiles());
        Assert.Equal(0, Missiles(sim, near.Guard.Building));
        Assert.Equal(1, Missiles(sim, far.Guard.Building));
    }

    [Fact(DisplayName = "не стреляет по ракете, чья точка удара вне круга, по своей и по союзной")]
    public void SkipsOutsideOwnAndAllied()
    {
        // Цель за кругом, хотя курс идёт через него.
        using var passing = Face(160);
        Fire(passing.Sim, passing.Attacker.Building, (int)Math.Floor(passing.Center.X + Intercept.Radius + 30), (int)Math.Floor(passing.Center.Y));
        passing.Sim.Seconds(30);
        Assert.Empty(passing.Sim.Chasers());
        Assert.Equal(1, Missiles(passing.Sim, passing.Defender.Building));

        // Союзник.
        using var friends = Face();
        // Союз — приказами ally раздела игроков.
        friends.Sim.Ally(1, 2);
        Fire(friends.Sim, friends.Attacker.Building, (int)(friends.Center.X + 20), (int)friends.Center.Y);
        friends.Sim.Seconds(40);
        Assert.Equal(1, Missiles(friends.Sim, friends.Defender.Building));
        Assert.Empty(friends.Sim.Missiles());
    }

    /// <summary>Залп двух шахт игрока 1 по базе перехватчика с loaded ракетами (salvo).</summary>
    private static Standoff Salvo(int loaded)
    {
        var found = Face(260, loaded);
        var (sim, attacker, center) = (found.Sim, found.Attacker, found.Center);
        var second = Pit.Base(sim, 1, "silo", attacker.X, attacker.Y + 30, Shelves);
        sim.Set(second.Building, "Silo.ready", 1);
        sim.Send(1, new LaunchOrder { Silo = attacker.Building, X = (int)center.X - 2, Y = (int)center.Y });
        sim.Send(1, new LaunchOrder { Silo = second.Building, X = (int)center.X, Y = (int)center.Y });
        sim.Seconds(Warhead.Launch + 0.1);
        Assert.Equal(2, sim.Missiles().Count);
        sim.Seconds(40);
        Assert.Empty(sim.Missiles());
        return found;
    }

    [Fact(DisplayName = "одна ракета — одна сбитая: при одной ракете вторая из залпа долетает")]
    public void OneMissileOneKill()
    {
        using var found = Salvo(1);
        // Вторая ракета снесла базу вместе с самим перехватчиком.
        Assert.True(Pit.Health(found.Sim, found.Defender.Core) < 1);
    }

    [Fact(DisplayName = "с запасом ракет перехватчик сбивает весь залп: по ракете на каждую")]
    public void StockedInterceptorDownsTheSalvo()
    {
        using var found = Salvo(4);
        Assert.Equal(1, found.Sim.HealthOf(found.Defender.Core));
        Assert.Equal(2, Missiles(found.Sim, found.Defender.Building));
    }

    [Fact(DisplayName = "заряжен ли перехватчик, чужим не видно; ракета-перехватчик видна")]
    public void LoadIsHiddenFromOthers()
    {
        using var face = Face();
        var (sim, attacker, defender, center) = (face.Sim, face.Attacker, face.Defender, face.Center);
        var component = sim.Component(Names.Interceptor.Name);
        Assert.False(new Kharos.Host.VisionView(sim.Host, 1).Shows(sim.World, defender.Building, component));
        Assert.True(new Kharos.Host.VisionView(sim.Host, 2).Shows(sim.World, defender.Building, component));
        Fire(sim, attacker.Building, (int)center.X, (int)center.Y);
        for (var i = 0; i < 1200 && sim.Chasers().Count == 0; i++)
            sim.Advance();
        var chasers = sim.Chasers();
        Assert.NotEmpty(chasers);
        Assert.True(sim.Num(chasers[0], "Chaser.arrive") > sim.Tick - 1);
        // Ракету-перехватчика видит тот, чей он.
        Assert.True(sim.ShownTo(2, chasers[0]));
    }

    [Fact(DisplayName = "перехватчик открывает радар, без своей темы")]
    public void InterceptorIsUnlockedByRadar()
    {
        var sim = Pit.Create(new { protection = 0, techTree = true });
        using var _sim = sim;
        Assert.False(sim.Unlocked(1, "interceptor"));
        Pit.Base(sim, 1, "radar", 0, 0, Shelves);
        Assert.True(sim.Unlocked(1, "interceptor"));
    }

    [Fact(DisplayName = "сборка ракеты и перехват в полёте переживают сохранение")]
    public void AssemblyAndInterceptionSurviveSave()
    {
        using var face = Face();
        var (sim, attacker, center) = (face.Sim, face.Attacker, face.Center);
        Fire(sim, attacker.Building, (int)center.X, (int)center.Y);
        for (var i = 0; i < 1200 && sim.Chasers().Count == 0; i++)
            sim.Advance();
        var missile = sim.Missiles()[0];
        var hunted = sim.Num(missile, "Missile.hunted");
        Assert.True(hunted > sim.Tick - 1);
        using var loaded = sim.ThroughSave();
        Assert.Equal(hunted, loaded.Num(missile, "Missile.hunted"));
        loaded.Seconds(5);
        Assert.Empty(loaded.Missiles());
    }
}
