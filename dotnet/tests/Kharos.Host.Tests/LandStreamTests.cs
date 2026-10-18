using Hello;
using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;
using Kharos.Plugins.Game;
using Kharos.Protocol;

namespace Kharos.Host.Tests;

/// <summary>
/// Правки земли клиентам (Д3, Н4 «открытый вопрос»): без тумана — всем сразу; с туманом — то, что игрок узнал (память
/// engine.recallLand), а подключившийся позже получает карту до правок и свои знания поверх. Покрытие — то же правило.
/// </summary>
public sealed class LandStreamTests
{
    private const byte Rock = 2, Swamp = 0;

    /// <summary>Клиент на конце транспорта: реплика и что пришло вне мира.</summary>
    private sealed class Peer
    {
        public Peer(HostSession session, uint player)
        {
            (var hostEnd, End) = MemoryTransport.Pair();
            Connection = session.Connect(hostEnd, player);
            Pump();
        }

        public MemoryTransport End { get; }
        public Connection Connection { get; }
        public Replica Replica { get; } = new();
        public List<MessageType> Types { get; } = [];

        public void Pump()
        {
            while (End.TryReceive(out var message))
            {
                Types.Add(Messages.TypeOf(message));
                Replica.Apply(message);
            }
            Replica.Inbox.Clear();
        }

        public Land Land => Replica.Terrain!;
    }

    private static (Host Host, HostSession Session) Party(string? settings)
    {
        var host = LayerLab.Create(ModMode.Clr, settings);
        host.Start();
        return (host, new HostSession(host, new HostSessionOptions { SendRate = 0 }));
    }

    private static void Step(HostSession session, params Peer[] peers)
    {
        session.Step();
        foreach (var peer in peers)
            peer.Pump();
    }

    private static void Steps(int count, HostSession session, params Peer[] peers)
    {
        for (var i = 0; i < count; i++)
            Step(session, peers);
    }

    private static void Edit(Host host, int x, int y, byte terrain) => host.Send(1, "hello.land", LayerLab.Tile(x, y, terrain));

    [Fact]
    public void WithoutFogEveryEditReachesEveryoneAndLateJoinerGetsTheMap()
    {
        var (host, session) = Party("""{ "fog": false }""");
        using var _ = host;
        Assert.Null(host.Recall);
        var one = new Peer(session, 1);
        var two = new Peer(session, 2);
        // Карта по генератору, пока её не правили: пакета карты нет.
        Assert.DoesNotContain(MessageType.Land, one.Types);
        var before = one.Land.Stored(30, 30);
        Assert.Equal(host.Land!.Stored(30, 30), before);

        Edit(host, 30, 30, Rock);
        Edit(host, 31, 30, Swamp);
        Step(session, one, two);
        foreach (var peer in new[] { one, two })
        {
            Assert.Equal(host.Land.Stored(30, 30), peer.Land.Stored(30, 30));
            Assert.Equal(host.Land.Stored(31, 30), peer.Land.Stored(31, 30));
            Assert.Equal(2, peer.Replica.LandRevision);
            Assert.Contains(MessageType.LandEdits, peer.Types);
        }
        Assert.Equal(TerrainType.Rock, one.Land.TerrainAt(30, 30));

        // Вошедший позже получает правленую карту целиком (она сжата и приходит до снимка).
        var third = new Peer(session, 3);
        Assert.Contains(MessageType.Land, third.Types);
        Assert.Equal(host.Land.Stored(30, 30), third.Land.Stored(30, 30));
        Assert.Equal(host.Land.Stored(31, 30), third.Land.Stored(31, 30));
        // Правка после — всем, и третьему тоже; пустая рассылка правок не шлёт.
        var count = one.Types.Count(type => type == MessageType.LandEdits);
        Step(session, one, two, third);
        Assert.Equal(count, one.Types.Count(type => type == MessageType.LandEdits));
        Edit(host, -40, 9, Rock);
        Step(session, one, two, third);
        Assert.Equal(TerrainType.Rock, third.Land.TerrainAt(-40, 9));
        Assert.Equal(host.Land.Stored(-40, 9), one.Land.Stored(-40, 9));
        Assert.Empty(host.Errors);
    }

