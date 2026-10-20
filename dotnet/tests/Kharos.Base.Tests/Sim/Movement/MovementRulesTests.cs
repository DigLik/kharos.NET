using Base.Shared;
using Base.Shared.Orders;
using Kharos.Base.Tests.Kernel;
using Kharos.Engine.Terrain;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Movement;

/// <summary>
/// Правила хода и обзора на стороне мода base: то, что в TS жило в units.test.ts, terrainSpeed.test.ts и vision.test.ts
/// и в .NET делится между движком (Engine.Tests, Host.Tests) и модом.
/// </summary>
public sealed class MovementRulesTests
{
    [Fact(DisplayName = "чужими юнитами командовать нельзя")]
    public void ForeignUnitsCannotBeCommanded()
    {
        using var sim = Scene.Create(256, new { techTree = false });
        // Стартовый набор игрока 1 (spawnStartingUnits): несколько юнитов на проходимых тайлах рядом.
        var navigation = sim.Host.Tools.Navigation!;
        string[] types = ["mcv", "buggy", "buggy", "infantry", "infantry", "infantry"];
        var units = new List<uint>();
        for (var radius = 0; radius < 80 && units.Count < types.Length; radius++)
            for (var y = -radius; y <= radius && units.Count < types.Length; y++)
                for (var x = -radius; x <= radius && units.Count < types.Length; x++)
                    if (Math.Max(Math.Abs(x), Math.Abs(y)) == radius && navigation.IsWalkable(x, y)
                        && sim.Land!.TerrainAt(x, y) == TerrainType.Rock && !sim.Land.IsCliffFoot(x + 0.5, y + 0.5))
                        units.Add(Scene.Spawn(sim, types[units.Count], 1, x, y));
        Assert.Equal(types.Length, units.Count);
        var (tx, ty) = sim.At(units[0]);
        sim.Advance();
        // Чужой игрок, мусорные номера и приказ в точку, где юнит уже стоит, путей не создают.
        sim.Send(2, new MoveOrder { Units = [.. units], X = (int)tx + 5, Y = (int)ty });
        sim.Send(1, new MoveOrder { Units = [999999, 0xFFFFFFFF, 12345], X = (int)tx + 5, Y = (int)ty });
        sim.Advance();
        Assert.Empty(sim.With(Names.Path.Name));
        // Свои юниты тем же приказом идут: проверка отказывает именно по хозяину и номерам, а не вообще.
        sim.Send(1, new MoveOrder { Units = [units[0]], X = (int)tx + 5, Y = (int)ty });
        sim.Advance();
        Assert.NotEmpty(sim.With(Names.Path.Name));
    }

    [Fact(DisplayName = "обзор: разведчик видит дальше пехоты, стрелок — дальше своего оружия, радар — дальше всех зданий")]
    public void SightNumbersComeFromData()
    {
        using var sim = Scene.Create(256, new { techTree = false });
        double Unit(string type) => sim.Num(Scene.Spawn(sim, type, 1, 0, 0), "Sight.radius");
        Assert.True(Unit("buggy") > Unit("infantry"));
        Assert.True(Unit("lancer") > 7.5);
        // Здания встают там, где им есть место (canPlace), по очереди.
        var taken = new List<(int X, int Y)>();
        double Building(string type)
        {
            for (var y = -100; y < 100; y++)
                for (var x = -100; x < 100; x++)
                    if (Scene.CanPlace(sim, type, x, y) && taken.All(at => Math.Abs(at.X - x) > 12 || Math.Abs(at.Y - y) > 12))
                    {
                        taken.Add((x, y));
                        return sim.Num(sim.PutBuilding(type, x, y, 1), "Sight.radius");
                    }
            throw new InvalidOperationException("не нашлось места");
        }
        var command = Building("command");
        Assert.True(Building("radar") > command);
        // Стройка видит меньше готового здания: полуразмер + 2 против полуразмера + 4 (SITE_SIGHT, BUILDING_SIGHT).
        var spec = Tables.Buildings["command"];
        Assert.True(Math.Max(spec.Width, spec.Height) / 2 + 2 < command);
    }

    [Fact(DisplayName = "на солончаках болото промёрзло и вязнут меньше, в красных пустошах оно жжёт наземных")]
    public void ToxicSwampBurnsGroundUnits()
    {
        using var sim = Scene.Create(512, new { techTree = false });
        var land = sim.Land!;
        (int X, int Y)? toxic = null;
        for (var y = -500; y < 500 && toxic is null; y++)
            for (var x = -500; x < 500 && toxic is null; x++)
                if (land.TerrainAt(x, y) == TerrainType.Swamp && land.BiomeAt(x + 0.5, y + 0.5) == Biome.RedWastes && !land.IsCliffFoot(x + 0.5, y + 0.5))
                    toxic = (x, y);
        var tile = toxic ?? throw new InvalidOperationException("не нашлось местности");
        var soldier = sim.PutUnit("infantry", 1, tile.X + 0.5, tile.Y + 0.5);
        var drone = sim.PutUnit("drone", 1, tile.X + 0.5, tile.Y + 0.5);
        sim.Seconds(10);
        // Пехота сама залечивается, но медленнее, чем жжёт болото.
        var health = sim.HealthOf(soldier);
        Assert.InRange(health, 0.85, 0.98);
        Assert.True(health < 0.98);
        Assert.Equal(1, sim.HealthOf(drone));
    }
}
