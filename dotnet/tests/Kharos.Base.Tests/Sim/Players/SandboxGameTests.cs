using Base.Shared;
using Kharos.Base.Tests.Kernel;

namespace Kharos.Base.Tests.Players;

/// <summary>
/// Партия «sandbox» из games.json: вошедшему игроку мод сам ставит тестовую карту (spawnSandbox в sandbox.ts) вместо
/// стартового набора. Как она устроена, проверяют тесты sandbox.test.ts (Pipes/SandboxTests, там база ставится правками).
/// </summary>
public sealed class SandboxGameTests
{
    [Fact(DisplayName = "партия-песочница ставит вошедшему готовую базу у месторождения металла: здания, запас, юниты и кредиты, всё — одной сетью")]
    public void SandboxGameSpawnsTheBase()
    {
        Scene.SkipUnlessAvailable();
        using var sim = Sim.Create(new SimOptions { Game = "sandbox", Size = 128, Land = Scene.Land, Players = [1] });
        var buildings = sim.Of(1, Names.Building.Name).Select(entity => Scene.TypeOf(sim, entity)).ToList();
        Assert.Contains("mine", buildings);
        Assert.Contains("command", buildings);
        Assert.Contains("spaceport", buildings);
        Assert.Equal(10000, sim.Credits(1));
        // Стартового набора нет: MCV не выдают, защиты новичка тоже.
        Assert.Empty(Scene.UnitsOf(sim, "mcv"));
        Assert.Equal(0, sim.ProtectionLeft(1));
        Assert.Equal(13, sim.Of(1, Names.Unit.Name).Count);
        Assert.Equal(120, sim.Of(1, Names.Building.Name).Sum(entity => sim.Stock(entity, "metal")));
        // Трубы соединяют базу в одну сеть за несколько тиков; шахта — в той же сети, что и главное здание.
        sim.Seconds(3);
        Assert.Single(sim.Zones(1));
        Assert.False(sim.IsDefeated(1));
    }
}
