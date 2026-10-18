using System.Text;
using Base.Host;
using MemoryPack;

namespace Kharos.Base.Tests;

/// <summary>Производство за кредиты (mechanics/production): заказ, очередь, отмена, точка сбора, права.</summary>
public class ProductionTests
{
    /// <summary>Тиков на сборку багги (7 с): сумма dt по 0,05 доходит до 7 лишь на 141-м тике.</summary>
    private static readonly int BuildTicks = BaseGame.TicksFor(7);

    private static void Near((double X, double Y) expected, (double X, double Y) actual) =>
        Assert.True(BaseGame.Distance(expected, actual) < 1e-9, $"ожидалось {expected}, есть {actual}");

    [Fact]
    public void OrderSpendsCreditsAndUnitComesOutAfterBuildTime()
    {
        using var host = BaseGame.Started(1);
        var factory = BaseGame.Factory(host, 1);
        var before = BaseGame.Units(host, 1);
        var signals = new List<(string Name, byte[] Payload)>();
        Assert.Equal(141, BuildTicks);

        BaseGame.Produce(host, 1, factory);
        BaseGame.Step(host, signals);
        Assert.Equal(750, BaseGame.Credits(host, 1));
        Assert.Equal("buggy", BaseGame.Str(host, factory, "Producer", "unit"));
        // Приказ идёт до систем: в тот же тик produce уже прибавил один dt.
        Assert.Equal(BaseGame.Dt, BaseGame.Num(host, factory, "Producer", "progress"));
        var ordered = MemoryPackSerializer.Deserialize<OrderedSignal>(Assert.Single(signals, s => s.Name == "ordered").Payload)!;
        Assert.Equal((1u, factory, "buggy"), (ordered.Player, ordered.Building, ordered.Unit));

        // Юнит не выходит раньше времени: ровно через BuildTicks тиков после приказа.
        BaseGame.Run(host, BuildTicks - 2, signals);
        Assert.Equal(before.Count, BaseGame.Units(host, 1).Count);
        Assert.DoesNotContain(signals, s => s.Name == "produced");
        BaseGame.Step(host, signals);
        var units = BaseGame.Units(host, 1);
        Assert.Equal(before.Count + 1, units.Count);
        var made = units.Except(before).Single();

        // У ворот (exit завода 0; 2,2), смотрит и едет к точке в 1,5 тайла за воротами: в тот же тик ходовая
        // (она после производства) уже сдвинула его на шаг 6 × 0,05 = 0,3 вперёд.
        Near((0.0, 2.2 + 6 * BaseGame.Dt), BaseGame.At(host, made));
        Assert.Equal(Math.Atan2(1.5, 0), BaseGame.Angle(host, made));
        Assert.Equal(1, BaseGame.Num(host, made, "Drive", "moving"));
        Assert.Equal((0.0, 2.2 + 1.5), (BaseGame.Num(host, made, "Drive", "tx"), BaseGame.Num(host, made, "Drive", "ty")));
        Assert.Equal(1, BaseGame.Num(host, made, "Owner", "player"));
        Assert.Equal(host.Content.FindProto("units/buggy")!.Id, BaseGame.Num(host, made, "Proto", "id"));
        // Турель на новой машине — в тот же тик (событие spawned).
        Assert.Single(BaseGame.Turrets(host), t => BaseGame.Num(host, t, "Attached", "parent") == made);
        var produced = MemoryPackSerializer.Deserialize<ProducedSignal>(Assert.Single(signals, s => s.Name == "produced").Payload)!;
        Assert.Equal((1u, factory, "buggy", made, 0.0, 2.2), (produced.Player, produced.Building, produced.Unit, produced.Entity, produced.X, produced.Y));
        Assert.Equal("", BaseGame.Str(host, factory, "Producer", "unit"));
        Assert.Equal(0, BaseGame.Num(host, factory, "Producer", "progress"));
        // Доход: раз в 20 тиков по 5 кредитов за всё время (BuildTicks тиков).
        Assert.Equal(750 + 5 * (BuildTicks / 20), BaseGame.Credits(host, 1), 9);
        Assert.Empty(host.Errors);

        // Новая машина едет: через пару секунд она у цели и стоит.
        BaseGame.Run(host, 40);
        Assert.Equal(0, BaseGame.Num(host, made, "Drive", "moving"));
        // Точка в 0,3 от стоящих у завода багги: расталкивание сдвигает новую машину чуть в сторону.
        Assert.True(BaseGame.Distance((0.0, 3.7), BaseGame.At(host, made)) < 0.5);
    }

