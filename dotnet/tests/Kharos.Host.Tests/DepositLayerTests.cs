using Kharos.Engine.Layers;
using Kharos.Plugins.Game;
using Kharos.Protocol;

namespace Kharos.Host.Tests;

/// <summary>
/// Слой месторождений (depositLayer.test.ts, часть про слой; Д3): месторождение — предмет слоя «deposits» (вид и запас),
/// его кладёт мод командой Item. Генератор месторождений и их смена — мод base (С2а, тесты — Kharos.Base.Tests/Sim/Mining), видимость слоёв предметов — сеть (Д4).
/// </summary>
public sealed class DepositLayerTests
{
    private const string NoFog = """{ "fog": false }""";

    private sealed class Peer
    {
        public Peer(HostSession session, uint player)
        {
            (var hostEnd, End) = MemoryTransport.Pair();
            session.Connect(hostEnd, player);
            Pump();
        }

        public MemoryTransport End { get; }
        public Replica Replica { get; } = new();

        public void Pump() => Replica.Pump(End);

        /// <summary>Сколько месторождений у клиента.</summary>
        public int Count()
        {
            var layers = Replica.Layers!;
            var id = layers.Id("deposits");
            return layers.Cells(id, LayerOwner.World).Sum(cell => layers.Items(id, LayerOwner.World, cell).Count);
        }
    }

