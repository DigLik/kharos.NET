using Base.Shared;
using Kharos.Base.Tests.Kernel;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Pipes;

/// <summary>Тестовая карта (packages/tests/sandbox.test.ts).</summary>
public sealed class SandboxTests
{
    [Fact(DisplayName = "тестовая карта: готовая база с энергией, добытое по трубам идёт в хранилища")]
    public void SandboxBaseWorks()
    {
        using var sim = Scene.Create(512);
        Assert.NotNull(Sandbox.Spawn(sim, 1));
        var types = sim.Of(1, Names.Building.Name).Select(entity => PipesScene.TypeOf(sim, entity)).ToList();
        foreach (var type in new[] { "mine", "command", "metalYard", "spaceport", "factory", "barracks", "generator", "matter" })
            Assert.Contains(type, types);
        // Всё стоит в одной зоне, и энергии хватает всем.
        Assert.NotEmpty(sim.Zones(1));
        Assert.All(sim.PowerStates().Values, state => Assert.NotEqual("starved", state));

        var units = sim.Of(1, Names.Unit.Name);
        var trucks = units.Where(entity => sim.Has(entity, Names.Hauler.Name)).ToList();
        Assert.Equal(4, trucks.Count);
        // Шахта в сети базы: грузовик к ней не привязан, руду несут трубы.
        Assert.Equal(0, trucks.Count(entity => sim.Hauler(entity).Mine != 0));
        Assert.True(units.Count > trucks.Count + 5);

        // Шахта работает: руда идёт через переработку, и металла в хранилищах становится больше.
        var before = Sandbox.StockOf(sim, 1, "metal");
        sim.Seconds(120);
        Assert.True(Sandbox.StockOf(sim, 1, "metal") > before + Tables.All.Deposits["metal"].Rate * 60, $"{before} → {Sandbox.StockOf(sim, 1, "metal")}");
    }
}
