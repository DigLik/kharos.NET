using Kharos.Engine.World;

namespace Kharos.Engine.Tests.World;

public sealed class GameWorldTests
{
    [Fact]
    public void IdsStartAtOneAndAreNeverReused()
    {
        var world = Fixtures.World();
        var a = world.Spawn();
        var b = world.Spawn();
        Assert.Equal((1u, 2u), (a, b));
        world.Destroy(a);
        world.Destroy(a);
        Assert.False(world.Alive(a));
        Assert.Equal(1, world.Count);
        Assert.Equal(3u, world.Spawn());
        var reserved = world.Reserve();
        Assert.Equal(4u, reserved);
        Assert.False(world.Alive(reserved));
        Assert.Equal(5u, world.Spawn());
        world.Revive(reserved);
        Assert.True(world.Alive(reserved));
        Assert.Throws<InvalidOperationException>(() => world.Revive(reserved));
        Assert.Throws<InvalidOperationException>(() => world.Revive(a));
        Assert.Throws<InvalidOperationException>(() => world.Revive(99));
        Assert.Equal([2u, 3u, 4u, 5u], world.All());
    }

    [Fact]
    public void ColumnsFollowAddsAndRemoves()
    {
        // Перенос columns_follow_adds_and_removes (world.rs).
        var world = Fixtures.World();
        var drive = world.Schema.Id("Drive");
        var producer = world.Schema.Id("Producer");
        var (a, b) = (world.Spawn(), world.Spawn());
        world.Add(a, drive);
        world.Set(a, drive, "speed", 4.0);
        world.Add(b, drive);
        world.Set(b, drive, "move", "legs");
        world.Add(b, producer);
        world.SetBytes(b, producer, 2, [1, 2, 3]);
        Assert.Equal(FieldValue.Of(4.0), world.Get(a, drive, "speed"));
        Assert.Equal(FieldValue.Of(""), world.Get(a, drive, "move"));
        Assert.Equal(FieldValue.Of("legs"), world.Get(b, drive, "move"));
        Assert.Equal(FieldValue.Of("buggy"), world.Get(b, producer, "unit"));
        Assert.Equal([1, 2, 3], world.GetBytes(b, producer, 2).ToArray());
        Assert.Throws<KeyNotFoundException>(() => world.Set(a, drive, "sped", 1.0));
        Assert.Throws<ArgumentException>(() => world.Set(a, drive, "speed", "fast"));
        Assert.Throws<InvalidOperationException>(() => world.Set(a, producer, "progress", 1.0));
        world.Destroy(a);
        Assert.Equal([b], world.Query(drive));
        Assert.Equal(0.0, world.Number(b, drive, 1));
        Assert.False(world.TryNumber(a, drive, 1, out _));
        Assert.Throws<InvalidOperationException>(() => world.Add(a, drive));
    }

