using Kharos.Engine.Layers;
using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Host.Tests;

/// <summary>Протокол: приветствие, снимок, дельты, реплика равна хосту, вход посреди партии, дельта — только изменения.</summary>
public class ProtocolTests
{
    /// <summary>Мир и слои реплики совпадают с хостом: сущности, компоненты, значения, строки, слои зрителя.</summary>
    private static void AssertSame(Host host, Replica replica)
    {
        var world = replica.World!;
        var (expected, actual) = (new List<uint>(), new List<uint>());
        host.World.Entities(expected);
        world.Entities(actual);
        Assert.Equal(expected, actual);
        var schema = host.World.Schema;
        for (var c = 0; c < schema.Count; c++)
        {
            Assert.Equal(host.World.Store(c).Count, world.Store(c).Count);
            foreach (var entity in host.World.Store(c).Entities)
            {
                Assert.True(world.Has(entity, c), $"у {entity} нет {schema[c].Name}");
                foreach (var field in schema[c].Fields)
                    Assert.Equal(host.World.Get(entity, c, field.Index), world.Get(entity, c, field.Index));
            }
        }
        for (var id = 0; id < host.Layers.Count; id++)
        {
            var spec = host.Layers.Spec(id);
            var owner = spec.Scope == LayerScope.World ? LayerOwner.World : LayerOwner.Player(replica.Player);
            var mine = host.Layers.Blocks(id, owner);
            var theirs = replica.Layers!.Blocks(replica.Layers.Id(spec.Name), owner);
            Assert.Equal(mine, theirs);
        }
    }

    [Fact]
    public void WelcomeRoundTripsAndRebuildsSchema()
    {
        using var host = HelloGame.Create();
        var welcome = host.Welcome(3);
        var decoded = Welcome.Decode(welcome.Encode());
        Assert.Equal((3u, "hello", host.Seed, 0.05), (decoded.Player, decoded.Game, decoded.Seed, decoded.Dt));
        Assert.Equal(host.Content.Checksum, decoded.Checksum);
        Assert.Equal(new MapBounds(-256, -256, 256, 256), decoded.Map);
        Assert.True(decoded.Fog);
        Assert.Equal(host.Game.Land, decoded.Land);
        Assert.Equal([Fog.Sight, Fog.Explored], decoded.Layers.Select(layer => layer.Name));
        var schema = decoded.BuildSchema();
        Assert.Equal(host.World.Schema.Components.Select(c => (c.Name, string.Join(",", c.Fields.Select(f => $"{f.Name}:{f.Kind}:{f.Default}")))),
            schema.Components.Select(c => (c.Name, string.Join(",", c.Fields.Select(f => $"{f.Name}:{f.Kind}:{f.Default}")))));
        Assert.Throws<FormatException>(() => Welcome.Decode(welcome.Encode()[..^3]));
    }