    [Fact(DisplayName = "клиент получает месторождения от хоста и правки — следом")]
    public void ClientGetsDepositsFromHostAndEditsAfter()
    {
        using var host = LayerLab.Create(ModMode.Clr, NoFog);
        host.Start();
        host.Send(1, "hello.item", LayerLab.Item("deposits", 5, 5, [1, 300]));
        host.Send(1, "hello.item", LayerLab.Item("deposits", 85, -42, [2, 120]));
        host.Step();
        var session = new HostSession(host, new HostSessionOptions { SendRate = 0 });
        var peer = new Peer(session, 1);
        Assert.Equal(2, peer.Count());
        var deposits = host.Layers.Id("deposits");
        Assert.Equal(host.Layers.Blocks(deposits, LayerOwner.World), peer.Replica.Layers!.Blocks(peer.Replica.Layers.Id("deposits"), LayerOwner.World));

        // Правка (запас убавился: предмет убрали и положили заново) и новое месторождение приходят следом.
        host.Send(1, "hello.item", LayerLab.Item("deposits", 5, 5, [], delete: true));
        host.Send(1, "hello.item", LayerLab.Item("deposits", 5, 5, [1, 250]));
        host.Send(1, "hello.item", LayerLab.Item("deposits", -100, 100, [3, 40]));
        session.Step();
        peer.Pump();
        Assert.Equal(3, peer.Count());
        var replica = peer.Replica.Layers;
        Assert.Equal([new LayerItem(5, 5, [1, 250])], replica.Items(replica.Id("deposits"), LayerOwner.World, replica.CellOf(replica.Id("deposits"), 5, 5)));
        Assert.Equal(host.Layers.Blocks(deposits, LayerOwner.World), replica.Blocks(replica.Id("deposits"), LayerOwner.World));
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void DepositLayerIsInTheSaveWithEditsAndEmptiedCells()
    {
        using var host = LayerLab.Create(ModMode.Clr, NoFog);
        host.Start();
        host.Send(1, "hello.item", LayerLab.Item("deposits", 20, 20, [4, 77]));
        host.Send(1, "hello.item", LayerLab.Item("deposits", 130, 20, [2, 5]));
        host.Step();
        // Месторождение выработано: предмет убран, ячейка опустела — в сохранении её нет.
        host.Send(1, "hello.item", LayerLab.Item("deposits", 130, 20, [], delete: true));
        host.Step();
        var deposits = host.Layers.Id("deposits");
        Assert.Single(host.Layers.Cells(deposits, LayerOwner.World));

        using var loaded = Host.Load(LayerLab.Content(), host.Save(), LayerLab.Options(ModMode.Clr));
        var layer = loaded.Layers.Id("deposits");
        Assert.Equal([new LayerItem(20, 20, [4, 77])], loaded.Layers.Items(layer, LayerOwner.World, loaded.Layers.CellOf(layer, 20, 20)));
        Assert.Single(loaded.Layers.Cells(layer, LayerOwner.World));
    }

    /// <summary>Месторождения у клиента: все предметы слоя deposits по порядку ячеек.</summary>
    private static List<LayerItem> Items(Peer peer)
    {
        var layers = peer.Replica.Layers!;
        var id = layers.Id("deposits");
        return [.. layers.Cells(id, LayerOwner.World).SelectMany(cell => layers.Items(id, LayerOwner.World, cell))];
    }

    /// <summary>Тики сессии: хост идёт, подключения получают рассылку.</summary>
    private static void Steps(int count, HostSession session, params Peer[] peers)
    {
        for (var i = 0; i < count; i++)
        {
            session.Step();
            foreach (var peer in peers)
                peer.Pump();
        }
    }

    [Fact(DisplayName = "месторождения в тумане клиенту не шлются")]
    public void DepositsInFogAreNotSentToClient()
    {
        using var host = LayerLab.Create(ModMode.Clr);
        host.Start();
        host.Send(1, "hello.item", LayerLab.Item("deposits", 60, 60, [1, 300]));
        host.Send(1, "hello.item", LayerLab.Item("deposits", -80, 30, [2, 120]));
        host.Step();
        var session = new HostSession(host, new HostSessionOptions { SendRate = 0 });
        var peer = new Peer(session, 1);
        Steps(2 * Vision.Ticks, session, peer);
        var deposits = host.Layers.Id("deposits");
        Assert.Equal(2, host.Layers.Cells(deposits, LayerOwner.World).Sum(cell => host.Layers.Items(deposits, LayerOwner.World, cell).Count));
        Assert.Equal(0, peer.Count());

        // Месторождение у дома игрока — на виду: оно приходит, а те, что в тумане, — нет.
        host.Send(1, "hello.item", LayerLab.Item("deposits", 1, 5, [3, 50]));
        Steps(2 * Vision.Ticks, session, peer);
        Assert.Equal([new LayerItem(1, 5, [3, 50])], Items(peer));
        Assert.Empty(host.Errors);
    }

    [Fact(DisplayName = "клиенту шлются ушедшие месторождения, которые игрок помнит, и не шлются новые, которых он не видел")]
    public void GhostDepositsAreSentAndUnseenNewOnesAreNot()
    {
        using var host = LayerLab.Create(ModMode.Clr);
        host.Start();
        var session = new HostSession(host, new HostSessionOptions { SendRate = 0 });
        var one = new Peer(session, 1);
        var two = new Peer(session, 2);
        host.Send(1, "hello.item", LayerLab.Item("deposits", 60, 60, [1, 300]));
        Steps(2 * Vision.Ticks, session, one, two);
        Assert.Equal(0, one.Count());

        // Игрок 1 приходит к месторождению — узнаёт его.
        host.Send(1, "hello.spawn", HelloGame.Spawn(2, 61, 61, spread: 0));
        Steps(2 * Vision.Ticks, session, one, two);
        Assert.Equal([new LayerItem(60, 60, [1, 300])], Items(one));
        var scouts = HelloGame.Units(host.World, 1).Where(unit => HelloGame.Position(host.World, unit).X > 30).ToList();
        Assert.Equal(2, scouts.Count);

        // Ушёл. В тумане запас месторождения меняется — клиенту уходит прежний, каким игрок его видел.
        LayerLab.Teleport(host, scouts, -60, -60);
        Steps(2 * Vision.Ticks, session, one, two);
        host.Send(1, "hello.item", LayerLab.Item("deposits", 60, 60, [], delete: true));
        host.Send(1, "hello.item", LayerLab.Item("deposits", 60, 60, [1, 250]));
        Steps(2 * Vision.Ticks, session, one, two);
        Assert.Equal([new LayerItem(60, 60, [1, 300])], Items(one));
        // Месторождение выработано и убрано, рядом — новое, которого никто не видел.
        host.Send(1, "hello.item", LayerLab.Item("deposits", 60, 60, [], delete: true));
        host.Send(1, "hello.item", LayerLab.Item("deposits", 66, 60, [2, 500]));
        host.Send(1, "hello.item", LayerLab.Item("deposits", -100, 100, [3, 40]));
        Steps(2 * Vision.Ticks, session, one, two);
        Assert.Equal([new LayerItem(60, 60, [1, 300])], Items(one));
        Assert.Equal(0, two.Count());
        Assert.Equal([new LayerItem(60, 60, [1, 300])], host.ItemMemory!.Items("deposits", 1));

        // Новая вкладка того же игрока получает то же, что он помнит; память переживает сохранение.
        var tab = new Peer(session, 1);
        Assert.Equal(Items(one), Items(tab));
        using (var loaded = Host.Load(LayerLab.Content(), host.Save(), LayerLab.Options(ModMode.Clr)))
        {
            Assert.Equal(host.ItemMemory.Items("deposits", 1), loaded.ItemMemory!.Items("deposits", 1));
            Assert.Empty(loaded.ItemMemory.Items("deposits", 2));
        }

        // Вернулся — увидел, что старого нет, и нашёл новое; игрок 2 так ничего и не знает.
        LayerLab.Teleport(host, scouts, 63, 61);
        Steps(2 * Vision.Ticks, session, one, two, tab);
        Assert.Equal([new LayerItem(66, 60, [2, 500])], Items(one));
        Assert.Equal(Items(one), Items(tab));
        Assert.Equal(0, two.Count());
        Assert.Empty(host.Errors);
    }
}
