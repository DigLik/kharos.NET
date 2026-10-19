using Base.Shared;
using Kharos.Base.Tests.Kernel;
using static Kharos.Base.Tests.Combat.Arena;

namespace Kharos.Base.Tests.Combat;

/// <summary>Оборонительные турели зданий (packages/tests/towers.test.ts): надбавка дальности, патроны, энергия зоны.</summary>
public sealed class TowersTests
{
    /// <summary>Надбавка дальности оружию на здании (TOWER_RANGE).</summary>
    private const double TowerRange = 2;

    private const uint Training = Sim.TrainingPlayer;

    [Fact(DisplayName = "ракетная турель достаёт штурмовика дальше его собственного выстрела")]
    public void RocketTurretOutrangesGunship()
    {
        using var sim = Create();
        var tower = Place(sim, "rocketTurret", 0, 0, 1);
        sim.SetStock(tower, "ammo", 100);
        // Дальше дальности ракетницы штурмовика, но в пределах надбавки турели.
        var away = Weapon("launcher").Range + TowerRange / 2;
        var gunship = Spawn(sim, "gunship", Training, (int)Math.Floor(0.5 + away), 0);
        sim.Seconds(10);
        Assert.True(!sim.Alive(gunship) || Health(sim, gunship) < 1);
    }

    [Fact(DisplayName = "главное здание бьёт по всей своей зоне, и по воздуху тоже, а дальше — нет")]
    public void CoreGunCoversItsZone()
    {
        // Центр главного здания — (1.5, 1.5); его зона — 12 тайлов. Каждую цель — в своём мире: турель бьёт по одной.
        bool Hurt(string type, int x, int y)
        {
            using var sim = Create();
            Place(sim, "command", 0, 0, 1);
            var unit = Spawn(sim, type, Training, x, y);
            sim.Seconds(10);
            return !sim.Alive(unit) || Health(sim, unit) < 1;
        }
        Assert.True(Hurt("buggy", 12, 1));
        Assert.True(Hurt("drone", 1, 12));
        Assert.False(Hurt("buggy", -14, 1));
    }

    [Fact(DisplayName = "лазерная турель бьёт танк без всяких патронов, на дальности лазера с надбавкой здания, — но только на энергии зоны")]
    public void LaserTurretNeedsPower()
    {
        bool Hurt(bool powered)
        {
            using var sim = Create();
            // Главное здание даёт зону, но стоит так, чтобы его собственная пушка до танка не доставала.
            Place(sim, "command", 0, 10, 1);
            if (powered)
                Place(sim, "generator", 4, 10, 1);
            var tower = Place(sim, "laserTurret", 0, 0, 1);
            Assert.False(sim.Has(tower, Names.Stock.Name));
            var tank = Spawn(sim, "tank", Training, 9, 0);
            sim.Seconds(5);
            return !sim.Alive(tank) || Health(sim, tank) < 1;
        }
        Assert.True(Hurt(true));
        // Без электростанции лазеру стрелять нечем.
        Assert.False(Hurt(false));
    }

    [Fact(DisplayName = "разрядная турель бьёт и землю, и воздух, и тоже только на энергии зоны")]
    public void ArcTurretNeedsPower()
    {
        bool Hurt(string type, bool powered)
        {
            using var sim = Create();
            Place(sim, "command", 0, 10, 1);
            if (powered)
                Place(sim, "generator", 4, 10, 1);
            var tower = Place(sim, "arcTurret", 0, 0, 1);
            Assert.False(sim.Has(tower, Names.Stock.Name));
            // Цель — по другую сторону от главного здания: его пушка туда не достаёт.
            var unit = Spawn(sim, type, Training, 0, -(int)Math.Floor(Weapon("arc").Range + TowerRange / 2));
            sim.SetStance("passive", unit);
            sim.Seconds(5);
            return !sim.Alive(unit) || Health(sim, unit) < 1;
        }
        Assert.True(Hurt("buggy", true));
        Assert.True(Hurt("drone", true));
        Assert.False(Hurt("buggy", false));
    }
}
