using System.Text.Json;
using Kharos.Engine.World;

namespace Kharos.Engine.Tests.World;

public sealed class SchemaTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void KindsComeFromDefaultsAndMerge()
    {
        // Перенос kinds_come_from_defaults_and_merge (schema.rs).
        var schema = new Schema();
        schema.Declare("Drive", "speed", Json("0"));
        schema.Declare("Drive", "move", Json("\"\""));
        schema.Declare("Drive", "queue", Json("{}"));
        schema.Declare("Drive", "speed", Json("4"));
        Assert.Throws<ArgumentException>(() => schema.Declare("Drive", "speed", Json("\"fast\"")));
        var drive = schema.Get("Drive");
        Assert.Equal(
            [("speed", FieldKind.Number, 0, 0), ("move", FieldKind.String, 1, 1), ("queue", FieldKind.Bytes, 2, 0)],
            drive.Fields.Select(field => (field.Name, field.Kind, field.Index, field.Slot)));
        Assert.Equal((2, 1), (drive.Columns, drive.BytesFields));
        Assert.Equal(0.0, drive.Fields[0].Default.Number);
    }

    [Fact]
    public void BuiltinComponentsHaveFixedIds()
    {
        var schema = new Schema();
        Assert.Equal(Builtin.Position, schema.Id("Position"));
        Assert.Equal(Builtin.Facing, schema.Id("Facing"));
        Assert.Equal(Builtin.Owner, schema.Id("Owner"));
        Assert.Equal(Builtin.Proto, schema.Id("Proto"));
        Assert.Equal(Builtin.Sight, schema.Id("Sight"));
        Assert.Equal(["x", "y"], schema[Builtin.Position].Fields.Select(field => field.Name));
        Assert.Null(schema.Find("Drive"));
        Assert.Throws<KeyNotFoundException>(() => schema.Id("Drive"));
    }

    [Fact]
    public void SchemaClosesWhenWorldIsMade()
    {
        var schema = new Schema();
        schema.DeclareFlag("Unit", "alive", true);
        _ = new GameWorld(schema);
        Assert.True(schema.Sealed);
        Assert.Throws<InvalidOperationException>(() => schema.DeclareNumber("Unit", "hp", 1));
    }

    [Fact]
    public void FieldsAreLimitedBySixtyFour()
    {
        var schema = new Schema();
        for (var i = 0; i < Schema.MaxFields; i++)
            schema.DeclareNumber("Wide", $"f{i}", i);
        Assert.Throws<InvalidOperationException>(() => schema.DeclareNumber("Wide", "extra", 0));
    }

    [Fact]
    public void DefaultMustMatchKind() =>
        Assert.Throws<ArgumentException>(() => new Schema().Declare("A", "b", FieldKind.Number, FieldValue.Of("x")));
}
