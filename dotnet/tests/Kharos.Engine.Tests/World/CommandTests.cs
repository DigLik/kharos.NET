using System.Buffers.Binary;
using Kharos.Engine.World;

namespace Kharos.Engine.Tests.World;

public sealed class CommandTests
{
    private readonly GameWorld _world = Fixtures.World();
    private readonly List<WorldEvent> _events = [];
    private readonly List<Signal> _signals = [];

    private int Drive => _world.Schema.Id("Drive");
    private int Producer => _world.Schema.Id("Producer");

    private void Apply(CommandBuffer commands) => Commands.Apply(_world, commands.Written, _events, _signals);

    private void Apply(byte[] bytes) => Commands.Apply(_world, bytes, _events, _signals);

    [Fact]
    public void CommandsApplyInOrder()
    {
        var commands = new CommandBuffer(_world);
        var unit = commands.Spawn();
        Assert.Equal(1u, unit);
        Assert.False(_world.Alive(unit), "появится после применения");
        commands.Add(unit, Builtin.Position).Number(0, 3).Number(1, 4);
        commands.Add(unit, Drive).Text(0, "wheels").Number(1, 5).Flag(5, true);
        commands.Add(unit, Builtin.Proto).Number(0, 7);
        commands.Add(unit, Producer).Bytes(2, [9, 8]);
        commands.Emit("hello", [1]);
        commands.Signal("boom", [2, 3]);
        Apply(commands);
        Assert.True(_world.Alive(unit));
        Assert.Equal(3.0, _world.Number(unit, Builtin.Position, 0));
        Assert.Equal(FieldValue.Of("wheels"), _world.Get(unit, Drive, "move"));
        Assert.Equal(FieldValue.Of(true), _world.Get(unit, Drive, "moving"));
        Assert.Equal([9, 8], _world.GetBytes(unit, Producer, 2).ToArray());
        Assert.Equal(["hello", Commands.Spawned], _events.Select(e => e.Name));
        var spawned = _events[1].Payload;
        Assert.Equal(unit, BinaryPrimitives.ReadUInt32LittleEndian(spawned));
        Assert.Equal(7.0, BinaryPrimitives.ReadDoubleLittleEndian(spawned.AsSpan(4)));
        Assert.Equal("boom", Assert.Single(_signals).Name);

        commands.Clear();
        commands.Set(unit, Drive).Number(1, 6);
        commands.Remove(unit, Producer);
        commands.Add(unit, Drive).Number(2, 1);
        Apply(commands);
        Assert.Equal(6.0, _world.Number(unit, Drive, 1));
        Assert.Equal(1.0, _world.Number(unit, Drive, 2));
        Assert.Equal(FieldValue.Of("wheels"), _world.Get(unit, Drive, "move"));
        Assert.False(_world.Has(unit, Producer));

        commands.Clear();
        commands.Destroy(unit);
        Apply(commands);
        Assert.False(_world.Alive(unit));
    }

    [Fact]
    public void BytesFollowTheDocumentedLayout()
    {
        // Так пишет мод на wasm: номер новой сущности — следующий, известный ему до системы.
        var drive = (byte)Drive;
        byte[] buffer =
        [
            0x01, 0x01,                                // Spawn 1
            0x03, 0x01, 0x00, 0x02,                    // Add 1 Position, 2 поля
            0x00, 0x00, .. BitConverter.GetBytes(1.5), // x = 1.5
            0x01, 0x00, .. BitConverter.GetBytes(-2.0),// y = -2
            0x03, 0x01, drive, 0x02,                   // Add 1 Drive, 2 поля
            0x00, 0x04, 0x06, .. "wheels"u8,           // move = "wheels" текстом
            0x05, 0x02,                                // moving = true
            0x06, 0x02, .. "hi"u8, 0x00,               // Emit "hi", пусто
        ];
        Apply(buffer);
        Assert.Equal(2u, _world.NextEntity);
        Assert.Equal(1.5, _world.Number(1, Builtin.Position, 0));
        Assert.Equal(-2.0, _world.Number(1, Builtin.Position, 1));
        Assert.Equal(FieldValue.Of("wheels"), _world.Get(1, Drive, "move"));
        Assert.Equal(1.0, _world.Number(1, Drive, 5));
        Assert.Equal(["hi", Commands.Spawned], _events.Select(e => e.Name));

        var id = _world.Strings.Find("wheels")!.Value;
        byte[] set = [0x04, 0x01, drive, 0x01, 0x00, 0x03, (byte)id, 0x05, 0x01, 0x00];
        _world.Set(1, Drive, "move", "legs");
        Apply(set);
        Assert.Equal(FieldValue.Of("wheels"), _world.Get(1, Drive, "move"));
        Assert.False(_world.Has(1, Builtin.Position));
    }

