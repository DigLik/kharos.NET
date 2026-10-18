using Kharos.Engine.Layers;
using Kharos.Plugins.Game;
using Kharos.Protocol;

namespace Kharos.Host.Tests;

/// <summary>
/// Слой месторождений (depositLayer.test.ts, часть про слой; Д3): месторождение — предмет слоя «deposits» (вид и запас),
/// его кладёт мод командой Item. Генератор месторождений и их смена — мод base (С2а), видимость слоёв предметов — сеть (Д4).
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

    [Fact(DisplayName = "слой месторождений — в сохранении: и правки, и клетки, взятые до правок карты", Skip = "ждёт С2а: клетки, взятые у генератора")]
    public void DepositCellsTakenBeforeLandEditsAreSaved()
    {
    }

    [Fact(DisplayName = "кисть и правка карты месторождений не создают и не двигают", Skip = "ждёт С2а: генератор месторождений и редактор")]
    public void BrushAndLandEditsNeitherCreateNorMoveDeposits()
    {
    }

    [Fact(DisplayName = "месторождения в тумане клиенту не шлются", Skip = "ждёт видимости слоёв предметов в сети (Д4): слои предметов мира пока уходят всем")]
    public void DepositsInFogAreNotSentToClient()
    {
    }

    [Fact(DisplayName = "клиенту шлются ушедшие месторождения, которые игрок помнит, и не шлются новые, которых он не видел", Skip = "ждёт видимости слоёв предметов в сети (Д4) и призраков месторождений (С2а)")]
    public void GhostDepositsAreSentAndUnseenNewOnesAreNot()
    {
    }
}
