using Base.Shared;
using Base.Shared.Signals;
using Kharos.Base.Tests.Kernel;
using MemoryPack;
using static Kharos.Base.Tests.Logistics.LogisticsScene;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Trade;

/// <summary>
/// Корабли с подарками (packages/tests/ferry.test.ts). Тесты хоста (чат /give) и панели космопорта клиента ждут своих
/// разделов (Skip); весть о корабле у нас — сигналы ferry обеим сторонам.
/// </summary>
public sealed class FerryTests
{
    /// <summary>Правила партии тестов TS: без защиты новичка и дерева технологий.</summary>
    private static readonly object Rules = new { protection = 0, techTree = false };

    /// <summary>
    /// База игрока на ровном месте: главное здание и здания из списка в ряд, над рядом — труба, чтобы всё было одной сетью.
    /// away — место другой базы: рядом с ней не селимся.
    /// </summary>
    internal static (int X, int Y, List<uint> Buildings, uint Core) MakeBase(Sim sim, uint player, string[] extra, (int X, int Y)? away = null)
    {
        var row = new List<(string Type, int Dx)>();
        var at = 0;
        foreach (var type in new[] { "command" }.Concat(extra))
        {
            row.Add((type, at));
            at += Width(type) + 1;
        }
        for (var y = -100; y < 100; y++)
            for (var x = -100; x < 100; x++)
            {
                if (away is { } far && Math.Sqrt(Math.Pow(x - far.X, 2) + Math.Pow(y - far.Y, 2)) < 60)
                    continue;
                if (!row.All(item => Rock(sim, x + item.Dx, y, Width(item.Type), (int)Tables.Buildings[item.Type].Height))
                    || !Enumerable.Range(0, at).All(dx => Rock(sim, x + dx, y - 1, 1, 1)))
                    continue;
                if (!row.All(item => Scene.CanPlace(sim, item.Type, x + item.Dx, y)) || !Enumerable.Range(0, at).All(dx => Scene.CanPlace(sim, "pipe", x + dx, y - 1)))
                    continue;
                var buildings = row.Select(item => sim.PutBuilding(item.Type, x + item.Dx, y, player)).ToList();
                for (var dx = 0; dx < at; dx++)
                    sim.PutBuilding("pipe", x + dx, y - 1, player);
                return (x, y, buildings, buildings[0]);
            }
        throw new InvalidOperationException("Не нашлось места под базу");
    }

    /// <summary>Партия из двух баз и счетов трёх игроков: у второй главное здание и двор.</summary>
    private static (Sim Sim, (int X, int Y, List<uint> Buildings, uint Core) One, (int X, int Y, List<uint> Buildings, uint Core) Two) Make(string[] first, string[] second)
    {
        var sim = Scene.Create(128, Rules);
        var one = MakeBase(sim, 1, first);
        var two = MakeBase(sim, 2, second, (one.X, one.Y));
        return (sim, one, two);
    }

