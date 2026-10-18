using System.Buffers.Binary;
using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Engine.Tests.World;
using Kharos.Engine.Tools;
using Kharos.Engine.World;

namespace Kharos.Engine.Tests.Tools;

/// <summary>Инструменты Н5 движка: пакетное чтение (Columns, BytesMany), ревизии, носители, обзор; заглушки 16–29.</summary>
public sealed class ModToolsTests
{
    private sealed class Source(GameWorld world, LayerSet? layers = null) : IToolWorld
    {
        public GameWorld World => world;
        public Land? Land => null;
        public LayerSet? Layers => layers;
        public ulong Tick { get; set; }
        public int ProtoId(string path) => -1;
        public string? ProtoPath(int id) => null;
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

    [Fact]
    public void ColumnsAndBytesManyReadManyEntities()
    {
        var world = Fixtures.World();
        var drive = world.Schema.Id("Drive");
        var producer = world.Schema.Id("Producer");
        var a = world.At(1, 2);
        var b = world.Spawn();
        world.Add(b, drive);
        world.Set(b, drive, "speed", 5.0);
        world.Set(b, drive, "move", "wheels");
        world.Add(a, producer);
        world.SetBytes(a, producer, 2, [7, 8, 9]);
        var tools = new ModTools(new Source(world));

        // Пары: Position.y, Drive.speed, Drive.move (строка — номером).
        var output = Call(tools, 11, Input(new uint[] { a, b, 999 }, 3u, (uint)Builtin.Position, 1u, (uint)drive, 1u, (uint)drive, 0u));
        var values = Enumerable.Range(0, 9).Select(i => BinaryPrimitives.ReadDoubleLittleEndian(output.AsSpan(i * 8))).ToArray();
        Assert.Equal([2.0, 0, 0, 0, 5, 0, 0, world.Strings.Find("wheels")!.Value, 0], values);
        Assert.Equal([1, 0, 0, 0, 1, 0, 0, 1, 0], output[72..]);

        output = Call(tools, 12, Input((uint)producer, 2u, new uint[] { a, b }));
        Assert.Equal([3, 0, 0, 0, 7, 8, 9, 0xFF, 0xFF, 0xFF, 0xFF], output);
        // Поле не того вида — ошибка разбора.
        Assert.Equal(-4, tools.Call(12, Input((uint)drive, 0u, new uint[] { b }), new byte[64]));
    }

    [Fact]
    public void RevisionTracksWorldLayersAndTick()
    {
        var world = Fixtures.World();
        var layers = new LayerSet([new LayerSpec { Name = "g", Scope = LayerScope.World, Channels = [Channel.U8] }]);
        var source = new Source(world, layers) { Tick = 42 };
        var tools = new ModTools(source);
        ulong[] Read() => [.. Enumerable.Range(0, 5).Select(i => BinaryPrimitives.ReadUInt64LittleEndian(Call(tools, 13, []).AsSpan(i * 8)))];
        var before = Read();
        Assert.Equal(42ul, before[4]);
        world.At(3, 3);
        layers.Set(0, LayerOwner.World, 1, 1, 0, 5);
        var after = Read();
        Assert.True(after[0] > before[0] && after[1] > before[1] && after[3] > before[3]);
        Assert.Equal(before[2], after[2]);
    }

    [Fact]
    public void ChildrenFollowMountParent()
    {
        var schema = Fixtures.Schema();
        schema.DeclareNumber("Mount", "parent", 0);
        var world = new GameWorld(schema);
        var mount = schema.Id("Mount");
        var carrier = world.Spawn();
        var lonely = world.Spawn();
        var turrets = new List<uint>();
        for (var i = 0; i < 3; i++)
        {
            var turret = world.Spawn();
            world.Add(turret, mount);
            world.SetNumber(turret, mount, 0, carrier);
            turrets.Insert(0, turret);
        }
        var output = Call(new ModTools(new Source(world)), 14, Input(new uint[] { carrier, lonely }));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(output));
        Assert.Equal(turrets.Order(), Enumerable.Range(0, 3).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(4 + i * 4))));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(16)));
    }

    [Fact]
    public void SeesWithoutFogIsVisibleAndStubsAreUnknown()
    {
        var tools = new ModTools(new Source(Fixtures.World()));
        Assert.Equal([2, 2], Call(tools, 15, Input(1u, 2u, 0.5, 0.5, -100.0, 3.0)));
        // 18–20 (Layer, Items, Peak) сделаны: их проверяет LayerToolsTests.
        for (var id = 16; id <= 29; id++)
            if (id is not (18 or 19 or 20))
                Assert.Equal(-1, tools.Call(id, [], new byte[16]));
        Assert.Equal(-1, tools.Call(30, [], new byte[16]));
    }

    [Fact]
    public void RepeatAfterSmallBufferReturnsSameAnswer()
    {
        var world = Fixtures.World();
        for (var i = 0; i < 50; i++)
            world.At(i, 0);
        var tools = new ModTools(new Source(world));
        var input = Input(1u, 0u, 0.0, 0.0, 100.0);
        var small = new byte[8];
        var length = tools.Call(1, input, small);
        Assert.Equal(4 + 50 * 4, length);
        Assert.Equal(new byte[8], small);
        // Тот же вход сразу следом — готовый ответ.
        var full = new byte[length];
        Assert.Equal(length, tools.Call(1, input, full));
        Assert.Equal(50u, BinaryPrimitives.ReadUInt32LittleEndian(full));
        // Мир между вызовами поменялся — ответ считается заново.
        Assert.Equal(length, tools.Call(1, input, small));
        world.At(0.5, 0.5);
        full = new byte[length + 4];
        Assert.Equal(length + 4, tools.Call(1, input, full));
        Assert.Equal(51u, BinaryPrimitives.ReadUInt32LittleEndian(full));
    }
}
