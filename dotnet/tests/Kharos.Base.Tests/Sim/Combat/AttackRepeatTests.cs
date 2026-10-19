using Base.Shared;
using Base.Shared.Orders;
using Kharos.Base.Tests.Kernel;

namespace Kharos.Base.Tests.Combat;

/// <summary>
/// Повтор приказа атаки (не из TS, сознательное отступление). В TS каждый приказ атаки снимает путь, а гонящийся без пути
/// ищет новый лишь в свою очередь раз в SCAN_TICKS (5) тиков. Игрок, который щёлкает по цели чаще, или напоминание раз в
/// 5 тиков, выпавшее на тик после очереди юнита, оставляли юнит на месте навсегда. Здесь тот же приказ по той же цели путь
/// погони не трогает; другая цель снимает его, как прежде.
/// </summary>
public sealed class AttackRepeatTests
{
    private static Sim Field(out uint tank, out uint wall, out uint other)
    {
        var sim = Sim.Create(new SimOptions { Size = 128, Land = new { blank = true }, Rules = new { protection = 0 } });
        wall = sim.PutBuilding("wall", 70, 40, 2);
        other = sim.PutBuilding("wall", 70, 60, 2);
        tank = sim.PutUnit("tank", 1, 20.5, 40.5);
        sim.SetFacing(0, tank);
        return sim;
    }

    [Theory(DisplayName = "приказ атаки по той же цели раз в every тиков (со сдвигом phase) не держит юнит на месте: он доезжает и бьёт")]
    [InlineData(1, 0)]
    [InlineData(5, 0)]
    [InlineData(5, 1)]
    [InlineData(5, 2)]
    [InlineData(5, 3)]
    [InlineData(5, 4)]
    public void RepeatedOrderKeepsTheChase(int every, int phase)
    {
        Scene.SkipUnlessAvailable();
        using var sim = Field(out var tank, out var wall, out _);
        var start = sim.At(tank).X;
        var hit = false;
        for (var tick = 0; tick < Sim.TicksIn(60) && !hit; tick++)
        {
            if ((tick + phase) % every == 0)
                sim.Send(1, new AttackOrder { Units = [tank], Target = wall });
            sim.Advance();
            hit = sim.HealthOf(wall) < 1;
        }
        Assert.True(sim.At(tank).X > start + 10, $"танк не доехал: x = {sim.At(tank).X}");
        Assert.True(hit, "танк так и не выстрелил по стене");
    }

    [Fact(DisplayName = "повтор приказа по той же цели оставляет путь погони, приказ по другой цели его снимает")]
    public void NewTargetResetsThePath()
    {
        Scene.SkipUnlessAvailable();
        using var sim = Field(out var tank, out var wall, out var other);
        // Танк воюет турелью: цель — у неё, путь — у носителя.
        var gun = sim.TurretsOf(tank).Single();
        Assert.True(sim.Now(1, new AttackOrder { Units = [tank], Target = wall }));
        Assert.True(sim.Until(() => sim.Has(tank, Names.Path.Name), 2), "погоня не проложила путь");
        sim.Advance(3);
        Assert.True(sim.Has(tank, Names.Path.Name));
        var goal = (sim.Num(tank, Names.Path.Name, Names.Path.GoalX), sim.Num(tank, Names.Path.Name, Names.Path.GoalY));

        Assert.True(sim.Now(1, new AttackOrder { Units = [tank], Target = wall }));
        Assert.True(sim.Has(tank, Names.Path.Name), "повтор приказа снял путь");
        Assert.Equal(goal, (sim.Num(tank, Names.Path.Name, Names.Path.GoalX), sim.Num(tank, Names.Path.Name, Names.Path.GoalY)));
        Assert.Equal(wall, sim.TargetOf(gun));

        Assert.True(sim.Now(1, new AttackOrder { Units = [tank], Target = other }));
        Assert.False(sim.Has(tank, Names.Path.Name), "приказ по другой цели оставил прежний путь");
        Assert.Equal(other, sim.TargetOf(gun));
    }
}