    [Fact(DisplayName = "корабль с подарком выходит только к союзнику: не себе, не пустым, не в долг и только с готового свободного порта")]
    public void ShipGoesOnlyToAnAllyFromAFreePort()
    {
        var (sim, one, two) = Make(["spaceport", "metalYard"], ["metalYard"]);
        using var _sim = sim;
        var (_, port, yard) = (one.Buildings[0], one.Buildings[1], one.Buildings[2]);
        Assert.NotEqual(0u, two.Core);
        sim.SetCredits(1, 1000);
        // Игроки 2 и 3 — с пустыми счетами: без Player их и в союз не позвать.
        sim.SetCredits(2, 0);
        sim.SetCredits(3, 0);
        sim.SetStock(yard, "metal", 200);

        // Не союзник — подарок не летит.
        Assert.False(sim.SendFerry(1, port, 2, 100));
        sim.Ally(1, 2);
        // Себе и не союзнику нельзя; пустой корабль и долг — тоже не отправка.
        Assert.False(sim.SendFerry(1, port, 1, 100));
        Assert.False(sim.SendFerry(1, port, 3, 100));
        Assert.False(sim.SendFerry(1, port, 2, 0));
        Assert.False(sim.SendFerry(1, port, 2, -5));
        Assert.False(sim.SendFerry(1, port, 2, 5000));
        Assert.Equal(1000, sim.Credits(1));
        // Союзнику без главного здания выгружать некуда: подарок отклоняется, и деньги остаются при нём.
        sim.Ally(1, 3);
        Assert.False(sim.SendFerry(1, port, 3, 50));
        Assert.Equal(1000, sim.Credits(1));

        Assert.True(sim.CanFerry(1, port));
        Assert.True(sim.SendFerry(1, port, 2, 100, ("metal", 200)));
        // Отдаётся сразу: деньги списаны, груз ушёл из хранилищ, и всё это уже в пути.
        Assert.Equal(900, sim.Credits(1));
        Assert.Equal(0, sim.Stock(yard, "metal"));
        var transfer = sim.TransferOf(port);
        Assert.True(transfer.Present);
        Assert.Equal((2u, 100.0, 200.0, false), (transfer.To, transfer.Credits, transfer.Goods["metal"], transfer.Done));
        Assert.Equal(transfer.Total, transfer.Left);
        Assert.True(transfer.Left > 0);
        Assert.Equal(sim.At(two.Core).X + 1, transfer.X);
        // Пока корабль летит, космопорт занят: ни продать, ни послать второй подарок.
        Assert.False(sim.CanSell(1, port));
        Assert.False(sim.Sell(1, port, "metal", 10));
        Assert.False(sim.SendFerry(1, port, 2, 0, ("metal", 1)));
        Assert.Equal(one.Core, one.Buildings[0]);
    }

    [Fact(DisplayName = "корабль долетает: деньги и груз — получателю, а что не вошло в хранилища — падает у его главного здания")]
    public void ShipArrivesAndTheOverflowFallsNearTheCore()
    {
        // Два двора у отправителя: трюм корабля не бездонный, и в подарок уходит не больше 500 одного груза.
        var (sim, one, two) = Make(["spaceport", "metalYard", "metalYard"], ["metalYard"]);
        using var _sim = sim;
        var (port, firstYard, secondYard) = (one.Buildings[1], one.Buildings[2], one.Buildings[3]);
        var yard = two.Buildings[1];
        sim.SetCredits(1, 1000);
        sim.SetCredits(2, 0);
        sim.SetStock(firstYard, "metal", 300);
        sim.SetStock(secondYard, "metal", 300);
        sim.Ally(1, 2);
        var before = sim.Credits(2);

        Assert.True(sim.SendFerry(1, port, 2, 250, ("metal", 600)));
        var transfer = sim.TransferOf(port);
        Assert.Equal(500, transfer.Goods["metal"]);
        Assert.Equal(100, sim.Stock(firstYard, "metal") + sim.Stock(secondYard, "metal"));

        var ticks = sim.FlightTicks(port, TradeSeconds);
        sim.Advance(ticks - 1);
        // До последнего тика полёта корабль в пути.
        Assert.False(sim.TransferOf(port).Done);
        sim.Advance();
        // Подарок пришёл целиком; сверх него у получателя капает свой доход с главного здания.
        Assert.True(sim.Credits(2) >= before + 250);
        // Двор получателя берёт 400 — больше в него не влезет; остаток ложится на землю у главного здания.
        Assert.Equal(400, sim.Stock(yard, "metal"));
        var spill = Assert.Single(sim.Drops());
        Assert.Equal(100, sim.Stock(spill, "metal"));
        Assert.Equal(((double)transfer.X, (double)transfer.Y), sim.At(spill));
        var arrived = sim.TransferOf(port);
        Assert.True(arrived.Done);
        Assert.Equal(0, arrived.Credits);
        // Долетевший корабль больше ничего не везёт и не отсчитывает.
        sim.Seconds(1);
        var rest = sim.TransferOf(port);
        Assert.True(rest.Done);
        Assert.Equal((0.0, 0.0), (rest.Credits, rest.Left));
        Assert.Empty(rest.Goods);
    }