    [Fact]
    public void SwapRemoveOrderIsTheSameForTheSameOps()
    {
        static List<uint> Run()
        {
            var world = Fixtures.World();
            var health = world.Schema.Id("Health");
            var list = Enumerable.Range(0, 6).Select(_ => world.Spawn()).ToList();
            foreach (var entity in list)
                world.Add(entity, health);
            world.Remove(list[1], health);
            world.Destroy(list[3]);
            world.Add(list[1], health);
            return [.. world.Store(health).Entities];
        }
        // 1..6 → снять 2: 1 6 3 4 5 → убрать 4: 1 6 3 5 → вернуть 2: 1 6 3 5 2.
        Assert.Equal([1u, 6u, 3u, 5u, 2u], Run());
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void ValuesMoveWithSwappedRows()
    {
        var world = Fixtures.World();
        var health = world.Schema.Id("Health");
        var producer = world.Schema.Id("Producer");
        var list = Enumerable.Range(0, 4).Select(_ => world.Spawn()).ToList();
        foreach (var entity in list)
        {
            world.Add(entity, health);
            world.SetNumber(entity, health, 0, entity * 10);
            world.Add(entity, producer);
            world.SetBytes(entity, producer, 2, [(byte)entity]);
        }
        world.Remove(list[0], health);
        world.Remove(list[0], producer);
        foreach (var entity in list.Skip(1))
        {
            Assert.Equal(entity * 10.0, world.Number(entity, health, 0));
            Assert.Equal([(byte)entity], world.GetBytes(entity, producer, 2).ToArray());
        }
    }

    [Fact]
    public void QueryWalksTheShortestStore()
    {
        var world = Fixtures.World();
        var (drive, health) = (world.Schema.Id("Drive"), world.Schema.Id("Health"));
        var list = Enumerable.Range(0, 5).Select(_ => world.Spawn()).ToList();
        foreach (var entity in list)
            world.Add(entity, health);
        world.Add(list[4], drive);
        world.Add(list[2], drive);
        Assert.Equal([list[4], list[2]], world.Query(health, drive));
        Assert.Equal([list[4], list[2]], world.Query(drive, health));
        Assert.Empty(world.Query(drive, Builtin.Position));
        Assert.Empty(world.Query());
    }

    [Fact]
    public void QueryFiltersByFieldValues()
    {
        var world = Fixtures.World();
        var drive = world.Schema.Id("Drive");
        var list = Enumerable.Range(0, 4).Select(_ => world.Spawn()).ToList();
        foreach (var (entity, move) in list.Zip(["wheels", "legs", "wheels", "hover"]))
        {
            world.Add(entity, drive);
            world.Set(entity, drive, "move", move);
        }
        world.Set(list[2], drive, "moving", true);
        var wheels = new Query(world, ["Drive"], [("Drive", "move", "wheels")]);
        var found = new List<uint>();
        wheels.Entities(found);
        Assert.Equal([list[0], list[2]], found);
        found.Clear();
        new Query(world, ["Drive"], [("Drive", "move", "wheels"), ("Drive", "moving", true)]).Entities(found);
        Assert.Equal([list[2]], found);
        Assert.Throws<ArgumentException>(() => new Query(world, ["Drive"], [("Drive", "move", 1.0)]));
        Assert.Throws<ArgumentException>(() => new Query(world, ["Producer"], [("Producer", "queue", FieldValue.Of([]))]));
        Assert.Throws<ArgumentException>(() => new Query(world, ["Drive", "Drive"]));
    }

    [Fact]
    public void ColumnsGatherInOneBlockAndScatterBack()
    {
        var world = Fixtures.World();
        var drive = world.Schema.Id("Drive");
        var list = Enumerable.Range(0, 5).Select(i => world.At(i, -i)).ToList();
        foreach (var entity in list.Where((_, i) => i % 2 == 0))
        {
            world.Add(entity, drive);
            world.SetNumber(entity, drive, 1, entity);
        }
        var query = new Query(world, ["Position", "Drive"]);
        var columns = new QueryColumns();
        query.Gather(columns);
        // Drive короче — порядок его строк; Position идёт не подряд.
        Assert.Equal([list[0], list[2], list[4]], columns.Entities.ToArray());
        Assert.Equal(2 + 6, query.Width);
        Assert.Equal(query.Width * 3, columns.Data.Length);
        Assert.Equal([0.0, 2, 4], columns.Column(0, 0).ToArray());
        Assert.Equal([0.0, -2, -4], columns.Column(Builtin.Position, "y").ToArray());
        Assert.Equal([1.0, 3, 5], columns.Column(drive, "speed").ToArray());
        // Блок: Position.x, Position.y, затем столбцы Drive по слотам.
        Assert.Equal(columns.Column(1, 1).ToArray(), columns.Data.Slice((2 + 1) * 3, 3).ToArray());
        columns.Column(Builtin.Position, "x")[1] = 100;
        columns.Column(drive, "speed")[2] = 50;
        query.Scatter(columns, [Builtin.Position]);
        Assert.Equal(100.0, world.Number(list[2], Builtin.Position, 0));
        Assert.Equal(list[4], world.Number(list[4], drive, 1));
        query.Scatter(columns, [drive]);
        Assert.Equal(50.0, world.Number(list[4], drive, 1));
        world.Remove(list[0], drive);
        Assert.Throws<InvalidOperationException>(() => query.Scatter(columns, [drive]));
    }

    [Fact]
    public void NearFindsByCircleInIdOrder()
    {
        var world = Fixtures.World();
        var health = world.Schema.Id("Health");
        var far = world.At(10, 10);
        var a = world.At(1, 1);
        var b = world.At(-3.5, 0);
        var edge = world.At(5, 0);
        world.Add(b, health);
        var found = new List<uint>();
        world.Near(0, 0, 5, [], found);
        Assert.Equal([a, b, edge], found);
        found.Clear();
        world.Near(0, 0, 5, [health], found);
        Assert.Equal([b], found);
        world.SetNumber(far, Builtin.Position, 0, 0);
        world.SetNumber(far, Builtin.Position, 1, -1);
        world.Destroy(a);
        found.Clear();
        world.Near(0, 0, 5, [], found);
        Assert.Equal([far, b, edge], found);
    }

    [Fact]
    public void StringsAreInternedOnce()
    {
        var world = Fixtures.World();
        var drive = world.Schema.Id("Drive");
        var entity = world.Spawn();
        world.Add(entity, drive);
        world.Set(entity, drive, "move", "wheels");
        var id = world.Strings.Find("wheels");
        Assert.NotNull(id);
        Assert.Equal((double)id.Value, world.Number(entity, drive, 0));
        Assert.Equal(id.Value, world.Strings.Id("wheels"));
        Assert.Equal("", world.Strings.Text(0));
        Assert.Equal("", world.Strings.Text(9999));
    }
}
