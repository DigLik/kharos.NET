using Base.Shared;
using Base.Shared.Orders;
using Kharos.Base.Tests.Construction;
using MemoryPack;

namespace Kharos.Base.Tests.Tactics;

/// <summary>Очередь приказов Shift и приказы с мусором (packages/tests/orders.test.ts).</summary>
public sealed class OrdersTests
{
    /// <summary>Сколько приказов может ждать в очереди юнита (ORDERS_LIMIT).</summary>
    private const int OrdersLimit = 32;

    /// <summary>Первые count юнитов, кроме MCV (squad).</summary>
    private static uint[] Squad(Sim sim, int count)
    {
        var mcv = Kernel.Scene.Proto(sim, "units/mcv");
        return [.. sim.With(Names.Unit.Name).Where(entity => sim.Num(entity, Names.Proto.Name, Names.Proto.Id) != mcv).Take(count)];
    }

    private static void Move(Sim sim, uint[] units, int x, int y, bool queue = false) => sim.Send(1, new MoveOrder { Units = units, X = x, Y = y, Queue = queue });

    [Fact(DisplayName = "Shift+ПКМ: точки по очереди, приказ без Shift забывает очередь")]
    public void ShiftQueuesPoints()
    {
        using var yard = Yard.Start();
        var (sim, site) = (yard.Sim, yard.Site);
        var units = Squad(sim, 2);
        Move(sim, units, site.X, site.Y);
        Move(sim, units, site.X + 4, site.Y + 5, queue: true);
        sim.Seconds(0.2);
        Assert.Equal(1, sim.QueueLength(units[0]));
        sim.Seconds(20);
        Assert.Equal(0, sim.QueueLength(units[0]));
        foreach (var entity in units)
            Assert.True(Sim.Distance(sim.At(entity), (site.X + 4, site.Y + 5)) < 3);

        Move(sim, units, site.X, site.Y);
        Move(sim, units, site.X + 4, site.Y, queue: true);
        sim.Seconds(0.2);
        Move(sim, units, site.X + 1, site.Y + 1);
        sim.Seconds(0.2);
        Assert.Equal(0, sim.QueueLength(units[0]));
    }

    [Fact(DisplayName = "приказ группы ждёт всех: пока один едет, никто не берёт следующий")]
    public void GroupWaitsForAll()
    {
        using var yard = Yard.Start();
        var (sim, site) = (yard.Sim, yard.Site);
        var units = Squad(sim, 2);
        var (a, b) = (units[0], units[1]);
        // b занят своим путём — a стоит и ждёт.
        Move(sim, [b], site.X + 6, site.Y + 6);
        Move(sim, [a, b], site.X + 4, site.Y + 5, queue: true);
        sim.Seconds(0.1);
        Assert.True(sim.Has(b, Names.Path.Name));
        Assert.False(sim.Has(a, Names.Path.Name));
    }