    [Fact(DisplayName = "правку в тумане игрок не узнаёт, пока не увидит; на виду — узнаёт сразу")]
    public void EditInFogIsKnownOnlyWhenSeenAndNewTabGetsPristineMapWithKnownEdits()
    {
        var (host, session) = Party(null);
        using var _ = host;
        var one = new Peer(session, 1);
        // Юниты игрока 1 стоят у (0, 4): ближний тайл — в обзоре, дальний — нет.
        var (near, far) = ((1, 6), (100, 100));
        Assert.True(host.Vision!.Sees(1, near.Item1 + 0.5, near.Item2 + 0.5));
        Assert.False(host.Vision.Sees(1, far.Item1 + 0.5, far.Item2 + 0.5));
        var farBefore = host.Land!.Stored(far.Item1, far.Item2);
        var nearBefore = host.Land.Stored(near.Item1, near.Item2);

        Edit(host, near.Item1, near.Item2, host.Land.TerrainAt(near.Item1, near.Item2) == TerrainType.Rock ? Swamp : Rock);
        Edit(host, far.Item1, far.Item2, host.Land.TerrainAt(far.Item1, far.Item2) == TerrainType.Rock ? Swamp : Rock);
        // Увиденное игроки сверяют раз в LearnTicks тиков.
        Steps(Kharos.Engine.Recall.LayerMemory.LearnTicks + 1, session, one);
        Assert.NotEqual(nearBefore, host.Land.Stored(near.Item1, near.Item2));
        Assert.Equal(host.Land.Stored(near.Item1, near.Item2), one.Land.Stored(near.Item1, near.Item2));
        Assert.Equal(farBefore, one.Land.Stored(far.Item1, far.Item2));
        Assert.NotEqual(host.Land.Stored(far.Item1, far.Item2), one.Land.Stored(far.Item1, far.Item2));

        // Новая вкладка того же игрока: карта до правок, правка на виду — поверх; в тумане — нет.
        var second = new Peer(session, 1);
        Assert.Contains(MessageType.Land, second.Types);
        Assert.Contains(MessageType.LandEdits, second.Types);
        Assert.Equal(host.Land.Stored(near.Item1, near.Item2), second.Land.Stored(near.Item1, near.Item2));
        Assert.Equal(farBefore, second.Land.Stored(far.Item1, far.Item2));
        // Игрок 2 не видел ничего: его карта — до правок.
        var other = new Peer(session, 2);
        Assert.Equal(nearBefore, other.Land.Stored(near.Item1, near.Item2));
        Assert.Equal(farBefore, other.Land.Stored(far.Item1, far.Item2));

        // Свой юнит у дальнего тайла открывает его: правка доходит всем вкладкам игрока.
        host.Send(1, "hello.spawn", HelloGame.Spawn(1, far.Item1 + 1, far.Item2 + 1, spread: 0));
        Steps(12, session, one, second, other);
        Assert.Equal(host.Land.Stored(far.Item1, far.Item2), one.Land.Stored(far.Item1, far.Item2));
        Assert.Equal(host.Land.Stored(far.Item1, far.Item2), second.Land.Stored(far.Item1, far.Item2));
        Assert.Equal(farBefore, other.Land.Stored(far.Item1, far.Item2));
        Assert.Empty(host.Errors);
    }

    [Fact(DisplayName = "знание игроков о карте переживает сохранение")]
    public void KnowledgeSurvivesHostSave()
    {
        var (host, session) = Party(null);
        using var _ = host;
        var one = new Peer(session, 1);
        var (near, far) = ((2, 6), (-90, 70));
        Edit(host, near.Item1, near.Item2, Rock);
        Edit(host, far.Item1, far.Item2, Swamp);
        Steps(Kharos.Engine.Recall.LayerMemory.LearnTicks + 1, session, one);
        var known = host.KnownLand(1);
        Assert.Single(known);
        var farBefore = one.Land.Stored(far.Item1, far.Item2);

        var save = host.Save();
        using var loaded = Host.Load(LayerLab.Content(), save, LayerLab.Options(ModMode.Clr));
        Assert.Equal(known, loaded.KnownLand(1));
        Assert.Equal(host.Recall!.Land!.OriginalTiles(), loaded.Recall!.Land!.OriginalTiles());
        Assert.Equal(host.Land!.Stored(far.Item1, far.Item2), loaded.Land!.Stored(far.Item1, far.Item2));
        // Подключение к загруженному хосту: карта до правок и знания игрока поверх, как у прежнего.
        var session2 = new HostSession(loaded, new HostSessionOptions { SendRate = 0 });
        var again = new Peer(session2, 1);
        Assert.Equal(host.Land.Stored(near.Item1, near.Item2), again.Land.Stored(near.Item1, near.Item2));
        Assert.Equal(farBefore, again.Land.Stored(far.Item1, far.Item2));
        Assert.Empty(loaded.Errors);
    }

    // Покрытие: код = хозяин·2 + вид + 2.