    [Fact]
    public void LargeIdsUseVarints()
    {
        var commands = new CommandBuffer();
        commands.Destroy(300);
        Assert.Equal([0x02, 0xac, 0x02], commands.Written.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 0x09 }, "незнакомая запись")]
    [InlineData(new byte[] { 0x03, 0x01 }, "обрывается")]
    [InlineData(new byte[] { 0x02, 0x05 }, "сущности 5 не было")]
    [InlineData(new byte[] { 0x02, 0x00 }, "сущности 0 не было")]
    [InlineData(new byte[] { 0x05, 0x01, 0x63 }, "нет компонента")]
    [InlineData(new byte[] { 0x04, 0x01, 0x00, 0x01, 0x07, 0x00 }, "нет поля")]
    [InlineData(new byte[] { 0x04, 0x01, 0x00, 0x01, 0x00, 0x02 }, "не число")]
    [InlineData(new byte[] { 0x01, 0x07 }, "Spawn 7")]
    [InlineData(new byte[] { 0x01, 0x02, 0x01, 0x02 }, "Spawn 2")]
    [InlineData(new byte[] { 0x06, 0x00, 0x00 }, "без имени")]
    [InlineData(new byte[] { 0x06, 0x01, 0xff, 0x00 }, "UTF-8")]
    [InlineData(new byte[] { 0x02, 0xff, 0xff, 0xff, 0xff, 0xff }, "varint")]
    public void BadBuffersAreRejectedWhole(byte[] tail, string message)
    {
        var alive = _world.Spawn();
        _world.Add(alive, Builtin.Position);
        // Верная запись перед неверной не применяется: буфер — целиком или никак.
        byte[] buffer = [0x04, 0x01, 0x00, 0x01, 0x00, 0x00, .. BitConverter.GetBytes(9.0), .. tail];
        var error = Assert.Throws<CommandException>(() => Apply(buffer));
        Assert.Contains(message, error.Message);
        Assert.Equal(0.0, _world.Number(alive, Builtin.Position, 0));
        Assert.Equal(2u, _world.NextEntity);
        Assert.Empty(_events);
    }

    [Fact]
    public void StringIdMustExist()
    {
        var entity = _world.Spawn();
        _world.Add(entity, Drive);
        var commands = new CommandBuffer().Set(entity, Drive).TextId(0, 42);
        Assert.Contains("нет строки", Assert.Throws<CommandException>(() => Apply(commands)).Message);
    }

    [Fact]
    public void ReservedIdsSpawnOnceAndOldIdsNever()
    {
        var old = _world.Spawn();
        _world.Destroy(old);
        var commands = new CommandBuffer();
        commands.Spawn(old);
        Assert.Throws<CommandException>(() => Apply(commands));
        var reserved = _world.Reserve();
        commands.Clear();
        commands.Spawn(reserved);
        commands.Spawn(reserved);
        Assert.Throws<CommandException>(() => Apply(commands));
        commands.Clear();
        commands.Spawn(reserved);
        Apply(commands);
        Assert.True(_world.Alive(reserved));
        Assert.Throws<CommandException>(() => Apply(commands));
    }

    [Fact]
    public void GoneEntitiesAreSkippedQuietly()
    {
        var (gone, bare) = (_world.Spawn(), _world.Spawn());
        _world.Destroy(gone);
        var commands = new CommandBuffer();
        commands.Add(gone, Drive).Number(1, 1);
        commands.Set(gone, Drive).Number(1, 1);
        commands.Set(bare, Drive).Number(1, 1);
        commands.Remove(gone, Drive);
        commands.Remove(bare, Drive);
        commands.Destroy(gone);
        Apply(commands);
        Assert.False(_world.Has(bare, Drive));
        Assert.False(_world.Alive(gone));
    }

    [Fact]
    public void FieldValuesNeedAnOpenRecord()
    {
        var commands = new CommandBuffer();
        Assert.Throws<InvalidOperationException>(() => commands.Number(0, 1));
        commands.Destroy(1);
        Assert.Throws<InvalidOperationException>(() => commands.Number(0, 1));
        Assert.Throws<InvalidOperationException>(() => commands.Spawn());
    }
}