    [Fact]
    public void QueueTakesFiveOrdersAndRunsOneByOne()
    {
        using var host = BaseGame.Started(1);
        var factory = BaseGame.Factory(host, 1);
        BaseGame.SetNum(host, BaseGame.Wallet(host, 1), "Player", "credits", 10000);
        for (var k = 0; k < 7; k++)
            BaseGame.Produce(host, 1, factory);
        BaseGame.Step(host);
        // Текущий и четыре в очереди; шестой и седьмой не приняты и не оплачены.
        Assert.Equal(10000 - 5 * 250, BaseGame.Credits(host, 1));
        Assert.Equal(["buggy", "buggy", "buggy", "buggy"], Encoding.UTF8.GetString(BaseGame.Bytes(host, factory, "Producer", "queue")).Split('\n'));

        var count = BaseGame.Units(host, 1).Count;
        BaseGame.Run(host, BuildTicks - 1);
        Assert.Equal(count + 1, BaseGame.Units(host, 1).Count);
        // Следующий из очереди начался с нуля.
        Assert.Equal("buggy", BaseGame.Str(host, factory, "Producer", "unit"));
        Assert.Equal(0, BaseGame.Num(host, factory, "Producer", "progress"));
        Assert.Equal(3, Encoding.UTF8.GetString(BaseGame.Bytes(host, factory, "Producer", "queue")).Split('\n').Length);
        BaseGame.Run(host, BuildTicks);
        Assert.Equal(count + 2, BaseGame.Units(host, 1).Count);
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void NotEnoughCreditsRefusesWithSignal()
    {
        using var host = BaseGame.Started(1);
        var factory = BaseGame.Factory(host, 1);
        BaseGame.SetNum(host, BaseGame.Wallet(host, 1), "Player", "credits", 249);
        var signals = new List<(string Name, byte[] Payload)>();
        BaseGame.Produce(host, 1, factory);
        BaseGame.Step(host, signals);
        var refused = MemoryPackSerializer.Deserialize<RefusedSignal>(Assert.Single(signals, s => s.Name == "refused").Payload)!;
        Assert.Equal((1u, "credits"), (refused.Player, refused.Reason));
        Assert.DoesNotContain(signals, s => s.Name == "ordered");
        Assert.Equal(249, BaseGame.Credits(host, 1));
        Assert.Equal("", BaseGame.Str(host, factory, "Producer", "unit"));
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void CancelRemovesLastOrderAndRefunds()
    {
        using var host = BaseGame.Started(1);
        var factory = BaseGame.Factory(host, 1);
        var wallet = BaseGame.Wallet(host, 1);
        BaseGame.SetNum(host, wallet, "Player", "credits", 1000);
        for (var k = 0; k < 3; k++)
            BaseGame.Produce(host, 1, factory);
        BaseGame.Step(host);
        Assert.Equal(250, BaseGame.Credits(host, 1));
        string[] Queue() => Encoding.UTF8.GetString(BaseGame.Bytes(host, factory, "Producer", "queue")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, Queue().Length);

        // Сначала снимается последний из очереди, затем текущий; пустое здание — отмена ничего не делает.
        BaseGame.Cancel(host, 1, factory);
        BaseGame.Step(host);
        Assert.Equal((1, 500.0), (Queue().Length, BaseGame.Credits(host, 1)));
        BaseGame.Cancel(host, 1, factory);
        BaseGame.Step(host);
        Assert.Equal((0, 750.0), (Queue().Length, BaseGame.Credits(host, 1)));
        Assert.Equal("buggy", BaseGame.Str(host, factory, "Producer", "unit"));
        BaseGame.Cancel(host, 1, factory);
        BaseGame.Step(host);
        Assert.Equal("", BaseGame.Str(host, factory, "Producer", "unit"));
        Assert.Equal(0, BaseGame.Num(host, factory, "Producer", "progress"));
        Assert.Equal(1000, BaseGame.Credits(host, 1));
        BaseGame.Cancel(host, 1, factory);
        BaseGame.Step(host);
        Assert.Equal(1000, BaseGame.Credits(host, 1));
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void RallyPointSendsNewUnitThere()
    {
        using var host = BaseGame.Started(1);
        var factory = BaseGame.Factory(host, 1);
        BaseGame.Rally(host, 1, factory, 10, 12);
        BaseGame.Produce(host, 1, factory);
        BaseGame.Step(host);
        Assert.Equal(1, BaseGame.Num(host, factory, "Producer", "rally"));
        Assert.Equal((10.0, 12.0), (BaseGame.Num(host, factory, "Producer", "rallyX"), BaseGame.Num(host, factory, "Producer", "rallyY")));
        var before = BaseGame.Units(host, 1);
        BaseGame.Run(host, BuildTicks - 1);
        var made = BaseGame.Units(host, 1).Except(before).Single();
        var angle = Math.Atan2(12 - 2.2, 10 - 0);
        Near((Math.Cos(angle) * 0.3, 2.2 + Math.Sin(angle) * 0.3), BaseGame.At(host, made));
        Assert.Equal((10.0, 12.0), (BaseGame.Num(host, made, "Drive", "tx"), BaseGame.Num(host, made, "Drive", "ty")));
        Assert.Equal(Math.Atan2(12 - 2.2, 10 - 0), BaseGame.Angle(host, made));
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void OnlyOwnerOrdersOwnFactoryAndOnlyKnownUnits()
    {
        using var host = BaseGame.Started(1, 2);
        var factory = BaseGame.Factory(host, 1);
        // Чужой игрок, неизвестный вид, не здание, мёртвая сущность, мусор в payload — всё мимо, без списаний.
        BaseGame.Produce(host, 2, factory);
        BaseGame.Rally(host, 2, factory, 5, 5);
        BaseGame.Cancel(host, 2, factory);
        BaseGame.Produce(host, 1, factory, "tank");
        BaseGame.Produce(host, 1, BaseGame.Units(host, 1)[0]);
        BaseGame.Produce(host, 1, 99999);
        BaseGame.Rally(host, 1, factory, double.NaN, 3);
        host.Send(1, "produce", [1, 2, 3, 250, 17]);
        host.Send(1, "move", []);
        BaseGame.Step(host);
        Assert.Equal(1000, BaseGame.Credits(host, 1));
        Assert.Equal(1000, BaseGame.Credits(host, 2));
        Assert.Equal("", BaseGame.Str(host, factory, "Producer", "unit"));
        Assert.Equal(0, BaseGame.Num(host, factory, "Producer", "rally"));
        Assert.Empty(host.Errors);
    }
}