    [Fact]
    public void PavingReachesPlayerAsHeKnowsItNotAsTheWorldHasIt()
    {
        var (host, session) = Party(null);
        using var _ = host;
        var one = new Peer(session, 1);
        var two = new Peer(session, 2);
        var paving = host.Layers.Id("paving");
        // Игрок 1 кладёт своё покрытие у своих юнитов, игрок 2 — у своих (в 24 тайлах, вне обзора первого).
        host.Send(1, "hello.layer", LayerLab.Layer("paving", 0, 1, 6, 2, 1, 4));
        host.Send(2, "hello.layer", LayerLab.Layer("paving", 0, 24, 6, 2, 1, 6));
        Steps(2, session, one, two);
        Assert.Equal(4, one.Replica.Layers!.Get(one.Replica.Layers.Id("paving"), LayerOwner.World, 1, 6, 0));
        Assert.Equal(4, one.Replica.Layers.Get(one.Replica.Layers.Id("paving"), LayerOwner.World, 2, 6, 0));
        Assert.Equal(0, one.Replica.Layers.Get(one.Replica.Layers.Id("paving"), LayerOwner.World, 24, 6, 0));
        Assert.Equal(6, two.Replica.Layers!.Get(two.Replica.Layers.Id("paving"), LayerOwner.World, 24, 6, 0));
        Assert.Equal(0, two.Replica.Layers.Get(two.Replica.Layers.Id("paving"), LayerOwner.World, 1, 6, 0));
        // У хоста слой полный.
        Assert.Equal(6, host.Layers.Get(paving, LayerOwner.World, 25, 6, 0));

        // Вкладка игрока 1 позже — то же, что знает он, а не мир.
        var again = new Peer(session, 1);
        Assert.Equal(4, again.Replica.Layers!.Get(again.Replica.Layers.Id("paving"), LayerOwner.World, 1, 6, 0));
        Assert.Equal(0, again.Replica.Layers.Get(again.Replica.Layers.Id("paving"), LayerOwner.World, 25, 6, 0));

        // Игрок 1 пришёл к покрытию соперника — увидел и узнал.
        host.Send(1, "hello.spawn", HelloGame.Spawn(1, 25, 7, spread: 0));
        Steps(12, session, one, two, again);
        Assert.Equal(6, one.Replica.Layers.Get(one.Replica.Layers.Id("paving"), LayerOwner.World, 25, 6, 0));
        Assert.Equal(6, again.Replica.Layers.Get(again.Replica.Layers.Id("paving"), LayerOwner.World, 25, 6, 0));
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void WithoutFogPavingGoesAsAnOrdinaryWorldLayer()
    {
        var (host, session) = Party("""{ "fog": false }""");
        using var _ = host;
        var one = new Peer(session, 1);
        host.Send(1, "hello.layer", LayerLab.Layer("paving", 0, 50, 50, 3, 3, 8));
        Step(session, one);
        var id = one.Replica.Layers!.Id("paving");
        Assert.Equal(host.Layers.Blocks(host.Layers.Id("paving"), LayerOwner.World), one.Replica.Layers.Blocks(id, LayerOwner.World));
        Assert.NotEmpty(one.Replica.Layers.Blocks(id, LayerOwner.World));
    }

    [Fact]
    public void ReplicaKeepsLandEditsQueuedUntilTerrainIsBuiltAndRejectsBadMessages()
    {
        var (host, session) = Party("""{ "fog": false }""");
        using var _ = host;
        // Реплика, что получила правки, не обращаясь к карте: они ложатся при сборке.
        var (hostEnd, end) = MemoryTransport.Pair();
        session.Connect(hostEnd, 1);
        var replica = new Replica();
        replica.Apply(Take(end));
        Edit(host, 8, 8, Rock);
        session.Step();
        while (end.TryReceive(out var message))
            replica.Apply(message);
        Assert.Equal(1, replica.LandRevision);
        Assert.Equal(host.Land!.Stored(8, 8), replica.Terrain!.Stored(8, 8));
        // Обрыв сообщения и лишние байты — ошибка формата.
        var edits = Messages.LandEdits([new LandTile(1, 2, new StoredTile(1, 2, 3, 4))]);
        Assert.Equal([new LandTile(1, 2, new StoredTile(1, 2, 3, 4))], Messages.ReadLandEdits(edits));
        Assert.Throws<FormatException>(() => Messages.ReadLandEdits(edits[..^1]));
        Assert.Throws<FormatException>(() => Messages.ReadLandEdits([.. edits, 0]));
        Assert.Throws<FormatException>(() => Messages.ReadLandEdits([(byte)MessageType.LandEdits, 200]));
    }

    private static byte[] Take(MemoryTransport end)
    {
        Assert.True(end.TryReceive(out var message));
        return message;
    }
}
