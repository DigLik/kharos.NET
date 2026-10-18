using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Base.Shared.Signals;
using Kharos.Engine.World;
using MemoryPack;
using static Kharos.Base.Tests.Kernel.Scene;

namespace Kharos.Base.Tests.Kernel;

/// <summary>Диспетчер приказов ядра (.NET): книга приказов, права, отказы, очередь Shift, приказ точкой.</summary>
public sealed class DispatcherTests
{
    private static string[] Reasons(Sim sim) =>
        [.. sim.TakeSignals(RefusedSignal.Name).Select(signal => MemoryPackSerializer.Deserialize<RefusedSignal>(signal.Payload)!).Select(refused => $"{refused.Order}: {refused.Reason}")];

    [Fact]
    public void EveryOrderOfTheBookIsDeclared()
    {
        using var sim = Create(64);
        foreach (var name in OrderNames.Commands.Concat(OrderNames.Own).Concat(OrderNames.Edits))
            Assert.True(sim.HasOrder(name), name);
    }

    [Fact]
    public void OrderWithoutSectionHandlerIsRefusedWithItsPackage()
    {
        using var sim = Create(64);
        sim.Send(1, new WorkOrder());
        sim.Send(1, new BuildOrder { Building = "generator" });
        sim.Advance();
        Assert.Equal(["work: ждёт С3", "build: ждёт С1"], Reasons(sim));
    }

    [Fact]
    public void InternalOrdersFromClientsAndEditsWithoutRightsAreRefused()
    {
        using var sim = Create(64);
        sim.Send(1, new AttackOrder { Units = [1], Target = 2 }, OrderRights.Remote);
        sim.Send(1, new EditCreditsOrder { Player = 1, Amount = 500 });
        sim.Send(1, "produce", [1, 2, 3]);
        sim.Advance();
        Assert.Equal(["attack: internal", "edit.credits: rights", "produce: payload"], Reasons(sim));
        Assert.Equal(0, sim.Credits(1));
    }

    [Fact]
    public void ShiftQueuesOrdersAndPlainOrderForgetsThem()
    {
        using var sim = Create(64);
        var tiles = Builders.FreeTilesNear(sim, 0, 0, 2);
        var unit = Spawn(sim, "infantry", 1, tiles[0].X, tiles[0].Y);
        sim.Send(1, new MoveOrder { Units = [unit], X = 10, Y = 10, Queue = true });
        sim.Send(1, new MoveOrder { Units = [unit], X = 12, Y = 10, Queue = true });
        sim.Advance();
        Assert.Equal(["move", "move"], sim.Query<NameList>(KernelQueries.Orders, new EntityArgs { Entity = unit }).Items);
        // Чужой юнит в очередь не встаёт.
        sim.Send(2, new MoveOrder { Units = [unit], X = 1, Y = 1, Queue = true });
        sim.Send(1, new StopOrder { Units = [unit] });
        sim.Advance();
        Assert.Empty(sim.Query<NameList>(KernelQueries.Orders, new EntityArgs { Entity = unit }).Items);
    }

    [Fact]
    public void PointOrderOnEmptyGroundIsMove()
    {
        using var sim = Create(64, fog: false);
        var tiles = Builders.FreeTilesNear(sim, 0, 0, 2);
        var unit = Spawn(sim, "buggy", 1, tiles[0].X, tiles[0].Y);
        var goal = Builders.FreeTilesNear(sim, 20, 0, 1)[0];
        sim.Send(1, new PointOrder { Units = [unit], X = goal.X + 0.4, Y = goal.Y + 0.6 });
        sim.Advance();
        Assert.True(sim.Has(unit, Names.Path.Name));
        Assert.Equal((goal.X, goal.Y), ((int)sim.Num(unit, "Path.goalX"), (int)sim.Num(unit, "Path.goalY")));
    }

    [Fact]
    public void EditOrderRunsOrderForPlayerAtOnce()
    {
        using var sim = Create(64);
        var tiles = Builders.FreeTilesNear(sim, 0, 0, 1);
        var unit = Spawn(sim, "infantry", 1, tiles[0].X, tiles[0].Y);
        sim.OrderNow(1, new MoveOrder { Units = [unit], X = tiles[0].X + 6, Y = tiles[0].Y });
        Assert.True(sim.Has(unit, Names.Path.Name));
        Assert.Equal(0.0, sim.Time);
    }
}
