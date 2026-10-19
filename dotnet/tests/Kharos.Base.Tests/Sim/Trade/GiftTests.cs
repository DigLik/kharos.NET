using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Signals;
using Kharos.Base.Tests.Kernel;
using Kharos.Engine.World;
using MemoryPack;
using static Kharos.Base.Tests.Logistics.LogisticsScene;

namespace Kharos.Base.Tests.Trade;

/// <summary>Передача юнитов союзнику (packages/tests/gift.test.ts). Меню юнитов клиента ждёт Н6 (Skip).</summary>
public sealed class GiftTests
{
    private static Sim Make() => Scene.Create(128, new { protection = 0, techTree = false });

    /// <summary>Игрока без счёта мир не знает: Player появляется вместе с первым начислением.</summary>
    private static void Account(Sim sim, params uint[] players)
    {
        foreach (var player in players)
            sim.SetCredits(player, 0);
    }

    /// <summary>Ровное место под здание.</summary>
    private static (int X, int Y) Spot(Sim sim, string type)
    {
        for (var y = -80; y < 80; y++)
            for (var x = -80; x < 80; x++)
                if (Rock(sim, x, y, Width(type), 2) && Scene.CanPlace(sim, type, x, y))
                    return (x, y);
        throw new InvalidOperationException($"Не нашлось места под {type}");
    }

