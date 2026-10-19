using Base.Shared;
using Base.Shared.Orders;
using static Kharos.Base.Tests.Combat.Arena;

namespace Kharos.Base.Tests.Combat;

/// <summary>Бой: цели, погоня, снаряды, турели, летающие, разрушение (packages/tests/combat.test.ts, часть 1).</summary>
public sealed partial class CombatTests
{
    [Fact(DisplayName = "пехотинцы сами стреляют во врага в пределах выстрела: пули летят, цель гибнет и взрывается")]
    public void InfantryShootsOnItsOwn()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var mine = Spawn(sim, "infantry", 1, x, y);
        var foe = Spawn(sim, "builder", 2, x + 3, y);
        UntilShot(sim);
        Assert.Equal(foe, sim.TargetOf(mine));
        sim.Seconds(1);
        Assert.True(Health(sim, foe) < 1);
        // Стрелок повернулся к цели и с места не сошёл.
        Assert.True(Math.Abs(Facing(sim, mine)) < 0.2);
        Assert.Equal((x + 0.5, y + 0.5), sim.At(mine));

        sim.Seconds(40);
        Assert.False(sim.Alive(foe));
        Assert.Equal(0u, sim.TargetOf(mine));
        // Снаряды и взрывы не копятся.
        sim.Seconds(2);
        Assert.Equal(0, sim.Count(Names.Shot.Name));
        Assert.Equal(0, sim.Count(Names.Blast.Name));
    }

    [Fact(DisplayName = "до врага вне дальности юнит сам не идёт; своих и ничьих не трогает")]
    public void DoesNotWalkToFarEnemies()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var mine = Spawn(sim, "infantry", 1, x, y);
        var far = Spawn(sim, "builder", 2, x + 8, y);
        var friend = Spawn(sim, "builder", 1, x + 2, y);
        var nobody = Spawn(sim, "builder", 0, x + 2, y + 2);
        sim.Seconds(3);
        Assert.Equal(0u, sim.TargetOf(mine));
        Assert.False(sim.Has(mine, Names.Path.Name));
        foreach (var entity in new[] { far, friend, nobody })
            Assert.Equal(1, Health(sim, entity));
        Assert.True(sim.CanAttack(1, far));
        Assert.False(sim.CanAttack(1, friend));
        Assert.False(sim.CanAttack(1, nobody));
    }

    [Fact(DisplayName = "по приказу юнит гонится за целью и бьёт её; приказ идти снимает цель")]
    public void ChasesOnOrder()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var mine = Spawn(sim, "infantry", 1, x, y);
        var foe = Spawn(sim, "truck", 2, x + 12, y);
        sim.Send(1, new AttackOrder { Units = [mine, foe], Target = foe });
        sim.Seconds(1);
        Assert.True(sim.Has(mine, Names.Path.Name));
        Assert.Equal(foe, sim.TargetOf(mine));
        Assert.Equal(1, sim.Num(mine, "Armed.chase"));
        sim.Seconds(5);
        // Подошёл на выстрел и встал.
        Assert.False(sim.Has(mine, Names.Path.Name));
        Assert.True(Health(sim, foe) < 1);

        sim.Send(1, new MoveOrder { Units = [mine], X = x, Y = y });
        sim.Seconds(1);
        Assert.Equal(0u, sim.TargetOf(mine));
        Assert.Equal(0, sim.Num(mine, "Armed.chase"));
        var left = Health(sim, foe);
        sim.Seconds(6);
        Assert.Equal(left, Health(sim, foe));

        // Своих и чужими юнитами атаковать нельзя.
        var friend = Spawn(sim, "builder", 1, x + 1, y + 2);
        sim.Send(1, new AttackOrder { Units = [mine], Target = friend });
        sim.Send(2, new AttackOrder { Units = [mine], Target = friend });
        sim.Seconds(3);
        Assert.Equal(1, Health(sim, friend));
    }

    [Fact(DisplayName = "попавший под огонь издалека отвечает: идёт к стрелку")]
    public void AnswersFireFromAfar()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var tank = Spawn(sim, "tank", 1, x, y);
        var foe = Spawn(sim, "infantry", 2, x + 7, y);
        Assert.True(Weapon("cannon").Range > Weapon("rifle").Range + 2);
        sim.Seconds(2.5);
        Assert.True(Health(sim, foe) < 1);
        Assert.Equal(tank, sim.TargetOf(foe));
        Assert.Equal(1, sim.Num(foe, "Armed.chase"));
        Assert.True(sim.At(foe).X < x + 7);
    }

    [Fact(DisplayName = "ядро бьёт по площади и падает туда, где цель была при выстреле")]
    public void ShellSplashes()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        Spawn(sim, "tank", 1, x, y);
        var target = Spawn(sim, "truck", 2, x + 5, y);
        var near = Spawn(sim, "truck", 2, x + 5, y + 1);
        var far = Spawn(sim, "truck", 2, x + 5, y + 3);
        // Ядро летит не мгновенно: пока оно в воздухе, урона нет.
        UntilShot(sim);
        sim.Seconds(0.2);
        Assert.Equal(1, sim.Count(Names.Shot.Name));
        Assert.Equal(1, Health(sim, target));
        var blasted = false;
        for (var i = 0; i < 20; i++)
        {
            sim.Advance();
            blasted |= sim.Count(Names.Blast.Name) > 0;
        }
        Assert.True(Health(sim, target) < 1);
        Assert.True(Health(sim, near) < 1);
        Assert.True(Health(sim, near) > Health(sim, target));
        Assert.Equal(1, Health(sim, far));
        Assert.True(blasted);
    }

    [Fact(DisplayName = "танк наводит башню, а не корпус, стреляет на ходу, а без цели ровняет башню по корпусу", Skip = "ждёт С5b: restTurrets — без цели башня ровняется по корпусу")]
    public void TankAimsTurret()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var tank = Spawn(sim, "tank", 1, x + 5, y);
        var tower = sim.TurretsOf(tank)[0];
        // Враг сбоку: поворачивается башня, корпус смотрит, куда смотрел.
        var facing = Facing(sim, tank);
        var foe = Spawn(sim, "truck", 2, x + 10, y);
        UntilShot(sim);
        Assert.True(Math.Abs(facing) > 1);
        Assert.Equal(facing, Facing(sim, tank));
        // Поворот башни — относительно корпуса: она смотрит вправо, корпус — вниз.
        Assert.True(Math.Abs(Wrap(sim.Num(tower, "Mount.angle") + facing)) < 0.13);
        Assert.Equal(foe, sim.TargetOf(tower));

        // Едет по приказу — и стреляет на ходу, не останавливаясь.
        while (sim.Num(tower, "Armed.cooldown") > 3)
            sim.Advance();
        sim.Send(1, new MoveOrder { Units = [tank], X = x, Y = y });
        var fired = false;
        for (var i = 0; i < 60 && !fired; i++)
        {
            var before = sim.Count(Names.Shot.Name);
            sim.Advance();
            fired |= sim.Has(tank, Names.Path.Name) && sim.Count(Names.Shot.Name) > before;
        }
        Assert.True(fired);

        // Цели нет — башня поворачивается туда же, куда корпус.
        sim.Erase(foe);
        sim.Seconds(4);
        Assert.Equal(0u, sim.TargetOf(tower));
        Assert.Equal(0, sim.Num(tower, "Mount.angle"));
    }

    [Fact(DisplayName = "турели носителя, попавшего под огонь издалека, отвечают: носитель едет к стрелку")]
    public void CarrierTurretsAnswer()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        Assert.True(Weapon("cannon").Range > Weapon("launcher").Range);
        var carrier = Spawn(sim, "carrier", 1, x, y);
        var tank = Spawn(sim, "tank", 2, x + 7, y);
        // Пушка до носителя достаёт, а ракеты до танка — нет.
        sim.Set(tank, "Position.x", x + 8.1);
        for (var i = 0; i < 200 && Health(sim, carrier) == 1; i++)
            sim.Advance();
        sim.Advance();
        var rockets = sim.TurretsOf(carrier).Where(turret => sim.Has(turret, Names.Armed.Name)).ToList();
        foreach (var turret in rockets)
        {
            Assert.Equal(tank, sim.TargetOf(turret));
            Assert.Equal(1, sim.Num(turret, "Armed.chase"));
        }
        sim.Seconds(1);
        Assert.True(sim.At(carrier).X > x + 0.5);
    }

    [Fact(DisplayName = "разряд перескакивает на соседей, слабея")]
    public void ArcJumps()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var tesla = Spawn(sim, "tesla", 1, x, y);
        var facing = Facing(sim, tesla);
        var foes = new[] { 3, 5, 7, 9 }.Select(dx => Spawn(sim, "truck", 2, x + dx, y)).ToArray();
        sim.Seconds(1.5);
        // Наводится катушка-турель, а не корпус.
        Assert.Equal(facing, Facing(sim, tesla));
        var lost = foes.Select(entity => 1 - Health(sim, entity)).ToArray();
        Assert.True(lost[0] > lost[1]);
        Assert.True(lost[1] > lost[2]);
        Assert.True(lost[2] > 0);
        // Прыжков всего два.
        Assert.Equal(0, lost[3]);
    }

    [Fact(DisplayName = "лазер попадает сразу, оставляя след")]
    public void LaserHitsAtOnce()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        Spawn(sim, "lancer", 1, x, y);
        var foe = Spawn(sim, "truck", 2, x + 4, y);
        for (var i = 0; i < 40 && Health(sim, foe) == 1; i++)
            sim.Advance();
        var traced = sim.With(Names.Shot.Name).Any(shot => WeaponOf(sim, shot) == "laser" && sim.Num(shot, "Shot.toX") == x + 4.5);
        Assert.True(Health(sim, foe) < 1);
        Assert.True(traced);
    }

    [Fact(DisplayName = "летающие летят по прямой над зданиями, а пушка до них не достаёт")]
    public void FlyersFlyStraight()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var drone = Spawn(sim, "drone", 1, x, y);
        // Стена из зданий поперёк пути, если здесь есть скала; летающему всё равно.
        for (var dy = -3; dy <= 3; dy += 2)
            if (Kernel.Scene.CanPlace(sim, "generator", x + 5, y + dy))
                Place(sim, "generator", x + 5, y + dy, 0);
        sim.Send(1, new MoveOrder { Units = [drone], X = x + 12, Y = y });
        sim.Advance();
        Assert.Equal([x + 12.5, y + 0.5], Points(sim, drone));
        sim.Seconds(3);
        Assert.Equal((x + 12.5, y + 0.5), sim.At(drone));

        var tank = Spawn(sim, "tank", 2, x + 15, y);
        sim.Seconds(5);
        Assert.Equal(1, Health(sim, drone));
        Assert.Equal(0u, GunTarget(sim, tank));
        // А дрон по нему стреляет.
        Assert.True(Health(sim, tank) < 1);
        sim.Send(2, new AttackOrder { Units = [tank], Target = drone });
        sim.Seconds(1);
        Assert.Equal(0u, GunTarget(sim, tank));

        // Разрядник достаёт: разряд бьёт и по воздуху, хоть и вчетверо слабее.
        Spawn(sim, "tesla", 2, x + 15, y + 2);
        sim.Seconds(5);
        // Каждый разряд снимает с дрона четверть обычного урона.
        var shots = (1 - Health(sim, drone)) * Unit("drone").Hp / (Weapon("arc").Damage * 0.25);
        Assert.True(shots > 0.5);
        Assert.Equal(Math.Round(shots), shots, 5);
        sim.Seconds(20);
        Assert.False(sim.Alive(drone));
    }

    [Fact(DisplayName = "летающие не занимают землю: наземный проходит под ними")]
    public void FlyersDoNotTakeGround()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        Spawn(sim, "gunship", 1, x + 3, y);
        var walker = Spawn(sim, "infantry", 1, x, y);
        sim.Send(1, new MoveOrder { Units = [walker], X = x + 3, Y = y });
        sim.Seconds(3);
        Assert.Equal((x + 3.5, y + 0.5), sim.At(walker));
    }

    [Fact(DisplayName = "здания разрушаются: прочность считается от цены, занятые тайлы освобождаются")]
    public void BuildingsAreDestroyed()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var tank = Spawn(sim, "tank", 1, x, y);
        var (spotX, spotY) = (x + 4, y - 1);
        var building = Place(sim, "generator", spotX, spotY, 2);
        Assert.Equal(600, BuildingHp("generator"));
        Assert.Equal(building, sim.OccupantAt(spotX, spotY));
        sim.Seconds(4);
        Assert.Equal(building, GunTarget(sim, tank));
        var left = Health(sim, building);
        Assert.True(left < 1);
        Assert.True(left > 0.5);
        sim.Seconds(40);
        Assert.False(sim.Alive(building));
        Assert.Equal(0u, sim.OccupantAt(spotX, spotY));
    }

    [Fact(DisplayName = "бой переживает сохранение")]
    public void BattleSurvivesSave()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        Spawn(sim, "tank", 1, x, y);
        var foe = Spawn(sim, "tank", 2, x + 6, y);
        UntilShot(sim);
        using var copy = sim.ThroughSave();
        copy.Seconds(3);
        Assert.True(copy.HealthOf(foe) < 1);
        Assert.True(Unit("tank").Hp > Weapon("cannon").Damage * 2);
    }

    [Fact(DisplayName = "гонящийся идёт на выстрел от цели, а не в неё саму")]
    public void ChaserGoesToRange()
    {
        using var sim = Create();
        var tank = Spawn(sim, "tank", 1, 0, 0);
        var target = Spawn(sim, "tank", 2, 30, 0);
        sim.Send(1, new AttackOrder { Units = [tank], Target = target });
        sim.Seconds(1);
        // Путь ведёт на его место у цели: оно на выстреле, а не в самой цели.
        Assert.Equal(1, sim.Num(tank, "Path.slot"));
        var points = Points(sim, tank);
        var reach = Math.Sqrt(Math.Pow(points[^2] - 30.5, 2) + Math.Pow(points[^1] - 0.5, 2));
        Assert.True(reach <= Weapon("cannon").Range);
        Assert.True(reach > 2);
    }

    [Fact(DisplayName = "снаряд, долетевший до уже сбитого летающего, не рвётся на земле")]
    public void ShotAtDeadFlyerDoesNotScar()
    {
        var (sim, x, y) = Field();
        using var _sim = sim;
        var drone = Spawn(sim, "drone", 2, x, y);
        sim.SetStance("passive", drone);
        Spawn(sim, "rocketeer", 1, x + 4, y);
        UntilShot(sim);
        sim.Erase(drone);
        sim.Seconds(2);
        Assert.Equal(0, sim.Count(Names.Shot.Name));
        Assert.Empty(Traces(sim, 1));
    }

    /// <summary>Точки пути (Path.points, f64 x, y подряд).</summary>
    internal static double[] Points(Sim sim, uint entity)
    {
        var bytes = sim.Bytes(entity, "Path.points");
        var points = new double[bytes.Length / 8];
        for (var i = 0; i < points.Length; i++)
            points[i] = BitConverter.ToDouble(bytes, i * 8);
        return points;
    }

    /// <summary>Следы вида kind (0 колея, 1 гарь, 2 прожог, 3 остов) по всему миру.</summary>
    internal static List<(double X, double Y, double[] Fields)> Traces(Sim sim, int kind)
    {
        var list = new List<(double, double, double[])>();
        var id = sim.Host.Layers.Find("traces");
        if (id is not { } layer)
            return list;
        foreach (var cell in sim.Host.Layers.Cells(layer, Kharos.Engine.Layers.LayerOwner.World))
            foreach (var item in sim.Host.Layers.Items(layer, Kharos.Engine.Layers.LayerOwner.World, cell))
                if ((int)item.Fields[2] == kind)
                    list.Add((item.X, item.Y, item.Fields));
        return list;
    }
}
