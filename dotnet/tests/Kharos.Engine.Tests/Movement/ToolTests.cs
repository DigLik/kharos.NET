using System.Buffers.Binary;
using Kharos.Engine.Layers;
using Kharos.Engine.Movement;
using Kharos.Engine.Terrain;
using Kharos.Engine.Tools;
using Kharos.Engine.World;

namespace Kharos.Engine.Tests.Movement;

/// <summary>Инструменты хода (16, 17, 21–28): форматы ModAbi над <see cref="Navigation"/>.</summary>
public sealed class ToolTests
{
    /// <summary>Числа ModAbi (в движке он internal).</summary>
    private static class Abi
    {
        public const int ErrorUnknownId = -1, ErrorBadInput = -4;
        public const int ToolTiles = 16, ToolOccupant = 17, ToolWalkable = 21, ToolPlan = 22, ToolGroupMove = 23, ToolFreeTiles = 24, ToolReach = 25, ToolOpenSpawn = 26, ToolStand = 27, ToolClear = 28;
        public const byte TileOnMap = 1, TilePassable = 2, TileBuildable = 8, TileBuilding = 16, TileOpenGate = 32, TileUnit = 64;
        public const uint PlanAir = 4;
        public const byte PlanFound = 0, PlanCut = 1, PlanNone = 2;
    }

    private sealed class Source(MoveWorld world) : IToolWorld
    {
        public GameWorld World => world.World;
        public Land? Land => world.Land;
        public LayerSet? Layers => world.Layers;
        public ulong Tick { get; set; } = 1;
        public int ProtoId(string path) => -1;
        public string? ProtoPath(int id) => null;
    }

    private static (MoveWorld World, ModTools Tools, Source Source) Make()
    {
        var world = new MoveWorld(256, GeneratorConfig.Default with { Blank = true });
        var source = new Source(world);
        return (world, new ModTools(source) { Navigation = world.Nav }, source);
    }

    private static byte[] Input(params object[] values)
    {
        var bytes = new List<byte>();
        foreach (var value in values)
            bytes.AddRange(value switch
            {
                uint u => BitConverter.GetBytes(u),
                int i => BitConverter.GetBytes(i),
                double d => BitConverter.GetBytes(d),
                uint[] list => [.. BitConverter.GetBytes((uint)list.Length), .. list.SelectMany(BitConverter.GetBytes)],
                _ => throw new ArgumentException(value.GetType().Name),
            });
        return [.. bytes];
    }

