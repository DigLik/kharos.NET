using Kharos.Engine.Terrain;

namespace Kharos.Engine.Tests.Movement;

/// <summary>
/// cliffs.test.ts, ход: подножие обрыва, пехота на плато, место появления. Правила места и покрытия у обрыва — мод base:
/// Kharos.Base.Tests/Sim/Construction/CliffsTests.
/// </summary>
public sealed class CliffsTests
{
    /// <summary>Карта 256² с ровной скалой первого яруса 60×60 вокруг начала.</summary>
    private static MoveWorld World()
    {
        var world = new MoveWorld(256);
        Flat(world, -30, -30, 60);
        return world;
    }

    private static void Flat(MoveWorld world, int left, int top, int side)
    {
        world.Land.Batch(() =>
        {
            for (var y = top; y < top + side; y++)
                for (var x = left; x < left + side; x++)
                    world.SetTile(x, y, TerrainType.Rock, 1, false);
        });
    }

    /// <summary>Плато второго яруса с обрывом по всему краю.</summary>
    private static void Mesa(MoveWorld world, int left, int top, int side)
    {
        world.Land.Batch(() =>
        {
            for (var y = top; y < top + side; y++)
                for (var x = left; x < left + side; x++)
                    world.SetTile(x, y, TerrainType.Rock, 2, true);
        });
    }

    private static double Speed(MoveWorld world, string type, int x, int y)
    {
        var spec = MoveWorld.Spec(type);
        return world.Nav.TerrainSpeed(world.Nav.Chassis.Of(spec.Chassis), spec.Radius, x, y);
    }

    [Fact(DisplayName = "подножие обрыва — нижние тайлы у стенки; технике туда нельзя, пехоте можно, но медленно; верх — свободен")]
    public void CliffFoot()
    {
        var world = World();
        Mesa(world, 0, 0, 10);
        Assert.True(world.Nav.IsCliffFoot(-1, 5));
        Assert.True(world.Nav.IsCliffFoot(5, 10));
        Assert.True(world.Nav.IsCliffFoot(10, 10));
        Assert.False(world.Nav.IsCliffFoot(0, 5));
        Assert.False(world.Nav.IsCliffFoot(-2, 5));
        Assert.True(world.Nav.IsWalkable(0, 5));
        Assert.False(world.Nav.IsWalkable(5, 10));
        Assert.True(world.Nav.IsWalkable(5, 10, true));
        Assert.True(Speed(world, "infantry", 5, 10) < Speed(world, "infantry", 5, 12));
        Assert.Equal(1.0, Speed(world, "buggy", 5, 12));
    }

    [Fact(DisplayName = "пологий край (без обрыва) проходим всем")]
    public void Gentle()
    {
        var world = World();
        world.Land.Batch(() =>
        {
            for (var y = 0; y < 10; y++)
                for (var x = 0; x < 10; x++)
                    world.SetTile(x, y, TerrainType.Rock, 2, false);
        });
        Assert.False(world.Nav.IsCliffFoot(-1, 5));
        Assert.True(world.Nav.IsWalkable(-1, 5));
    }

    [Fact(DisplayName = "на плато за обрывом пехота забирается, а техника — нет")]
    public void InfantryClimbs()
    {
        var world = World();
        Mesa(world, 0, 0, 10);
        var tank = world.Spawn("tank", 1, -10, 5);
        var soldier = world.Spawn("infantry", 1, -10, 6);
        world.Nav.Order(tank, 5, 5);
        world.Nav.Order(soldier, 5, 6);
        world.Seconds(30);
        Assert.Equal((5, 6), world.TileOf(soldier));
        // Танк встаёт у подножия: на плато он не заезжает.
        var (x, y) = world.At(tank);
        Assert.False(x >= 0 && x < 10 && y >= 0 && y < 10);
    }

    [Fact(DisplayName = "место появления не запирается обрывами")]
    public void OpenSpawn()
    {
        var world = World();
        // Карман первого яруса посреди плато: со всех сторон обрыв, технике не выбраться.
        Mesa(world, -20, -20, 40);
        Flat(world, -3, -3, 6);
        Assert.False(world.Nav.NotWalledIn(0, 0));
        var (x, y) = world.Nav.OpenSpawn(0, 0);
        Assert.True(world.Nav.NotWalledIn(x, y));
        Assert.True(Math.Max(Math.Abs(x), Math.Abs(y)) > 3);
    }
}
