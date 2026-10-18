using Kharos.Engine.Numerics;

namespace Kharos.Engine.Tests.Movement;

/// <summary>army-paths.test.ts: армия едет напрямую, путь ищется, только когда упрётся.</summary>
public sealed class ArmyPathsTests
{
    [Fact(DisplayName = "большая армия сразу трогается, каждый юнит — к своему месту, и доходит")]
    public void ArmyMoves()
    {
        var world = new MoveWorld();
        var units = Enumerable.Range(0, 400).Select(i => world.Spawn(i % 2 == 1 ? "infantry" : "buggy", 1, -40 + i % 20, -10 + i / 20)).ToArray();
        world.Nav.OrderGroup(units, 40, 0);
        var goals = new HashSet<(double, double)>();
        foreach (var entity in units)
        {
            if (!world.HasPath(entity))
                continue;
            goals.Add((world.PathNumber(entity, "goalX"), world.PathNumber(entity, "goalY")));
            // Пути приказ не ищет: юниты едут напрямую.
            Assert.Equal(1, world.PathNumber(entity, "direct"));
        }
        // Почти у всех есть путь, и места не повторяются: армии хватает колец вокруг цели.
        Assert.True(goals.Count > 380);
        world.Seconds(60);
        var arrived = units.Count(entity => DetMath.Hypot(world.At(entity).X - 40, world.At(entity).Y) < 30);
        Assert.True(arrived > 360, $"доехали {arrived}");
    }

    [Fact(DisplayName = "упёршись в стену, юнит сразу ищет путь, а не скользит вдоль неё до угла")]
    public void WallMeansPath()
    {
        var world = new MoveWorld();
        (int X, int Y)? at = null;
        for (var y = -150; y < 150 && at is null; y++)
            for (var x = -150; x < 150 && at is null; x++)
            {
                var flat = true;
                for (var dy = -10; dy <= 10 && flat; dy++)
                    for (var dx = -25; dx <= 25 && flat; dx++)
                        flat = world.Flat(x + dx, y + dy);
                if (flat)
                    at = (x, y);
            }
        Assert.NotNull(at);
        var (wx, wy) = at.Value;
        // Стена в 41 секцию поперёк дороги; цель за ней, наискось — юнит упирается и скользит к правому краю.
        for (var dx = -20; dx <= 20; dx++)
            world.Building(wx + dx, wy, 1, 1);
        var buggy = world.Spawn("buggy", 1, wx - 3, wy - 6);
        world.Nav.OrderGroup([buggy], wx + 3, wy + 6);
        Assert.Equal(1, world.PathNumber(buggy, "direct"));
        int planned = -1, arrived = -1;
        for (var tick = 0; tick < 20 * 30 && arrived < 0; tick++)
        {
            world.Step();
            if (planned < 0 && world.HasPath(buggy) && world.PathNumber(buggy, "direct") == 0)
                planned = tick;
            var (px, py) = world.At(buggy);
            if (DetMath.Hypot(px - wx - 3.5, py - wy - 6.5) < 1.5)
                arrived = tick;
        }
        // До стены — около секунды; путь ищется сразу, как упёрся.
        Assert.InRange(planned, 1, 39);
        Assert.InRange(arrived, 1, 219);
    }
}
