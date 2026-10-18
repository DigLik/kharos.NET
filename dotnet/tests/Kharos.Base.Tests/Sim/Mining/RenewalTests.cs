using Base.Shared;
using Kharos.Engine.Terrain;
using Kharos.Base.Tests.Kernel;
using Tables = Kharos.Base.Tests.Kernel.Data;
using static Kharos.Base.Tests.Mining.MiningScene;

namespace Kharos.Base.Tests.Mining;

/// <summary>
/// Смена месторождений (packages/tests/renewal.test.ts): выработанное уходит, через depositRenewal минут появляется новое.
/// Туман (находки и призраки) — память запоминаемого слоя в движке, ждёт его (Skip). Мир 256×256 — Size 128.
/// </summary>
public sealed class RenewalTests
{
    [Fact(DisplayName = "выработанное месторождение уходит, а через depositRenewal минут в другой клетке появляется новое")]
    public void SpentDepositLeavesAndANewOneAppears()
    {
        using var sim = Small();
        var before = sim.Deposits();
        var spot = Central(sim);
        sim.Exhaust(spot);
        sim.Seconds(1);
        Assert.Null(sim.DepositAt(spot.X, spot.Y));
        Assert.Equal(0, sim.Count(Names.Deposit.Name));
        Assert.Equal(before.Count - 1, sim.Deposits().Count);

        sim.Seconds(28);
        Assert.Equal(before.Count - 1, sim.Deposits().Count);
        sim.Seconds(2);
        Assert.Equal(before.Count, sim.Deposits().Count);
        var fresh = Assert.Single(sim.Deposits().Except(before));
        foreach (var (x, y) in Enumerable.Range(0, 4).Select(i => (fresh.X + i % 2, fresh.Y + i / 2)))
            Assert.Equal(TerrainType.Rock, sim.Land!.TerrainAt(x, y));
        Assert.Empty(sim.Renewal().Due);
    }

    [Fact(DisplayName = "новое месторождение не появляется у зданий")]
    public void NewDepositNeverAppearsNearBuildings()
    {
        using var sim = Small(renewal: 0.05);
        // Здания по всей карте с шагом 12 тайлов, до самого края: свободного от них на 12 тайлов места нет нигде.
        for (var y = -128; y < 128; y += 12)
            for (var x = -128; x < 128; x += 12)
                Block(sim, "metalYard", x, y, 3, 2);
        var count = sim.Deposits().Count;
        sim.Exhaust(Central(sim));
        sim.Seconds(10);
        Assert.Equal(count - 1, sim.Deposits().Count);
        Assert.Single(sim.Renewal().Due);
    }

    [Fact(DisplayName = "под шахтой выработанное месторождение остаётся, пока шахта стоит; смена отсчитывается от выработки")]
    public void SpentDepositStaysWhileMineStands()
    {
        using var sim = Small();
        var spot = Central(sim);
        var mine = MineAt(sim, spot);
        sim.Exhaust(spot);
        sim.Seconds(1);
        Assert.Equal(spot, sim.DepositAt(spot.X, spot.Y));
        Assert.Single(sim.Renewal().Due);
        // Новое появилось, хотя шахта стоит: месторождений на карте не меньше, чем было.
        sim.Seconds(30);
        Assert.Empty(sim.Renewal().Due);
        sim.Erase(mine);
        sim.Seconds(1);
        Assert.Null(sim.DepositAt(spot.X, spot.Y));
        Assert.Empty(sim.Renewal().Due);
    }

    [Fact(DisplayName = "без правила месторождения конечны")]
    public void WithoutTheRuleDepositsAreFinite()
    {
        using var sim = Small(renewal: 0);
        var spot = Central(sim);
        sim.Exhaust(spot);
        sim.Seconds(5);
        Assert.Equal(spot, sim.DepositAt(spot.X, spot.Y));
        Assert.Empty(sim.Renewal().Due);
    }

    [Fact(DisplayName = "туман: ушедшее помнят, пока не увидят его место; новое знает только тот, кто его видел", Skip = "ждёт памяти предметных слоёв игрока в движке (призраки и находки месторождений, remember у слоя deposits, Д4)")]
    public void FogGhostsAndFoundDeposits()
    {
    }

    [Fact(DisplayName = "новое месторождение там же, где у TS: эталон сида 40507 и счёта попыток")]
    public void NewDepositMatchesTs()
    {
        using var sim = Small();
        var before = sim.Deposits();
        var spot = Central(sim);
        // Эталон: тот же мир в sim-ts (createSim 256, DEFAULT_CONFIG), выработано central, через 31 секунду.
        Assert.Equal(new DepositSpot(59, 18, "silicon", 1212), spot);
        sim.Exhaust(spot);
        sim.Seconds(31);
        Assert.Equal(new DepositSpot(-34, -94, "kharite", 236), Assert.Single(sim.Deposits().Except(before)));
        Assert.Equal(1, sim.Renewal().Spawned);
    }

    [Fact(DisplayName = "смена месторождений в сохранении: срок и счёт попыток переживают загрузку, новое ставится после неё")]
    public void RenewalSurvivesSave()
    {
        using var sim = Small();
        var count = sim.Deposits().Count;
        sim.Exhaust(Central(sim));
        sim.Seconds(5);
        var due = Assert.Single(sim.Renewal().Due);
        using var again = sim.ThroughSave();
        Assert.Equal([due], again.Renewal().Due);
        Assert.Equal(count - 1, again.Deposits().Count);
        again.Seconds(27);
        Assert.Equal(count, again.Deposits().Count);
        Assert.Empty(again.Renewal().Due);
        Assert.Equal(1, again.Renewal().Spawned);
    }
}
