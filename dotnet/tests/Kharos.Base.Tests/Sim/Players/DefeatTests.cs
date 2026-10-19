using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Signals;
using Kharos.Base.Tests.Kernel;
using MemoryPack;

namespace Kharos.Base.Tests.Players;

/// <summary>Поражение, сдача и уничтожение своих юнитов (packages/tests/defeat.test.ts), начать заново и весть о поражении.</summary>
public sealed class DefeatTests
{
    /// <summary>Стартовый набор игрока 1 на скале, MCV развёрнут, прошло 6 секунд (start).</summary>
    private static Sim Start()
    {
        var (sim, _, _) = Scene.Deployed();
        sim.Seconds(6 - 3.1);
        return sim;
    }

    /// <summary>Свободный проходимый тайл рядом с (x, y).</summary>
    private static (int X, int Y) Free(Sim sim, int x, int y)
    {
        for (var radius = 0; radius < 40; radius++)
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == radius && sim.Walkable(x + dx, y + dy) && Scene.Occupant(sim, x + dx, y + dy) == 0)
                        return (x + dx, y + dy);
        throw new InvalidOperationException("Не нашлось места под юнита");
    }

    [Fact(DisplayName = "сдавшийся взрывает всё своё волной и проигрывает")]
    public void SurrenderBlowsUpEverything()
    {
        using var sim = Start();
        Assert.False(sim.IsDefeated(1));
        var before = sim.Owned(1);
        sim.Send(1, new SurrenderOrder());
        sim.Advance();
        Assert.Equal(0, sim.Credits(1));
        // Не всё сразу: волна идёт от главного здания.
        sim.Seconds(0.3);
        Assert.Equal(before, sim.Owned(1));
        var blasts = 0;
        for (var i = 0; i < Sim.TicksIn(6); i++)
        {
            sim.Advance();
            blasts += sim.Count(Names.Blast.Name);
        }
        Assert.Equal(0, sim.Owned(1));
        Assert.True(blasts > 0);
        Assert.True(sim.IsDefeated(1));
        // Весть о поражении — всем, один раз.
        var heard = sim.TakeSignals(DefeatedSignal.Name);
        Assert.Single(heard);
        Assert.Null(heard[0].To);
        Assert.Equal(1u, MemoryPackSerializer.Deserialize<DefeatedSignal>(heard[0].Payload)!.Player);
        sim.Seconds(1);
        Assert.Empty(sim.TakeSignals(DefeatedSignal.Name));
    }

    [Fact(DisplayName = "игрока, которого в мире нет, проигравшим не считают; с главным зданием — не проиграл")]
    public void UnknownIsNotDefeated()
    {
        using var sim = Start();
        Assert.False(sim.IsDefeated(3));
        Assert.False(sim.IsDefeated(1));
    }

    [Fact(DisplayName = "без главного здания, но с другим зданием — не проиграл; без зданий и MCV — проиграл")]
    public void AnyBuildingKeepsTheGame()
    {
        using var sim = Start();
        var core = Scene.Core(sim);
        var (x, y) = sim.At(core);
        sim.Erase(core);
        var generator = sim.PutBuilding("generator", (int)x, (int)y, 1);
        Assert.False(sim.IsDefeated(1));
        sim.Erase(generator);
        Assert.True(sim.IsDefeated(1));
    }

    [Fact(DisplayName = "игрок уничтожает своих юнитов: они гибнут на месте, никого не задев; кредиты не возвращаются, лимит свободен")]
    public void ScuttleOwnUnits()
    {
        using var sim = Start();
        var (cx, cy) = sim.At(Scene.Core(sim));
        var at = Free(sim, (int)cx + 5, (int)cy + 1);
        var tank = Scene.Spawn(sim, "tank", 1, at.X, at.Y);
        var near = Free(sim, at.X + 1, at.Y);
        var neighbour = Scene.Spawn(sim, "tank", 1, near.X, near.Y);
        // Чужой — далеко: рядом его расстреляли бы, а проверяется другое.
        sim.SetCredits(2, 0);
        var far = Free(sim, (int)cx + 60, (int)cy + 2);
        var foe = Scene.Spawn(sim, "truck", 2, far.X, far.Y);
        sim.Advance();
        var credits = sim.Credits(1);
        var used = Scene.Army(sim, 1);
        // Чужого уничтожить нельзя, своего — можно.
        sim.Send(1, new ScuttleOrder { Units = [tank, foe] });
        sim.Seconds(0.2);
        Assert.False(sim.Has(tank, Names.Unit.Name));
        Assert.True(sim.Has(foe, Names.Unit.Name));
        Assert.Equal(1, sim.HealthOf(neighbour));
        Assert.Equal(1, sim.HealthOf(foe));
        Assert.True(Scene.Army(sim, 1) < used);
        Assert.True(sim.Credits(1) <= credits + 1);
        Assert.False(sim.IsDefeated(1));
    }

    [Fact(DisplayName = "проигравший начинает заново: остатки базы исчезают, союзы забыты, новый стартовый набор и защита; непобеждённому — нельзя")]
    public void RespawnAfterDefeat()
    {
        Scene.SkipUnlessAvailable();
        using var sim = Sim.Create(new SimOptions { Size = 128, Land = Scene.Land, Settings = new { start = true } });
        sim.Seconds(0.5);
        var kit = sim.Owned(1);
        Assert.True(kit > 0);
        Assert.True(sim.ProtectionLeft(1) > 0);
        sim.Ally(1, 2);
        // Не побеждён — просьба начать заново ничего не меняет.
        sim.Respawn(1);
        Assert.Equal(kit, sim.Owned(1));
        Assert.True(sim.Allied(1, 2));
        // Сдался — и начинает заново.
        sim.Send(1, new SurrenderOrder());
        sim.Seconds(8);
        Assert.True(sim.IsDefeated(1));
        Assert.Single(sim.TakeSignals(DefeatedSignal.Name));
        sim.Respawn(1);
        Assert.False(sim.IsDefeated(1));
        Assert.Equal(kit, sim.Owned(1));
        Assert.False(sim.Allied(1, 2));
        Assert.True(sim.ProtectionLeft(1) > 0);
        // Кредиты — стартовые (STARTING_CREDITS), остатки прежних сгорели.
        Assert.Equal(1500, sim.Credits(1));
    }
}