    [Fact(DisplayName = "недолёт переживает сохранение: после загрузки корабль довозит и деньги, и груз")]
    public void ShipInFlightSurvivesASave()
    {
        var (sim, one, two) = Make(["spaceport", "metalYard"], ["metalYard"]);
        using var _sim = sim;
        var (port, yard) = (one.Buildings[1], one.Buildings[2]);
        var theirYard = two.Buildings[1];
        sim.SetCredits(1, 1000);
        sim.SetCredits(2, 0);
        sim.SetStock(yard, "metal", 60);
        sim.Ally(1, 2);
        Assert.True(sim.SendFerry(1, port, 2, 40, ("metal", 60)));
        sim.Seconds(3);
        var left = sim.TransferOf(port).Left;
        Assert.True(left > 0);
        Assert.True(left < sim.FlightTicks(port, TradeSeconds));

        var before = sim.Credits(2);
        using var loaded = sim.ThroughSave();
        Assert.Equal(left, loaded.TransferOf(port).Left);
        loaded.Advance((int)left + 1);
        Assert.True(loaded.Credits(2) >= before + 40);
        Assert.Equal(60, loaded.Stock(theirYard, "metal"));
        Assert.True(loaded.TransferOf(port).Done);
    }

    [Fact(DisplayName = "/give переводит деньги союзнику сразу, а чужим и в долг — отказывает", Skip = "ждёт Н6/С9: чат хоста (/give, /ally) — в моде этой команды нет")]
    public void GiveCommandTransfersMoney()
    {
    }

    [Fact(DisplayName = "о корабле с подарком весть идёт обеим сторонам: вышел — и прибыл")]
    public void BothSidesHearAboutTheShip()
    {
        var (sim, one, _) = Make(["spaceport"], []);
        using var _sim = sim;
        var port = one.Buildings[1];
        sim.SetCredits(1, 500);
        sim.SetCredits(2, 0);
        sim.Ally(1, 2);
        var before = sim.Credits(2);
        sim.TakeSignals();

        Assert.True(sim.SendFerry(1, port, 2, 100));
        sim.Seconds(0.5);
        Assert.Equal([(FerrySignal.Sent, 1u), (FerrySignal.Coming, 2u)], Heard(sim));
        Assert.False(sim.TransferOf(port).Done);

        sim.Seconds(TradeSeconds + 1);
        Assert.Equal([(FerrySignal.Arrived, 1u), (FerrySignal.Got, 2u)], Heard(sim));
        // Пришло ровно сто и ни копейкой меньше: своя казна у получателя ещё и доход с главного здания даёт.
        Assert.True(sim.Credits(2) >= before + 100);
        // Корабль долетел и ушёл: космопорт снова свободен.
        Assert.True(sim.CanSell(1, port));
    }

    /// <summary>Вести о кораблях за прошедшее время: что и кому.</summary>
    private static (string Kind, uint To)[] Heard(Sim sim) =>
        [.. sim.TakeSignals(FerrySignal.Name).Select(signal => (MemoryPackSerializer.Deserialize<FerrySignal>(signal.Payload)!.Kind, signal.To![0]))];

    private static double TradeSeconds => global::Base.Shared.Rules.TradeRules.SellSeconds;

    [Fact(DisplayName = "панель космопорта: подарок союзнику набирается грузом и деньгами и уходит кораблём", Skip = "ждёт Н6: панель космопорта клиента (commandsOf)")]
    public void PortPanelBuildsTheGift()
    {
    }

    [Fact(DisplayName = "космопорт занят или союзников нет: подарок не начать", Skip = "ждёт Н6: панель космопорта клиента (commandsOf)")]
    public void PortPanelBlocksWhenBusyOrAlone()
    {
    }

    [Fact(DisplayName = "панель подарка не обещает взять больше вместимости корабля", Skip = "ждёт Н6: панель космопорта клиента (commandsOf)")]
    public void GiftPanelRespectsShipCapacity()
    {
    }
}
