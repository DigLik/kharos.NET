using Kharos.Engine.Movement;
using Kharos.Engine.Numerics;
using Kharos.Engine.World;

namespace Kharos.Engine.Tests.Movement;

/// <summary>followCarriers (turrets.ts) и занятость тайлов (createOccupancy в buildings.ts).</summary>
public sealed class MountTests
{
    private static uint Mounted(MoveWorld world, uint parent, double along, double across, double angle = 0, bool facing = true)
    {
        var turret = world.World.Spawn();
        world.Set(turret, Builtin.Position, ("x", 0), ("y", 0));
        if (facing)
            world.Set(turret, Builtin.Facing, ("angle", 0));
        world.Set(turret, Builtin.Mount, ("parent", parent), ("along", along), ("across", across), ("angle", angle));
        return turret;
    }

    [Fact(DisplayName = "турель едет на своём месте на юните и поворачивается вместе с ним")]
    public void TurretOnUnit()
    {
        var world = new MoveWorld(256);
        var (x, y) = world.Plateau(20, 5);
        var tank = world.Spawn("tank", 1, x + 1, y + 2);
        var turret = Mounted(world, tank, 0.3, -0.1, 0.25);
        world.Nav.Order(tank, x + 15, y + 2);
        for (var i = 0; i < 40; i++)
        {
            world.Step();
            var (cx, cy) = world.At(tank);
            var facing = world.Facing(tank);
            Assert.Equal((cx + 0.3 * DetMath.Cos(facing) - -0.1 * DetMath.Sin(facing), cy + 0.3 * DetMath.Sin(facing) + -0.1 * DetMath.Cos(facing)), world.At(turret));
            Assert.Equal(Navigation.Wrap(facing + 0.25), world.Facing(turret));
        }
    }

    [Fact(DisplayName = "турель на здании стоит от середины основания, а с носителем уходит и она")]
    public void TurretOnBuildingAndOrphans()
    {
        var world = new MoveWorld(256);
        var building = world.Building(4, 6, 2, 3);
        var turret = Mounted(world, building, 0.5, 0, 1, facing: false);
        var free = Mounted(world, 0, 1, 1);
        var nested = Mounted(world, turret, 0, 0);
        world.Step();
        Assert.Equal((5.5, 7.5), world.At(turret));
        Assert.Equal((0.0, 0.0), world.At(free));
        world.World.Destroy(building);
        world.Step();
        Assert.False(world.World.Alive(turret));
        Assert.False(world.World.Alive(nested));
        Assert.True(world.World.Alive(free));
    }

    [Fact(DisplayName = "занятость тайлов: здание занимает основание, площадка — нет, открытые ворота проходимы")]
    public void Occupancy()
    {
        var world = new MoveWorld(256, Kharos.Engine.Terrain.GeneratorConfig.Default with { Blank = true });
        var building = world.Building(2, 3, 3, 2);
        var site = world.Building(10, 10, 2, 2, solid: false);
        Assert.Equal(building, world.Nav.Occupancy.At(4, 4));
        Assert.Equal(0u, world.Nav.Occupancy.At(5, 4));
        Assert.Equal(0u, world.Nav.Occupancy.At(10, 10));
        Assert.Equal(site, world.Nav.Occupancy.SiteAt(11, 11));
        Assert.False(world.Nav.IsWalkable(3, 3));
        // Ворота открылись — через них ходят; переехало здание — тайлы следом.
        world.World.Set(building, Builtin.Footprint, "open", true);
        Assert.True(world.Nav.IsWalkable(3, 3));
        world.World.SetNumber(building, Builtin.Position, 0, 20);
        Assert.Equal(0u, world.Nav.Occupancy.At(2, 3));
        Assert.Equal(building, world.Nav.Occupancy.At(22, 4));
        // Стройка началась — площадка заняла тайлы; здание снесли — освободило.
        world.World.Set(site, Builtin.Footprint, "solid", true);
        // Флаг сам по себе ревизий не двигает: занятость сверяется при смене состава или мест.
        world.World.Add(site, Builtin.Sight);
        Assert.Equal(site, world.Nav.Occupancy.At(11, 11));
        world.World.Destroy(building);
        Assert.Equal(0u, world.Nav.Occupancy.At(22, 4));
        Assert.Equal(1, world.Nav.Occupancy.Count);
    }
}
