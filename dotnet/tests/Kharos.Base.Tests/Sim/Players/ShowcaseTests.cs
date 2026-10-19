using System.Text.Json.Nodes;
using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Base.Shared.Rules;
using Kharos.Base.Tests.Kernel;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Players;

/// <summary>
/// Сценки фона главного меню (packages/tests/showcase.test.ts, сами сценки — showcase.ts): каждая заводит мир и ведёт его
/// раз в полсекунды. Сценки живут в моде (Base.Host/Sections/Showcase), партия showcase из games.json; имя сценки — в
/// настройках партии (scene). Мод берёт случайные числа хоста, не зерно TS, поэтому проверяется суть, не число.
/// </summary>
public sealed class ShowcaseTests
{
    private const uint Player = 1;
    private const uint Training = Sim.TrainingPlayer;

    /// <summary>Что строит бригада, по дереву технологий (PLAN в showcase.ts; в моде — Sections/Showcase/Scenes.cs).</summary>
    public static readonly string[] Plan =
        ["generator", "radar", "generator", "barracks", "smelter", "metalYard", "matter", "turret", "rocketTurret", "siliconWorks", "siliconStore", "cannonTurret"];

    private static int Count(Sim sim, string component, uint player) => sim.With(component, Names.Owner.Name).Count(entity => sim.OwnerOf(entity) == player);

    /// <summary>Мир сценки: партия showcase (крошечная карта без тумана) с именем сценки в настройках; ищет зерно, на котором сценка встаёт.</summary>
    private static Sim Stage(string name)
    {
        Kernel.Scene.SkipUnlessAvailable();
        for (var seed = 1; seed < 20; seed++)
        {
            var sim = Sim.Create(new SimOptions { Game = "showcase", Land = new { seed }, Settings = new { scene = name }, Players = [Player] });
            // Сценка встаёт на двух тиках после входа игрока: здания, затем люди.
            sim.Advance(3);
            if (Count(sim, Names.Building.Name, Player) + Count(sim, Names.Unit.Name, Player) > 0)
                return sim;
            sim.Dispose();
        }
        throw new InvalidOperationException($"Сценка {name} не встала ни на одном зерне");
    }

    /// <summary>Ведёт мир seconds секунд, как фон меню: сценку мод ведёт сам раз в полсекунды.</summary>
    private static void Play(Sim sim, double seconds) => sim.Advance((int)Math.Ceiling(seconds / Sim.Dt - 1e-9));

    [Theory(DisplayName = "каждая сценка встаёт на любом зерне: после завода на карте есть её здания или юниты")]
    [InlineData("mining")]
    [InlineData("construction")]
    [InlineData("defense")]
    [InlineData("battle")]
    [InlineData("armor")]
    [InlineData("infantry")]
    [InlineData("air")]
    public void EverySceneStands(string name)
    {
        Kernel.Scene.SkipUnlessAvailable();
        for (var seed = 1; seed <= 8; seed++)
        {
            using var sim = Sim.Create(new SimOptions { Game = "showcase", Land = new { seed }, Settings = new { scene = name }, Players = [Player] });
            sim.Advance(3);
            var entities = Count(sim, Names.Building.Name, Player) + Count(sim, Names.Unit.Name, Player);
            Assert.True(entities > 0, $"{name}: на зерне {seed} сценка пуста");
        }
    }

    [Fact(DisplayName = "стройка: бригада поднимает здания одно за другим")]
    public void ConstructionBuildsOneByOne()
    {
        using var sim = Stage("construction");
        var before = Count(sim, Names.Building.Name, Player);
        Play(sim, 90);
        Assert.True(Count(sim, Names.Building.Name, Player) > before + 2);
    }

    [Fact(DisplayName = "оборона: волны идут на стену, а турели их встречают")]
    public void DefenseMeetsWaves()
    {
        using var sim = Stage("defense");
        var walls = Count(sim, Names.Building.Name, Player);
        var foes = 0;
        for (var i = 0; i < 60 && foes == 0; i++)
        {
            Play(sim, 1);
            foes = Count(sim, Names.Unit.Name, Training);
        }
        Assert.True(foes > 0);
        Play(sim, 60);
        // Бой был: кто-то из волны погиб или стена пострадала.
        Assert.True(Count(sim, Names.Unit.Name, Training) < foes || Count(sim, Names.Building.Name, Player) < walls);
    }

    [Fact(DisplayName = "добыча и бои встают на крошечной карте; бой кончается")]
    public void ScenesStageAndBattleEnds()
    {
        foreach (var name in new[] { "mining", "battle", "armor", "infantry", "air" })
        {
            using var staged = Stage(name);
            Assert.True(Count(staged, Names.Unit.Name, Player) + 1 > 1);
        }
        using var sim = Stage("infantry");
        // Бой кончается: у одной из сторон не остаётся бойцов.
        for (var i = 0; i < 120 && Count(sim, Names.Unit.Name, Player) > 0 && Count(sim, Names.Unit.Name, Training) > 0; i++)
            Play(sim, 1);
        Assert.True(Count(sim, Names.Unit.Name, Player) == 0 || Count(sim, Names.Unit.Name, Training) == 0);
    }

    [Fact(DisplayName = "в сценках боя все бойцы агрессивны: под огнём не стоят, а идут на врага")]
    public void BattleScenesAreAggressive()
    {
        using var sim = Stage("battle");
        var units = sim.With(Names.Unit.Name);
        Assert.NotEmpty(units);
        Assert.All(units, entity => Assert.True(sim.StanceOf(entity) == "aggressive", $"{Kernel.Scene.TypeOf(sim, entity)}: {sim.StanceOf(entity)}"));
    }

    [Fact(DisplayName = "стройка: план идёт по дереву технологий, и бригада ставит его целиком")]
    public void ConstructionFollowsTechTree()
    {
        var buildable = Tables.Buildings.Where(pair => pair.Value.Menu is not null).Select(pair => pair.Key).ToHashSet();
        for (var i = 0; i < Plan.Length; i++)
        {
            Assert.Contains(Plan[i], buildable);
            foreach (var need in Tables.Buildings[Plan[i]].Requires)
                Assert.True(need == "command" || Plan.Take(i).Contains(need), $"{Plan[i]} требует {need}");
        }
        using var sim = Stage("construction");
        var before = Count(sim, Names.Building.Name, Player);
        Play(sim, 400);
        Assert.True(Count(sim, Names.Building.Name, Player) - before >= Plan.Length);
    }
}
