using Kharos.Engine.World;

namespace Kharos.Engine.Tests.World;

public sealed class WorldSaveTests
{
    private static GameWorld Sample()
    {
        var world = Fixtures.World();
        var (drive, producer, health) = (world.Schema.Id("Drive"), world.Schema.Id("Producer"), world.Schema.Id("Health"));
        for (var i = 0; i < 20; i++)
        {
            var unit = world.At(i * 1.25, -i);
            if (i % 2 == 0)
            {
                world.Add(unit, drive);
                world.Set(unit, drive, "move", i % 4 == 0 ? "wheels" : "legs");
                world.Set(unit, drive, "moving", i % 3 == 0);
                world.Set(unit, drive, "tx", double.NaN);
            }
            if (i % 5 == 0)
            {
                world.Add(unit, producer);
                world.SetBytes(unit, producer, 2, [(byte)i, 1, 2]);
            }
            world.Add(unit, health);
        }
        world.Destroy(3);
        world.Destroy(8);
        world.Remove(11, health);
        world.Reserve();
        return world;
    }

    [Fact]
    public void SaveLoadSaveIsTheSame()
    {
        var world = Sample();
        var bytes = WorldSave.Save(world);
        var loaded = WorldSave.Load(Fixtures.Schema(), bytes);
        Assert.Equal(bytes, WorldSave.Save(loaded));
        Assert.Equal(world.Count, loaded.Count);
        Assert.Equal(world.All(), loaded.All());
        for (var id = 0; id < world.Schema.Count; id++)
            Assert.Equal(world.Store(id).Entities.ToArray(), loaded.Store(id).Entities.ToArray());
        var drive = loaded.Schema.Id("Drive");
        Assert.Equal(FieldValue.Of("wheels"), loaded.Get(5, drive, "move"));
        Assert.Equal(FieldValue.Of(true), loaded.Get(1, drive, "moving"));
        Assert.True(double.IsNaN(loaded.Number(1, drive, 3)));
        Assert.Equal([10, 1, 2], loaded.GetBytes(11, loaded.Schema.Id("Producer"), 2).ToArray());
        // Номера идут дальше, выданные заранее не повторяются.
        Assert.Equal(world.Spawn(), loaded.Spawn());
    }

    [Fact]
    public void SchemaMayGrowAndShrink()
    {
        var world = Sample();
        var bytes = WorldSave.Save(world);
        var schema = new Schema();
        schema.DeclareNumber("Drive", "speed", 0);
        schema.DeclareString("Drive", "move", "none");
        schema.DeclareNumber("Drive", "grip", 9);
        schema.DeclareNumber("Armor", "value", 1);
        var loaded = WorldSave.Load(schema, bytes);
        var drive = schema.Id("Drive");
        Assert.Equal(FieldValue.Of("legs"), loaded.Get(7, drive, "move"));
        Assert.Equal(9.0, loaded.Number(1, drive, schema[drive].FieldIndex("grip")));
        Assert.Equal(world.Count, loaded.Count);
        Assert.Equal(0, loaded.Store(schema.Id("Armor")).Count);
    }

    [Fact]
    public void KindChangeAndGarbageFail()
    {
        var bytes = WorldSave.Save(Sample());
        var schema = new Schema();
        schema.DeclareNumber("Drive", "move", 0);
        Assert.Throws<FormatException>(() => WorldSave.Load(schema, bytes));
        Assert.Throws<FormatException>(() => WorldSave.Load(Fixtures.Schema(), bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<FormatException>(() => WorldSave.Load(Fixtures.Schema(), [.. bytes, 0]));
        Assert.Throws<FormatException>(() => WorldSave.Load(Fixtures.Schema(), "NOPE"u8));
        for (var cut = 0; cut < bytes.Length; cut += 7)
            Assert.Throws<FormatException>(() => WorldSave.Load(Fixtures.Schema(), bytes.AsSpan(0, cut)));
    }

    [Fact]
    public void LoadedWorldRunsLikeTheOriginal()
    {
        static List<double> Run(GameWorld world)
        {
            var scheduler = new Scheduler(world, [new SystemSpec
            {
                Name = "walk",
                Query = ["Position", "Health"],
                Write = ["Position"],
                System = new DelegateSystem(ctx =>
                {
                    var x = ctx.Column(Builtin.Position, "x");
                    for (var i = 0; i < ctx.Count; i++)
                        x[i] += i;
                    if (ctx.Count > 0)
                        ctx.Commands.Destroy(ctx.Entities[0]);
                }),
            }]);
            for (var i = 0; i < 5; i++)
                scheduler.Step(0.05);
            return [.. world.Store(Builtin.Position).Column(0).ToArray()];
        }
        var world = Sample();
        var copy = WorldSave.Load(Fixtures.Schema(), WorldSave.Save(world));
        Assert.Equal(Run(world), Run(copy));
    }
}
