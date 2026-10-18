using Kharos.Engine.Layers;
using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Host.Tests;

/// <summary>Обзор (vision.rs): круг глаза, гаснущее видимое, разведанное, слои игрока.</summary>
public class VisionTests
{
    private static (GameWorld World, uint Eye) World(uint player, double x, double y, double radius)
    {
        var world = new GameWorld(new Schema());
        var eye = world.Spawn();
        world.Add(eye, Builtin.Owner);
        world.SetNumber(eye, Builtin.Owner, 0, player);
        world.Add(eye, Builtin.Position);
        world.SetNumber(eye, Builtin.Position, 0, x);
        world.SetNumber(eye, Builtin.Position, 1, y);
        world.Add(eye, Builtin.Sight);
        world.SetNumber(eye, Builtin.Sight, 0, radius);
        return (world, eye);
    }

    [Fact]
    public void SightLightsCircleAndLeavesExploredBehind()
    {
        var (world, eye) = World(1, 0.5, 0.5, 2);
        var layers = new LayerSet(Fog.Layers);
        layers.AddPlayer(1);
        var vision = new Vision(new MapBounds(-8, -8, 8, 8), layers);
        vision.AddPlayer(1);
        vision.Update(world);

        Assert.True(vision.Sees(1, 2.5, 0.5));
        Assert.False(vision.Sees(1, 3.5, 0.5));
        Assert.False(vision.Sees(2, 0.5, 0.5));
        // Круг как в vision.rs: в ряду — от ⌊x − s⌋ до ⌊x + s⌋, s — полухорда на середине ряда.
        for (var ty = -8; ty < 8; ty++)
            for (var tx = -8; tx < 8; tx++)
            {
                var dy = ty + 0.5 - 0.5;
                var span = Math.Sqrt(Math.Max(0, 4 - dy * dy));
                var inside = Math.Abs(dy) <= 2 && tx >= Math.Floor(0.5 - span) && tx <= Math.Floor(0.5 + span);
                Assert.Equal(inside, vision.Sees(1, tx + 0.5, ty + 0.5));
                Assert.Equal(inside ? 1.0 : 0.0, layers.Get(layers.Id(Fog.Sight), LayerOwner.Player(1), tx, ty, 0));
            }

        world.SetNumber(eye, Builtin.Position, 0, 5.5);
        vision.Update(world);
        Assert.False(vision.Sees(1, 0.5, 0.5));
        Assert.True(vision.Explored(1, 0.5, 0.5));
        Assert.True(vision.Sees(1, 5.5, 0.5));
        Assert.Equal(2, vision.Changed(1));
        Assert.Equal(0.0, layers.Get(layers.Id(Fog.Sight), LayerOwner.Player(1), 0, 0, 0));
        Assert.Equal(1.0, layers.Get(layers.Id(Fog.Explored), LayerOwner.Player(1), 0, 0, 0));

        // Ничего не сдвинулось — обзор тот же, изменений слоёв нет.
        layers.TakeChanges();
        vision.Update(world);
        Assert.Equal(2, vision.Changed(1));
        Assert.Empty(layers.TakeChanges());
    }

    [Fact]
    public void EyeWithoutOwnerOrOffMapLightsNothing()
    {
        var (world, eye) = World(0, 0.5, 0.5, 3);
        var vision = new Vision(new MapBounds(-4, -4, 4, 4), null);
        vision.AddPlayer(1);
        vision.Update(world);
        Assert.False(vision.Explored(1, 0.5, 0.5));

        world.SetNumber(eye, Builtin.Owner, 0, 1);
        world.SetNumber(eye, Builtin.Position, 0, 100);
        vision.Update(world);
        Assert.True(vision.Cells(1).ToArray().All(cell => cell == 0));

        // Глаз у края: круг обрезан картой.
        world.SetNumber(eye, Builtin.Position, 0, 3.9);
        vision.Update(world);
        Assert.True(vision.Sees(1, 3.5, 0.5));
        Assert.False(vision.Sees(1, 4.5, 0.5));
    }

    [Fact]
    public void HostVisionFollowsUnitsAndReachesReplicaAsFog()
    {
        using var host = HelloGame.Create();
        host.Start();
        var session = new HostSession(host);
        var (hostEnd, clientEnd) = MemoryTransport.Pair();
        session.Connect(hostEnd, 1);
        var replica = new Replica();
        replica.Pump(clientEnd);
        Assert.True(replica.Ready);
        Assert.True(replica.Welcome!.Fog);

        // Обзор игрока сразу после входа: юниты у (0, 4), радиус 6.
        var vision = host.Vision!;
        Assert.True(vision.Sees(1, 0.5, 4.5));
        Assert.False(vision.Sees(1, 50.5, 50.5));
        Assert.Equal(0.0, replica.FogAlpha(0, 4));
        Assert.Equal(Fog.Hidden, replica.FogAlpha(50, 50));
        Assert.Equal(Fog.Hidden, replica.FogAlpha(1000, 0));

        var units = HelloGame.Units(host.World, 1);
        session.Host.Send(1, "hello.move", HelloGame.Move(40, 4, [.. units]));
        for (var tick = 0; tick < 300; tick++)
        {
            session.Step();
            replica.Pump(clientEnd);
            if (tick % 4 == 0)
                for (var y = -10; y < 20; y++)
                    for (var x = -10; x < 50; x++)
                        Assert.Equal((vision.Sees(1, x + 0.5, y + 0.5), vision.Explored(1, x + 0.5, y + 0.5)), (replica.Sees(x, y), replica.Explored(x, y)));
        }
        // Уехали: дом разведан, но не виден, если рядом никого.
        var near = units.Any(e => HelloGame.Position(host.World, e) is var (x, y) && x * x + (y - 4) * (y - 4) < 49);
        if (!near)
            Assert.Equal(Fog.Remembered, replica.FogAlpha(0, 4));
        Assert.True(vision.Changed(1) > 1);
        Assert.True(replica.FogRevision > 1);
    }
}
