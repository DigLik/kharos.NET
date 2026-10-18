using Kharos.Engine.World;

namespace Kharos.Engine.Tests.World;

/// <summary>Метки, атрибуты компонентов и компоненты движка Н5 (Footprint … Reveal).</summary>
public sealed class SchemaTagTests
{
    [Fact]
    public void EngineComponentsFollowTheFirstFiveWithFixedIds()
    {
        var schema = new Schema();
        Assert.Equal(Builtin.Count, schema.Count);
        Assert.Equal(
            [("Footprint", Builtin.Footprint), ("Body", Builtin.Body), ("Mover", Builtin.Mover), ("Path", Builtin.Path), ("Mount", Builtin.Mount), ("Team", Builtin.Team), ("Sighting", Builtin.Sighting), ("Reveal", Builtin.Reveal)],
            schema.Components.Skip(Builtin.Sight + 1).Select(c => (c.Name, c.Id)));
        Assert.Equal(["goalX", "goalY", "wait", "tries", "near", "roads", "direct", "stuck", "best", "slot", "points"], schema[Builtin.Path].Fields.Select(f => f.Name));
        Assert.Equal(FieldKind.Bytes, schema[Builtin.Path].Field("points")!.Kind);
        Assert.Equal(FieldKind.String, schema[Builtin.Mover].Field("chassis")!.Kind);
        Assert.Equal(-1.0, schema[Builtin.Team].Field("head")!.Default.Number);
        Assert.Equal(NetMode.Owner, schema[Builtin.Path].Net);
        Assert.Equal(NetMode.None, schema[Builtin.Sighting].Net);
        // Мод добавит компонент — его номер после движка.
        schema.DeclareNumber("Health", "value", 1);
        Assert.Equal(Builtin.Count, schema.Id("Health"));
    }

    [Fact]
    public void TagHasNoFieldsAndLivesInWorldAndSave()
    {
        var schema = new Schema();
        var drop = schema.DeclareTag("Drop");
        Assert.True(drop.Tag);
        Assert.Same(drop, schema.DeclareTag("Drop"));
        schema.DeclareNumber("Health", "value", 1);
        var world = new GameWorld(schema);
        var entity = world.Spawn();
        world.Add(entity, drop.Id);
        Assert.True(world.Has(entity, drop.Id));
        var loaded = WorldSave.Load(schema, WorldSave.Save(world));
        Assert.True(loaded.Has(entity, drop.Id));
        world.Remove(entity, drop.Id);
        Assert.False(world.Has(entity, drop.Id));
    }

    [Fact]
    public void AttributesSetNetPrivateAndSave()
    {
        var schema = new Schema();
        schema.DeclareNumber("Armed", "target", 0);
        schema.DeclareFlag("Armed", "chase", false);
        schema.DeclareNumber("Armed", "cooldown", 0);
        var armed = schema.Attributes("Armed", @private: ["cooldown", "target"]);
        Assert.Equal(["target", "cooldown"], armed.Private);
        Assert.Equal(0b101UL, armed.PrivateMask);
        Assert.True(armed.Field("target")!.Private);
        Assert.False(armed.Field("chase")!.Private);
        Assert.Equal((NetMode.All, true), (armed.Net, armed.Save));
        schema.Attributes("Armed", NetMode.Owner, save: false);
        Assert.Equal((NetMode.Owner, false), (armed.Net, armed.Save));
        Assert.Throws<ArgumentException>(() => schema.Attributes("Armed", @private: ["nope"]));
        Assert.Throws<ArgumentException>(() => schema.Attributes("Missing", NetMode.None));
        _ = new GameWorld(schema);
        Assert.Throws<InvalidOperationException>(() => schema.DeclareTag("Late"));
        Assert.Throws<InvalidOperationException>(() => schema.Attributes("Armed", NetMode.All));
    }
}
