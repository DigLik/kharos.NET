using Kharos.Engine.World;

namespace Kharos.Engine.Tests.World;

public sealed class ChangeTrackingTests
{
    [Fact]
    public void StructureAndFieldsAreTracked()
    {
        var world = Fixtures.World();
        world.TrackChanges = true;
        var drive = world.Schema.Id("Drive");
        var changes = new WorldChanges();
        var (a, b) = (world.At(1, 2), world.Spawn());
        world.Add(b, drive);
        world.TakeChanges(changes);
        Assert.Equal([a, b], changes.Spawned);
        Assert.Equal([new ComponentChange(a, Builtin.Position), new ComponentChange(b, drive)], changes.Added);

        world.TakeChanges(changes);
        Assert.True(changes.IsEmpty);

        world.SetNumber(a, Builtin.Position, 1, 2);
        world.Set(b, drive, "speed", 0.0);
        Assert.True(TakeEmpty(world, changes), "то же значение — не изменение");

        world.SetNumber(a, Builtin.Position, 1, 5);
        world.Set(b, drive, "tx", 1.0);
        world.Set(b, drive, "move", "wheels");
        world.Set(b, drive, "tx", 2.0);
        world.Remove(b, drive);
        world.Add(b, drive);
        world.Set(b, drive, "turn", 1.0);
        var c = world.Spawn();
        world.Destroy(c);
        world.TakeChanges(changes);
        Assert.Equal([c], changes.Spawned);
        Assert.Equal([c], changes.Destroyed);
        Assert.Equal([new ComponentChange(b, drive)], changes.Removed);
        Assert.Equal([new ComponentChange(b, drive)], changes.Added);
        // Снятый и вернувшийся компонент начинает маску заново.
        Assert.Equal([new FieldChange(a, Builtin.Position, 1UL << 1), new FieldChange(b, drive, 1UL << 2)], changes.Fields);
    }

    [Fact]
    public void ScatteredColumnsMarkOnlyDifferentValues()
    {
        var world = Fixtures.World();
        var units = Enumerable.Range(0, 4).Select(i => world.At(i, i)).ToList();
        world.TrackChanges = true;
        var changes = new WorldChanges();
        var query = new Query(world, ["Position"]);
        var columns = new QueryColumns();
        query.Gather(columns);
        columns.Column(Builtin.Position, "x")[1] = 10;
        columns.Column(Builtin.Position, "y")[1] = 10;
        columns.Column(Builtin.Position, "y")[3] = 3;
        query.Scatter(columns, [Builtin.Position]);
        world.TakeChanges(changes);
        Assert.Equal([new FieldChange(units[1], Builtin.Position, 0b11)], changes.Fields);
    }

    [Fact]
    public void CommandsAreTrackedToo()
    {
        var world = Fixtures.World();
        world.TrackChanges = true;
        var unit = world.At(0, 0);
        var changes = new WorldChanges();
        world.TakeChanges(changes);
        var commands = new CommandBuffer(world);
        commands.Set(unit, Builtin.Position).Number(0, 4);
        Commands.Apply(world, commands.Written, [], []);
        world.TakeChanges(changes);
        Assert.Equal([new FieldChange(unit, Builtin.Position, 1)], changes.Fields);
    }

    [Fact]
    public void NothingPilesUpWhenOff()
    {
        var world = Fixtures.World();
        var unit = world.At(1, 1);
        world.SetNumber(unit, Builtin.Position, 0, 7);
        world.Destroy(unit);
        Assert.True(TakeEmpty(world, new WorldChanges()));
    }

    private static bool TakeEmpty(GameWorld world, WorldChanges changes)
    {
        world.TakeChanges(changes);
        return changes.IsEmpty;
    }
}
