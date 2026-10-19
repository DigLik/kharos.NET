using static Kharos.Base.Tests.Combat.Arena;

namespace Kharos.Base.Tests.Combat;

/// <summary>Следы боя (packages/tests/traces.test.ts): остов и гарь кладёт бой командой Item в слой traces движка.</summary>
public sealed class TracesTests
{
    [Fact(DisplayName = "погибшая техника оставляет остов и гарь")]
    public void DestroyedVehicleLeavesWreckAndScar()
    {
        using var sim = Create();
        var tank = Spawn(sim, "tank", 2, 0, 0);
        sim.Advance();
        for (var i = 0; i < 3; i++)
            Spawn(sim, "lancer", 1, 4, 0);
        sim.Seconds(30);
        Assert.False(sim.Alive(tank));
        Assert.NotEmpty(CombatTests.Traces(sim, 3));
        Assert.True(CombatTests.Traces(sim, 1).Count + CombatTests.Traces(sim, 2).Count > 0);
    }
}