    private static byte[] Call(ModTools tools, int id, byte[] input)
    {
        var output = new byte[1 << 16];
        var length = tools.Call(id, input, output);
        Assert.True(length >= 0, $"инструмент {id}: код {length}");
        return output[..length];
    }

    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static int I32(byte[] bytes, int at) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
    private static double F64(byte[] bytes, int at) => BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(at));

    /// <summary>Разбор ответа Plan и Stand.</summary>
    private static (byte Status, bool Roads, int GoalX, int GoalY, double[] Points) Plan(byte[] output) =>
        (output[0], output[1] != 0, I32(output, 2), I32(output, 6), [.. Enumerable.Range(0, (int)U32(output, 10) * 2).Select(i => F64(output, 14 + i * 8))]);

    [Fact(DisplayName = "без хода мира инструменты хода не отвечают")]
    public void WithoutNavigation()
    {
        var world = new MoveWorld(64);
        var tools = new ModTools(new Source(world));
        foreach (var id in (int[])[16, 17, 21, 22, 23, 24, 25, 26, 27, 28])
            Assert.Equal(Abi.ErrorUnknownId, tools.Call(id, [], new byte[16]));
    }

    [Fact(DisplayName = "Walkable, Occupant, Tiles и Clear: здания, ворота, юниты, край карты")]
    public void Ground()
    {
        var (world, tools, _) = Make();
        var building = world.Building(2, 2, 2, 1);
        var unit = world.Spawn("buggy", 1, 5, 5);
        var plane = world.Spawn("drone", 1, 6, 5);
        var soldier = world.Spawn("infantry", 1, 5, 6);
        Assert.Equal([1, 0, 0, 1], Call(tools, Abi.ToolWalkable, Input(0u, 4u, 1, 2, 2, 2, 128, 0, 127, 0)));
        Assert.Equal([1, 1], Call(tools, Abi.ToolWalkable, Input(Abi.PlanAir, 2u, 2, 2, 127, 0)));
        var occupants = Call(tools, Abi.ToolOccupant, Input(2u, 3, 2, 4, 2));
        Assert.Equal((building, 0u), (U32(occupants, 0), U32(occupants, 4)));
        var tiles = Call(tools, Abi.ToolTiles, Input(2, 2, 5u, 4u));
        Assert.Equal(20, tiles.Length);
        Assert.Equal(Abi.TileOnMap | Abi.TilePassable | Abi.TileBuilding, tiles[0] & ~Abi.TileBuildable);
        Assert.Equal(Abi.TileUnit, tiles[3 * 5 + 3] & Abi.TileUnit);
        Assert.Equal(0, tiles[3 * 5 + 4] & Abi.TileUnit);
        world.World.Set(building, Builtin.Footprint, "open", true);
        Assert.Equal(Abi.TileOpenGate, Call(tools, Abi.ToolTiles, Input(2, 2, 1u, 1u))[0] & Abi.TileOpenGate);
        var clear = Call(tools, Abi.ToolClear, Input(4, 4, 4u, 4u, soldier));
        Assert.Equal(1u, U32(clear, 0));
        Assert.Equal(unit, U32(clear, 4));
        Assert.NotEqual(plane, U32(clear, 4));
    }

    [Fact(DisplayName = "Tiles у края int: x + w не переполняется, тайлы за int.MaxValue — вне карты")]
    public void TilesAtIntEdge()
    {
        var (_, tools, _) = Make();
        var tiles = Call(tools, Abi.ToolTiles, Input(int.MaxValue, int.MaxValue, 3u, 2u));
        Assert.Equal(6, tiles.Length);
        Assert.All(tiles, bits => Assert.Equal(0, bits & Abi.TileOnMap));
        // Настоящий тайл — только (int.MaxValue, int.MaxValue), остальные за краем int: нулевой байт.
        Assert.All(tiles[1..], bits => Assert.Equal(0, bits));
        Assert.Equal(4, Call(tools, Abi.ToolTiles, Input(int.MinValue, int.MaxValue - 1, 2u, 2u)).Length);
    }

    [Fact(DisplayName = "Plan: путь, цель на свободном тайле, норма тика и летающий за краем карты")]
    public void PlanTool()
    {
        var (world, tools, source) = Make();
        var unit = world.Spawn("buggy", 1, 0, 0);
        var guard = world.Spawn("tank", 1, 10, 0);
        var output = Plan(Call(tools, Abi.ToolPlan, Input(unit, 20, 0, 0.0, 0u, 0u, 0u, 0u, Array.Empty<uint>())));
        Assert.Equal(Abi.PlanFound, output.Status);
        Assert.Equal((20, 0), (output.GoalX, output.GoalY));
        Assert.Equal((20.5, 0.5), (output.Points[^2], output.Points[^1]));
        // Цель занята стоящим — свободный тайл рядом.
        output = Plan(Call(tools, Abi.ToolPlan, Input(unit, 10, 0, 0.0, 0u, 0u, 0u, 0u, Array.Empty<uint>())));
        Assert.NotEqual((10, 0), (output.GoalX, output.GoalY));
        // Стоящий в ignore — не помеха.
        output = Plan(Call(tools, Abi.ToolPlan, Input(unit, 10, 0, 0.0, 0u, 0u, 0u, 0u, new[] { unit, guard })));
        Assert.Equal((10, 0), (output.GoalX, output.GoalY));
        // Норма: поиск, оборванный ею, оставляет прежний путь; в следующем тике норма — снова целиком.
        Assert.Equal(Abi.PlanCut, Call(tools, Abi.ToolPlan, Input(unit, 30, 3, 0.0, 0u, 0u, 7u, 1u, Array.Empty<uint>()))[0]);
        Assert.Equal(Abi.PlanCut, Call(tools, Abi.ToolPlan, Input(unit, 30, 3, 0.0, 0u, 0u, 7u, 2u, Array.Empty<uint>()))[0]);
        source.Tick++;
        Assert.Equal(Abi.PlanFound, Call(tools, Abi.ToolPlan, Input(unit, 30, 3, 0.0, 0u, 0u, 7u, 100000u, Array.Empty<uint>()))[0]);
        Assert.Equal(Abi.PlanFound, Call(tools, Abi.ToolPlan, Input(unit, 30, 3, 0.0, 0u, 0u, 7u, 100000u, Array.Empty<uint>()))[0]);
        var drone = world.Spawn("drone", 1, 0, 2);
        Assert.Equal(Abi.PlanNone, Call(tools, Abi.ToolPlan, Input(drone, 500, 0, 0.0, 0u, 0u, 0u, 0u, Array.Empty<uint>()))[0]);
        output = Plan(Call(tools, Abi.ToolPlan, Input(drone, 50, 1, 0.0, 0u, 0u, 0u, 0u, Array.Empty<uint>())));
        Assert.Equal([50.5, 1.5], output.Points);
        // Мир инструмент не меняет.
        Assert.False(world.HasPath(unit));
        Assert.Equal(Abi.ErrorBadInput, tools.Call(Abi.ToolPlan, Input(999u, 0, 0, 0.0, 0u, 0u, 0u, 0u, Array.Empty<uint>()), new byte[64]));
    }

    [Fact(DisplayName = "GroupMove, FreeTiles, Reach и OpenSpawn")]
    public void Places()
    {
        var (world, tools, _) = Make();
        var a = world.Spawn("buggy", 1, 0, 0);
        var b = world.Spawn("infantry", 1, 1, 0);
        var drone = world.Spawn("drone", 1, 2, 0);
        world.World.Set(b, Builtin.Body, "rooted", true);
        var slots = Call(tools, Abi.ToolGroupMove, Input(20, 20, new[] { a, b, drone, 999u }));
        Assert.Equal(36, slots.Length);
        Assert.Equal((20, 20, 1), (I32(slots, 0), I32(slots, 4), (int)slots[8]));
        Assert.Equal(2, slots[17]);
        Assert.Equal((20, 20, 0), (I32(slots, 18), I32(slots, 22), (int)slots[26]));
        Assert.Equal(2, slots[35]);

        var free = Call(tools, Abi.ToolFreeTiles, Input(0, 0, 3u, 0u, 0u, -1.0, Array.Empty<uint>()));
        Assert.Equal(3u, U32(free, 0));
        Assert.Equal((0, 0), (I32(free, 4), I32(free, 8)));
        // Стоящие под корпус заняты, кроме ignore.
        free = Call(tools, Abi.ToolFreeTiles, Input(0, 0, 1u, 0u, 0u, 0.5, Array.Empty<uint>()));
        Assert.NotEqual((0, 0), (I32(free, 4), I32(free, 8)));
        free = Call(tools, Abi.ToolFreeTiles, Input(0, 0, 1u, 0u, 0u, 0.5, new[] { a }));
        Assert.Equal((0, 0), (I32(free, 4), I32(free, 8)));

        var reach = Call(tools, Abi.ToolReach, Input(5, 5, 10u, 0u));
        // Заливка останавливается, набрав limit: последний шаг добавляет соседей сверх него, как в TS.
        Assert.Equal(11u, U32(reach, 0));
        Assert.Equal((5, 5, 6, 5), (I32(reach, 4), I32(reach, 8), I32(reach, 12), I32(reach, 16)));
        var spawn = Call(tools, Abi.ToolOpenSpawn, Input(3, 4, 64u));
        Assert.Equal((3, 4), (I32(spawn, 0), I32(spawn, 4)));
    }

    [Fact(DisplayName = "Stand: путь к месту у здания, уже на месте, норма тика")]
    public void StandTool()
    {
        var (world, tools, _) = Make();
        world.Building(10, 0, 3, 2);
        var truck = world.Spawn("truck", 1, 0, 0);
        var output = Plan(Call(tools, Abi.ToolStand, Input(truck, 10.0, 0.0, 3.0, 2.0, 0.0, 2.0, 0u, 0u)));
        Assert.Equal(0, output.Status);
        Assert.True(output.GoalX is >= 7 and < 10);
        var at = Call(tools, Abi.ToolStand, Input(truck, 0.0, 1.0, 1.0, 1.0, 0.0, 2.0, 0u, 0u));
        Assert.Equal(3, at[0]);
        // Норма 1 тайл: первый поиск в тике осматривает её и промахивается, второму не остаётся — не промах.
        Assert.Equal(2, Call(tools, Abi.ToolStand, Input(truck, 10.0, 0.0, 3.0, 2.0, 0.0, 2.0, 5u, 1u))[0]);
        Assert.Equal(1, Call(tools, Abi.ToolStand, Input(truck, 10.0, 0.0, 3.0, 2.0, 0.0, 2.0, 5u, 1u))[0]);
    }
}
