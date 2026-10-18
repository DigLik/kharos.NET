using Kharos.Engine.World;

namespace Kharos.Engine.Tests.World;

/// <summary>Схема и мир для тестов: Drive — как у колёс base (move, speed, turn, tx, ty, moving), Producer — с очередью байтами.</summary>
internal static class Fixtures
{
    public static Schema Schema()
    {
        var schema = new Schema();
        schema.DeclareString("Drive", "move", "");
        schema.DeclareNumber("Drive", "speed", 0);
        schema.DeclareNumber("Drive", "turn", 0);
        schema.DeclareNumber("Drive", "tx", 0);
        schema.DeclareNumber("Drive", "ty", 0);
        schema.DeclareFlag("Drive", "moving", false);
        schema.DeclareString("Producer", "unit", "buggy");
        schema.DeclareNumber("Producer", "progress", 0);
        schema.DeclareBytes("Producer", "queue");
        schema.DeclareNumber("Health", "hp", 100);
        return schema;
    }

    public static GameWorld World() => new(Schema());

    public static List<uint> Query(this GameWorld world, params int[] components)
    {
        var list = new List<uint>();
        world.Query(components, list);
        return list;
    }

    public static List<uint> All(this GameWorld world)
    {
        var list = new List<uint>();
        world.Entities(list);
        return list;
    }

    /// <summary>Сущность с местом.</summary>
    public static uint At(this GameWorld world, double x, double y)
    {
        var entity = world.Spawn();
        world.Add(entity, Builtin.Position);
        world.SetNumber(entity, Builtin.Position, 0, x);
        world.SetNumber(entity, Builtin.Position, 1, y);
        return entity;
    }
}