    [Fact(DisplayName = "очередь построек: стройка закладывается сразу, строитель берётся за неё, закончив прежнее")]
    public void BuildQueue()
    {
        using var yard = Yard.Start();
        var (sim, site) = (yard.Sim, yard.Site);
        var builder = yard.Builders[0];
        Move(sim, [builder], site.X + 3, site.Y + 6);
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [builder], Queue = true });
        sim.Seconds(0.1);
        var placed = sim.SiteAt(site.X, site.Y);
        Assert.NotEqual(0u, placed);
        Assert.False(sim.Has(builder, Names.Builds.Name));
        var queue = sim.QueueOf(builder);
        Assert.Equal(AssistOrder.Name, queue.Names[0]);
        Assert.Equal(placed, MemoryPackSerializer.Deserialize<AssistOrder>(queue.Payloads[0])!.Site);
        sim.Seconds(20);
        // Строитель на этой стройке или она уже готова.
        var taken = sim.Has(builder, Names.Builds.Name) ? sim.Num(builder, Names.Builds.Name, Names.Builds.Site) : sim.Has(placed, Names.Site.Name) ? -1 : placed;
        Assert.Equal(placed, taken);
    }

    [Fact(DisplayName = "стройка из меню: строитель на своей стройке берёт новую в очередь, а ПКМ по ней — бросает всё и идёт")]
    public void MenuBuildQueuesForBusyBuilder()
    {
        using var yard = Yard.Start();
        var (sim, site) = (yard.Sim, yard.Site);
        var builder = yard.Builders[0];
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [builder] });
        sim.Seconds(0.1);
        var first = sim.SiteAt(site.X, site.Y);
        Assert.Equal(first, (uint)sim.Num(builder, Names.Builds.Name, Names.Builds.Site));

        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y + 3, Builders = [builder] });
        sim.Seconds(0.1);
        var second = sim.SiteAt(site.X, site.Y + 3);
        Assert.NotEqual(0u, second);
        Assert.Equal(first, (uint)sim.Num(builder, Names.Builds.Name, Names.Builds.Site));
        var queue = sim.QueueOf(builder);
        Assert.Equal(AssistOrder.Name, queue.Names[0]);
        Assert.Equal(second, MemoryPackSerializer.Deserialize<AssistOrder>(queue.Payloads[0])!.Site);

        sim.Send(1, new AssistOrder { Units = [builder], Site = second });
        sim.Seconds(0.1);
        Assert.Equal(second, (uint)sim.Num(builder, Names.Builds.Name, Names.Builds.Site));
        Assert.Equal(0, sim.QueueLength(builder));
    }

    [Fact(DisplayName = "негодный приказ очередь не стирает; принятый — стирает")]
    public void BadOrderKeepsQueue()
    {
        using var yard = Yard.Start();
        var (sim, site) = (yard.Sim, yard.Site);
        var units = Squad(sim, 2);
        Move(sim, units, site.X, site.Y);
        Move(sim, units, site.X + 4, site.Y, queue: true);
        sim.Seconds(0.1);
        // Атаковать некого: цели нет.
        sim.Send(1, new AttackOrder { Units = units, Target = 999999 });
        sim.Seconds(0.1);
        Assert.Equal(1, sim.QueueLength(units[0]));
    }

    [Fact(DisplayName = "очередь приказов ограничена, а приказ в ней не хранит список юнитов")]
    public void QueueIsLimited()
    {
        using var yard = Yard.Start();
        var (sim, site) = (yard.Sim, yard.Site);
        var units = Squad(sim, 2);
        Move(sim, units, site.X, site.Y);
        for (var i = 0; i < OrdersLimit + 10; i++)
            Move(sim, units, site.X + i % 4, site.Y, queue: true);
        sim.Seconds(0.1);
        var queue = sim.QueueOf(units[0]);
        Assert.Equal(OrdersLimit, queue.Names.Length);
        Assert.Empty(MemoryPackSerializer.Deserialize<MoveOrder>(queue.Payloads[0])!.Units);
        // Взятый из очереди приказ получает всю группу.
        Move(sim, units, site.X + 1, site.Y + 1);
        Move(sim, units, site.X + 4, site.Y + 5, queue: true);
        sim.Seconds(20);
        foreach (var entity in units)
            Assert.True(Sim.Distance(sim.At(entity), (site.X + 4, site.Y + 5)) < 3);
    }

    [Fact(DisplayName = "координаты команд проверяются на границы карты: точка сбора, патруль")]
    public void CoordinatesAreChecked()
    {
        using var yard = Yard.Start();
        var (sim, core) = (yard.Sim, yard.Core);
        var units = Squad(sim, 1);
        var map = sim.Host.Map!.Value;
        foreach (var (x, y) in new[] { (map.Right + 100, 0), (0, map.Bottom + 5000), (1_000_000_000, -1_000_000_000) })
            sim.Send(1, new RallyOrder { Building = core, X = x, Y = y });
        sim.Send(1, new PatrolOrder { Units = units, Points = [map.Right + 10, 0, 1e9, 1e9, double.NaN, 0.5, 1.5, 2] });
        sim.Seconds(0.1);
        Assert.Equal(0, sim.Num(core, "Producer.rally"));
        Assert.Empty(sim.PatrolOf(units[0]));
    }

    [Fact(DisplayName = "мусор в полях команды не роняет тик: команда отбрасывается, и сразу, и из очереди")]
    public void JunkDoesNotCrashTick()
    {
        using var yard = Yard.Start();
        var (sim, builders) = (yard.Sim, yard.Builders.ToArray());
        byte[][] junk = [[], [1, 2, 3]];
        foreach (var name in new[] { MoveOrder.Name, BuildOrder.Name, RallyOrder.Name, SellOrder.Name, PatrolOrder.Name, PaveOrder.Name, PointOrder.Name, StanceOrder.Name })
            foreach (var payload in junk)
                sim.Send(1, name, payload);
        foreach (var queue in new[] { false, true })
        {
            sim.Send(1, new PointOrder { Units = builders, X = double.NaN, Y = double.PositiveInfinity, Queue = queue });
            sim.Send(1, new MoveOrder { Units = builders, X = int.MinValue, Y = int.MaxValue, Queue = queue });
            sim.Send(1, new BuildOrder { Building = "no-such-building", X = int.MaxValue, Y = -1, Builders = builders, Queue = queue });
            sim.Send(1, new PaveOrder { Kind = "nonsense", Tiles = [int.MaxValue, 1], Builders = builders, Queue = queue });
        }
        sim.Send(1, new PatrolOrder { Units = builders, Points = [double.NaN, double.NaN, double.MaxValue, double.MinValue] });
        Assert.Null(Record.Exception(() => sim.Seconds(2)));
    }

    [Fact(DisplayName = "приказ точкой строителю: по своей стройке — строить, мимо неё — идти")]
    public void PointOrderToBuilder()
    {
        using var yard = Yard.Start();
        var (sim, site) = (yard.Sim, yard.Site);
        var (first, second) = (yard.Builders[0], yard.Builders[1]);
        sim.Send(1, new BuildOrder { Building = "generator", X = site.X, Y = site.Y, Builders = [first] });
        sim.Advance();
        var placed = sim.SiteAt(site.X, site.Y);
        Assert.NotEqual(0u, placed);
        sim.Send(1, new PointOrder { Units = [second], X = site.X + 0.5, Y = site.Y + 0.5 });
        sim.Advance();
        Assert.Equal(placed, (uint)sim.Num(second, Names.Builds.Name, Names.Builds.Site));
        Assert.Equal(1, sim.Num(second, Names.Builds.Name, Names.Builds.Ordered));
        sim.Send(1, new PointOrder { Units = [second], X = site.X + 6.5, Y = site.Y + 6.5 });
        sim.Advance();
        Assert.False(sim.Has(second, Names.Builds.Name));
        Assert.Equal(site.X + 6, sim.Num(second, Names.Path.Name, Names.Path.GoalX));
        Assert.Equal(site.Y + 6, sim.Num(second, Names.Path.Name, Names.Path.GoalY));
    }
}