    [Fact]
    public void ReplicaFollowsHostTickByTick()
    {
        using var host = HelloGame.Create();
        host.Start();
        var session = new HostSession(host);
        var (hostEnd, clientEnd) = MemoryTransport.Pair();
        session.Connect(hostEnd, 1);
        var replica = new Replica();
        Assert.Equal(2, replica.Pump(clientEnd));
        AssertSame(host, replica);

        // Приказы — через транспорт, как у настоящего клиента.
        clientEnd.Send(DeltaCodec.Order("hello.spawn", HelloGame.Spawn(300, 20, 20, spread: 30)));
        var beeps = 0;
        var signals = new List<ReplicaSignal>();
        for (var tick = 0; tick < 80; tick++)
        {
            if (tick == 40)
                clientEnd.Send(DeltaCodec.Order("hello.spawn", HelloGame.Spawn(50, -20, 0, spread: 10)));
            session.Step();
            Assert.Equal(1, replica.Pump(clientEnd));
            Assert.Equal(host.Tick, replica.Tick);
            replica.TakeSignals(signals);
            beeps += signals.Count(s => s.Signal.Name == "hello.beep");
            signals.Clear();
            if (tick % 10 == 9)
                AssertSame(host, replica);
        }
        AssertSame(host, replica);
        Assert.Equal(353, HelloGame.Units(replica.World!, 1).Count);
        Assert.True(beeps > 0, "сигналы доходят до реплики");
        Assert.Equal(1, replica.WorldVersion);
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void SecondPlayerJoinsMidGameFromSnapshot()
    {
        using var host = HelloGame.Create();
        host.Start();
        var session = new HostSession(host);
        var (hostOne, clientOne) = MemoryTransport.Pair();
        session.Connect(hostOne, 1);
        var one = new Replica();
        host.Send(1, "hello.spawn", HelloGame.Spawn(100, 0, 0, spread: 20));
        for (var tick = 0; tick < 30; tick++)
        {
            session.Step();
            one.Pump(clientOne);
        }

        var (hostTwo, clientTwo) = MemoryTransport.Pair();
        session.Connect(hostTwo, 2);
        var two = new Replica();
        two.Pump(clientTwo);
        one.Pump(clientOne);
        Assert.Equal((2u, 30ul), (two.Player, two.Tick));
        AssertSame(host, two);
        AssertSame(host, one);
        Assert.Equal(3, HelloGame.Units(two.World!, 2).Count);

        for (var tick = 0; tick < 20; tick++)
        {
            session.Step();
            one.Pump(clientOne);
            two.Pump(clientTwo);
        }
        AssertSame(host, one);
        AssertSame(host, two);
        // Слои игрока — только ему: туман второго к первому не приходит.
        var sight = one.Layers!.Id(Fog.Sight);
        Assert.Empty(one.Layers.Cells(sight, LayerOwner.Player(2)));
        Assert.NotEmpty(two.Layers!.Cells(sight, LayerOwner.Player(2)));
    }

    [Fact]
    public void DeltaCarriesExactlyWhatChanged()
    {
        using var host = HelloGame.Create();
        host.Start();
        host.Join(1);
        host.Send(1, "hello.spawn", HelloGame.Spawn(200, 0, 0, spread: 40));
        host.Step();
        var first = host.TakeDelta();
        Assert.Equal(203 + 1, first.Created.Count); // юниты и память мода

        for (var round = 0; round < 5; round++)
        {
            var before = Values(host.World);
            host.Step();
            var delta = host.TakeDelta();
            var after = Values(host.World);
            Assert.Empty(delta.Created);
            Assert.Empty(delta.Destroyed);
            var changed = after.Where(pair => !before.TryGetValue(pair.Key, out var old) || !old.Equals(pair.Value)).Select(pair => pair.Key).ToHashSet();
            var sent = new HashSet<(uint, int, int)>();
            foreach (var field in delta.Fields)
                for (var index = 0; index < 64; index++)
                    if ((field.Mask & (1UL << index)) != 0)
                        sent.Add((field.Entity, field.Component, index));
            Assert.Equal(changed.OrderBy(k => k), sent.OrderBy(k => k));
            Assert.DoesNotContain(delta.Fields, field => field.Entity == host.Mods[0].StorageEntity);
        }

        // Ничего не происходит — пустая дельта, сообщение в два десятка байт.
        var idle = HelloGame.Content();
        Assert.SkipUnless(HelloGame.Built, HelloGame.NotBuilt);
        using var quiet = new Host(idle);
        quiet.TakeDelta();
        quiet.Step();
        var empty = quiet.TakeDelta();
        Assert.True(empty.IsEmpty);
        Assert.True(DeltaCodec.Encode(empty, quiet.World, quiet.Layers, new FullView(1)).Length < 24);
    }

    [Fact]
    public void GarbageMessagesAreRejected()
    {
        using var host = HelloGame.Create();
        host.Start();
        var session = new HostSession(host);
        var (hostEnd, clientEnd) = MemoryTransport.Pair();
        session.Connect(hostEnd, 1);
        var replica = new Replica();
        replica.Pump(clientEnd);
        host.Send(1, "hello.spawn", HelloGame.Spawn(10, 0, 0));
        host.Step();
        var delta = DeltaCodec.Encode(host.TakeDelta(), host.World, host.Layers, new FullView(1));
        Assert.Throws<FormatException>(() => replica.Apply(delta.AsSpan(0, delta.Length / 2)));
        Assert.Throws<FormatException>(() => new Replica().Apply(delta));
        Assert.Throws<FormatException>(() => replica.Apply([0x7f]));

        // Мусор вместо приказа — отброшен, хост идёт дальше.
        clientEnd.Send([0x10, 0xff]);
        clientEnd.Send(DeltaCodec.Order("", []));
        session.Step();
        Assert.Equal(2, session.Connections[0].BadOrders);
    }

    /// <summary>Все значения полей мира: (сущность, компонент, поле) → значение.</summary>
    private static Dictionary<(uint, int, int), FieldValue> Values(GameWorld world)
    {
        var values = new Dictionary<(uint, int, int), FieldValue>();
        for (var c = 0; c < world.Schema.Count; c++)
            foreach (var entity in world.Store(c).Entities)
                foreach (var field in world.Schema[c].Fields)
                    values[(entity, c, field.Index)] = world.Get(entity, c, field.Index);
        return values;
    }
}