    /// <summary>Свободный тайл под юнита рядом с (x, y).</summary>
    private static (int X, int Y) Free(Sim sim, int x, int y)
    {
        for (var radius = 0; radius < 40; radius++)
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == radius && Rock(sim, x + dx, y + dy, 1, 1))
                        return (x + dx, y + dy);
        throw new InvalidOperationException("Не нашлось места под юнита");
    }

    private static uint Truck(Sim sim, uint player, int x, int y)
    {
        var (fx, fy) = Free(sim, x, y);
        return Spawn(sim, "truck", player, fx, fy);
    }

    [Fact(DisplayName = "отдать юнитов можно только союзнику и только своих — и только раз")]
    public void GiftOnlyToAnAllyOnlyOwnOnlyOnce()
    {
        using var sim = Make();
        Account(sim, 1, 2, 3);
        var truck = Truck(sim, 1, 4, 4);
        // Не союзник: юниты остаются у хозяина.
        Assert.False(sim.HandOver(1, 2, truck));
        Assert.Equal(1u, sim.OwnerOf(truck));
        sim.Ally(1, 2);
        Assert.False(sim.HandOver(1, 1, truck));
        // Чужой юнит не отдать, даже если оба — союзники: юнит не его.
        sim.Ally(1, 3);
        Assert.False(sim.HandOver(3, 2, truck));
        Assert.Equal(1u, sim.OwnerOf(truck));
        Assert.True(sim.HandOver(1, 2, truck));
        Assert.Equal(2u, sim.OwnerOf(truck));
        // Юнит уже не свой: второй раз отдавать нечего.
        Assert.False(sim.HandOver(1, 2, truck));
    }

    [Fact(DisplayName = "в список на отдачу не идут чужие, турели на носителе и обречённые; повтор в списке — один раз")]
    public void GiftListSkipsForeignTurretsAndDoomed()
    {
        using var sim = Make();
        var (tx, ty) = Free(sim, 4, 4);
        var tank = Spawn(sim, "tank", 1, tx, ty);
        var turret = Assert.Single(sim.With("Mount", "parent", tank));
        var doomed = Truck(sim, 1, 6, 6);
        sim.Add(doomed, Names.Doomed.Name);
        sim.Set(doomed, "Doomed.left", 500);
        var foreign = Truck(sim, 3, 8, 8);
        Account(sim, 1, 2);
        sim.Ally(1, 2);
        Assert.Equal([tank], sim.Giftable(1, tank, turret, doomed, foreign, tank));
    }

    [Fact(DisplayName = "юниты переходят там же, где стоят: работа брошена, турели уходят за носителем")]
    public void UnitsChangeHandsInPlaceAndDropTheirWork()
    {
        using var sim = Make();
        var (tx, ty) = Free(sim, 4, 4);
        var tank = Spawn(sim, "tank", 1, tx, ty);
        var truck = Truck(sim, 1, 6, 6);
        sim.SetHauler(truck, from: 13, to: 14, resource: "metal", amount: 5, full: true, pickup: 12);
        sim.Set(truck, "Hauler.mine", 11);
        sim.Set(truck, "Hauler.route", Scene.Ids(15, 16));
        sim.Set(truck, "Hauler.stop", 1);
        sim.Add(truck, Names.Path.Name);
        sim.Set(truck, "Path.goalX", 9);
        sim.Set(truck, "Path.goalY", 9);
        if (!sim.Has(truck, Names.Tactics.Name))
            sim.Add(truck, Names.Tactics.Name);
        sim.Set(truck, "Tactics.away", 1);
        sim.Set(truck, "Tactics.homeX", 6);
        sim.Set(truck, "Tactics.homeY", 6);
        Account(sim, 1, 2);
        sim.Ally(1, 2);

        Assert.True(sim.HandOver(1, 2, tank, truck));
        // Работа снята, маршрут и патруль забыты, путь брошен — и всё это там же, где юниты стояли.
        var hauler = sim.Hauler(truck);
        Assert.Equal([0u, 0u, 0u, 0u], [hauler.Mine, hauler.Pickup, hauler.From, hauler.To]);
        Assert.Equal([0.0, 0.0], [hauler.Amount, hauler.Stop]);
        Assert.False(hauler.Full);
        Assert.Empty(hauler.Route);
        Assert.False(sim.Has(truck, Names.Path.Name));
        Assert.Equal(0, sim.Num(truck, "Tactics.away"));
        Assert.Equal(2u, sim.OwnerOf(truck));
        // Турели не остаются на союзной технике чужими.
        var turrets = sim.With("Mount", "parent", tank);
        Assert.NotEmpty(turrets);
        foreach (var turret in turrets)
            Assert.Equal(2u, sim.OwnerOf(turret));
    }

    [Fact(DisplayName = "груз остаётся в кузове и довозится уже новому хозяину — в его хранилище")]
    public void CargoStaysInTheBodyAndReachesTheNewOwnersStore()
    {
        using var sim = Make();
        var at = Spot(sim, "metalYard");
        var yard = Place(sim, "metalYard", at.X, at.Y, 2);
        var truck = Truck(sim, 1, at.X - 2, at.Y);
        sim.SetStock(truck, "metal", 20);
        Account(sim, 1, 2);
        sim.Ally(1, 2);

        Assert.True(sim.HandOver(1, 2, truck));
        // Груз не вывалился и не пропал: он в кузове, и грузовик уже намерен его сгрузить.
        Assert.Equal(20, sim.Stock(truck, "metal"));
        var hauler = sim.Hauler(truck);
        Assert.True(hauler.Full);
        Assert.Equal("metal", hauler.Resource);
        Assert.Equal(2u, sim.OwnerOf(truck));

        sim.Seconds(6);
        Assert.Equal(20, sim.Stock(yard, "metal"));
        Assert.Equal(0, sim.Stock(truck, "metal"));
    }

    [Fact(DisplayName = "приказ gift из меню юнитов меняет хозяина в ближайшем тике, а несоюзнику — ничего")]
    public void GiftOrderFromTheMenuChangesTheOwnerNextTick()
    {
        using var sim = Make();
        var truck = Truck(sim, 1, 4, 4);
        var tank = Spawn(sim, "tank", 1, Free(sim, 6, 6).X, Free(sim, 6, 6).Y);
        Account(sim, 1, 2, 3);
        sim.Ally(1, 2);

        // Третьему — не союзник: в очередь команда уходит, но мир её отвергает.
        sim.Send(1, new GiftOrder { Units = [truck], To = 3 }, OrderRights.Remote);
        sim.Advance();
        Assert.Equal(1u, sim.OwnerOf(truck));

        sim.Send(1, new GiftOrder { Units = [truck, tank], To = 2 }, OrderRights.Remote);
        sim.Advance();
        Assert.Equal(2u, sim.OwnerOf(truck));
        Assert.Equal(2u, sim.OwnerOf(tank));
        // Клиенту приказ разрешён: по номерам юнитов от него отказа нет.
        Assert.DoesNotContain(sim.TakeSignals(RefusedSignal.Name), signal => MemoryPackSerializer.Deserialize<RefusedSignal>(signal.Payload)!.Reason == "internal");
    }

    [Fact(DisplayName = "о передаче юнитов весть идёт обеим сторонам, а чужим — молчание")]
    public void BothSidesHearAboutTheGift()
    {
        using var sim = Make();
        var ours = Truck(sim, 1, 4, 4);
        Account(sim, 1, 2);
        sim.TakeSignals();

        // Не союзник: ни перехода, ни вести.
        sim.Send(1, new GiftOrder { Units = [ours], To = 2 });
        sim.Seconds(0.2);
        Assert.Empty(sim.TakeSignals(GiftSignal.Name));
        Assert.Equal(1u, sim.OwnerOf(ours));

        sim.Ally(1, 2);
        sim.Send(1, new GiftOrder { Units = [ours], To = 2 });
        sim.Seconds(0.2);
        var heard = sim.TakeSignals(GiftSignal.Name).Select(signal => (MemoryPackSerializer.Deserialize<GiftSignal>(signal.Payload)!.Kind, signal.To![0])).ToArray();
        Assert.Equal(new[] { (GiftSignal.Sent, 1u), (GiftSignal.Got, 2u) }, heard);
        Assert.Equal(2u, sim.OwnerOf(ours));
        // Пустой отдать нечего — и весть об этом не идёт.
        sim.Send(1, new GiftOrder { Units = [ours], To = 2 });
        sim.Seconds(0.2);
        Assert.Empty(sim.TakeSignals(GiftSignal.Name));
    }

    [Fact(DisplayName = "меню юнитов: «Отдать союзнику» ведёт к списку союзников, и клик по имени отдаёт выбранных", Skip = "ждёт Н6: меню юнитов клиента (commandsOf)")]
    public void UnitMenuLeadsToTheAllyList()
    {
    }

    [Fact(DisplayName = "меню юнитов: без выбранных юнитов отдавать нечего, а без союзников — некому", Skip = "ждёт Н6: меню юнитов клиента (commandsOf)")]
    public void UnitMenuHasNothingToGiveWithoutSelectionOrAllies()
    {
    }
}
