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
/// раз в полсекунды. В TS сценки — часть sim-ts для клиента; здесь они на стороне теста поверх приказов и правок, кроме
/// «добычи»: её базу ставит сам мод в партии-песочнице (Players/Sandbox). Армии набираются тем же зерном, что в TS.
/// </summary>
public sealed class ShowcaseTests
{
    private const uint Player = 1;
    private const uint Training = Sim.TrainingPlayer;

    /// <summary>Math.random тестов TS с зерном 7 (Park–Miller).</summary>
    private sealed class Seeded
    {
        private long _seed = 7;

        public double Next() => (_seed = _seed * 16807 % 2147483647) / 2147483647.0;
    }

    /// <summary>Сценка: заводит мир (Create; false — на этой местности не встала) и ведёт его (Drive; false — кончилась).</summary>
    private abstract class Scene
    {
        public Seeded Random { get; } = new();

        /// <summary>Партия, в которой сценка встаёт (у «добычи» — песочница).</summary>
        public virtual string Game => "test";

        public abstract bool Create(Sim sim);

        public virtual bool? Drive(Sim sim, double seconds) => null;
    }

    // ===== Общее =====

    /// <summary>Тайлы вокруг (x, y) расширяющимися квадратами до radius (around).</summary>
    private static IEnumerable<(int X, int Y)> Around(int x, int y, int radius)
    {
        for (var ring = 0; ring <= radius; ring++)
            for (var dy = -ring; dy <= ring; dy++)
                for (var dx = -ring; dx <= ring; dx++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == ring)
                        yield return (x + dx, y + dy);
    }

    private static bool CanPlace(Sim sim, string type, int x, int y, int gap = 0) =>
        sim.Query<bool>(KernelQueries.CanPlace, new PlaceArgs { Type = type, X = x, Y = y, Gap = gap });

    /// <summary>Ближайшее к началу мира место под здание с проходом gap (openSpot).</summary>
    private static (int X, int Y)? OpenSpot(Sim sim, string type, int gap, int radius = 40)
    {
        foreach (var tile in Around(0, 0, radius))
            if (Kernel.Scene.Rock(sim, tile.X, tile.Y, 1, 1) && CanPlace(sim, type, tile.X, tile.Y, gap))
                return tile;
        return null;
    }

    /// <summary>Свободные тайлы кольцами с шагом группы (freeTilesNear).</summary>
    private static List<(int X, int Y)> FreeTiles(Sim sim, int x, int y, int count, int from)
    {
        var tiles = new List<(int X, int Y)>();
        var max = Math.Min(40, Math.Max(8, (int)Math.Ceiling(Math.Sqrt(count))));
        for (var radius = from; radius <= max && tiles.Count < count; radius++)
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var (tx, ty) = (x + dx * 2, y + dy * 2);
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == radius && tiles.Count < count && sim.Walkable(tx, ty) && Kernel.Scene.Occupant(sim, tx, ty) == 0)
                        tiles.Add((tx, ty));
                }
        return tiles;
    }

    /// <summary>Отряд игрока вокруг (x, y); в сценках все агрессивны (spawnGroup).</summary>
    private static List<uint> SpawnGroup(Sim sim, IReadOnlyList<string> types, uint player, double x, double y, int from = 0)
    {
        sim.EnsurePlayer(player);
        var tiles = FreeTiles(sim, (int)Math.Floor(x), (int)Math.Floor(y), types.Count, from);
        var units = new List<uint>();
        for (var i = 0; i < types.Count && i < tiles.Count; i++)
            units.Add(sim.PutUnit(types[i], player, tiles[i].X + 0.5, tiles[i].Y + 0.5));
        sim.SetStance("aggressive", [.. units]);
        return units;
    }

    /// <summary>Веса армии, где никто не берётся (NONE), с правками.</summary>
    private static Dictionary<string, double> Weights(params (string Type, double Weight)[] weights)
    {
        var all = Tables.Units.Keys.ToDictionary(type => type, _ => 0.0);
        foreach (var (type, weight) in weights)
            all[type] = weight;
        return all;
    }

    private static int Count(Sim sim, string component, uint player) => sim.With(component, Names.Owner.Name).Count(entity => sim.OwnerOf(entity) == player);

    // ===== Сценки =====

    /// <summary>Добыча: тестовая карта партии-песочницы — её ставит мод при входе игрока.</summary>
    private sealed class Mining : Scene
    {
        public override string Game => "sandbox";

        public override bool Create(Sim sim)
        {
            sim.Join(Player);
            return sim.With(Names.Building.Name, Names.Owner.Name).Any(entity => sim.OwnerOf(entity) == Player && Kernel.Scene.TypeOf(sim, entity) == "mine");
        }
    }

    /// <summary>Что строит бригада, по дереву технологий (PLAN).</summary>
    public static readonly string[] Plan =
        ["generator", "radar", "generator", "barracks", "smelter", "metalYard", "matter", "turret", "rocketTurret", "siliconWorks", "siliconStore", "cannonTurret"];

    /// <summary>Стройка: главное здание и бригада строителей, которая поднимает вокруг него базу здание за зданием.</summary>
    private sealed class Construction : Scene
    {
        private int _step;
        private double _done;
        private (int X, int Y) _core;

        public override bool Create(Sim sim)
        {
            if (OpenSpot(sim, "command", 6) is not { } spot)
                return false;
            sim.PutBuilding("command", spot.X, spot.Y, Player);
            _core = (spot.X + 1, spot.Y + 1);
            sim.SetCredits(Player, 50000);
            // Исследования сценка не показывает: всё открыто сразу (grantResearch).
            sim.Set(sim.Wallet(Player), "Science.done", Kernel.Scene.Ids([.. Tables.All.Research.Keys.Select(topic => Kernel.Scene.Proto(sim, "research/" + topic))]));
            SpawnGroup(sim, ["builder", "builder", "builder", "builder", "builder", "truck", "truck", "infantry", "infantry"], Player, _core.X, _core.Y, 3);
            return true;
        }

        public override bool? Drive(Sim sim, double seconds)
        {
            // Новая площадка — только когда прежние достроены.
            if (Count(sim, Names.Site.Name, Player) > 0)
                return true;
            if (_step >= Plan.Length)
                return (_done += seconds) < 12;
            var builders = sim.With(Names.Unit.Name, Names.Owner.Name).Where(entity => sim.OwnerOf(entity) == Player && Kernel.Scene.TypeOf(sim, entity) == "builder").ToArray();
            var type = Plan[_step++];
            foreach (var (x, y) in Around(_core.X, _core.Y, 12))
            {
                if (Math.Abs(x - _core.X) < 4 && Math.Abs(y - _core.Y) < 4)
                    continue;
                if (!CanPlace(sim, type, x, y, 1))
                    continue;
                if (sim.Now(Player, new BuildOrder { Building = type, X = x, Y = y, Builders = builders }))
                    break;
            }
            return true;
        }
    }

    private static readonly (int X, int Y)[] Sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>Оборона: главное здание, перед ним случайное укрепление, защитники и волны учебного противника.</summary>
    private sealed class Defense : Scene
    {
        private int _wave;
        private double _calm;
        private Func<int, int, (int X, int Y)> _at = (u, v) => (u, v);
        private int _reach;

        private static readonly Dictionary<string, double> WaveWeights =
            Weights(("infantry", 2), ("rocketeer", 1), ("buggy", 1), ("lancer", 1), ("tank", 1), ("tesla", 0.5));

        private static readonly Dictionary<string, double> GuardWeights =
            Weights(("infantry", 2), ("rocketeer", 2), ("buggy", 1), ("lancer", 1), ("tank", 1), ("tesla", 0.5));

        public override bool Create(Sim sim)
        {
            if (OpenSpot(sim, "command", 10) is not { } spot)
                return false;
            sim.EnsurePlayer(Player);
            sim.EnsurePlayer(Training);
            var side = Sides[(int)Math.Floor(Random.Next() * Sides.Length)];
            var core = (X: spot.X + 1, Y: spot.Y + 1);
            _at = (u, v) => (core.X + side.X * u - side.Y * v, core.Y + side.Y * u + side.X * v);
            sim.PutBuilding("command", spot.X, spot.Y, Player);
            var pieces = Fortify.Fortification(Random.Next);
            _reach = pieces.Max(piece => piece.U);
            foreach (var piece in pieces)
            {
                var (x, y) = _at(piece.U, piece.V);
                if (CanPlace(sim, piece.Type, x, y))
                    sim.PutBuilding(piece.Type, x, y, Player);
            }
            // Турели получают патроны по трубам: укрепление сразу собрано в одну сеть.
            sim.ConnectAll(Player);
            var guard = _at(3, 0);
            SpawnGroup(sim, [.. Sim.RandomArmy(1500, GuardWeights, Random.Next), "builder", "builder"], Player, guard.X, guard.Y, 1);
            return true;
        }

        public override bool? Drive(Sim sim, double seconds)
        {
            var turrets = 0;
            var targets = new List<(uint Entity, double X, double Y)>();
            foreach (var entity in sim.With(Names.Building.Name, Names.Position.Name, Names.Owner.Name))
            {
                if (sim.OwnerOf(entity) != Player || Kernel.Scene.TypeOf(sim, entity) is not { } type)
                    continue;
                var spec = Tables.Buildings[type];
                if (spec.Ammo)
                {
                    turrets++;
                    // Патроны подвозят «за кадром»: сценка про бой, а не про снабжение.
                    if (sim.Has(entity, Names.Stock.Name))
                        sim.SetStock(entity, "ammo", Math.Min(sim.Stock(entity, "ammo") + 20, sim.Num(entity, "Stock.capacity")));
                }
                var (x, y) = sim.At(entity);
                targets.Add((entity, x + spec.Width / 2, y + spec.Height / 2));
            }
            foreach (var entity in sim.With(Names.Unit.Name, Names.Position.Name, Names.Owner.Name))
                if (sim.OwnerOf(entity) == Player)
                    targets.Add((entity, sim.At(entity).X, sim.At(entity).Y));
            var foes = 0;
            foreach (var entity in sim.With(Names.Unit.Name, Names.Position.Name, Names.Owner.Name))
            {
                if (sim.OwnerOf(entity) != Training)
                    continue;
                foes++;
                var target = sim.TargetOf(entity);
                if (sim.Has(entity, Names.Path.Name) || (target != 0 && sim.Alive(target) && sim.Has(target, Names.Position.Name)))
                    continue;
                var (ax, ay) = sim.At(entity);
                var nearest = targets.OrderBy(item => (item.X - ax) * (item.X - ax) + (item.Y - ay) * (item.Y - ay)).Select(item => item.Entity).FirstOrDefault();
                if (nearest != 0)
                    sim.Send(Training, new AttackOrder { Units = [entity], Target = nearest });
            }
            if (turrets == 0)
                return false;
            if (foes > 0)
                return true;
            _calm += seconds;
            if (_calm < 3)
                return true;
            _calm = 0;
            if (_wave >= 6)
                return false;
            var army = Sim.RandomArmy(1200 + _wave * 500, WaveWeights, Random.Next);
            var from = _at(_reach + 20, (int)Math.Floor((Random.Next() * 2 - 1) * 8 + 0.5));
            SpawnGroup(sim, army, Training, from.X, from.Y);
            _wave++;
            return true;
        }
    }

    /// <summary>Бой двух армий одной цены (battle).</summary>
    private sealed class Battle(double budget, Dictionary<string, double>? weights) : Scene
    {
        public override bool Create(Sim sim)
        {
            sim.SpawnBattle(Player, 0, 0, Sim.RandomArmy(budget, weights, Random.Next), Sim.RandomArmy(budget, weights, Random.Next));
            return true;
        }

        public override bool? Drive(Sim sim, double seconds) => sim.DriveBattle(Player);
    }

    private static Scene Make(string name) => name switch
    {
        "mining" => new Mining(),
        "construction" => new Construction(),
        "defense" => new Defense(),
        "battle" => new Battle(3500, null),
        "armor" => new Battle(4000, Weights(("tank", 2), ("lancer", 1), ("tesla", 1), ("carrier", 1), ("buggy", 1))),
        "infantry" => new Battle(1500, Weights(("infantry", 2), ("rocketeer", 1))),
        "air" => new Battle(3000, Weights(("drone", 2), ("gunship", 1), ("rocketeer", 1))),
        _ => throw new ArgumentException(name),
    };

    /// <summary>Местность default с другим зерном.</summary>
    private static JsonObject Land(int seed)
    {
        var lands = JsonNode.Parse(Sim.Content().Data("lands")!.Value.GetRawText())!.AsObject();
        var land = lands[Kernel.Scene.Land]!.DeepClone().AsObject();
        land["seed"] = seed;
        return land;
    }

    /// <summary>Мир сценки на крошечной карте (128² TS) без тумана; ищет зерно, на котором сценка встаёт (stage).</summary>
    private static (Sim Sim, Scene Scene) Stage(string name)
    {
        Kernel.Scene.SkipUnlessAvailable();
        for (var seed = 1; seed < 20; seed++)
        {
            var scene = Make(name);
            var sim = Sim.Create(new SimOptions { Game = scene.Game, Size = 64, Fog = false, Land = Land(seed), Players = [] });
            if (scene.Create(sim))
            {
                // Стойки и приказы сценки — сразу, как прямые вызовы в TS.
                sim.Flush();
                return (sim, scene);
            }
            sim.Dispose();
        }
        throw new InvalidOperationException($"Сценка {name} не встала ни на одном зерне");
    }

    /// <summary>Ведёт мир seconds секунд, как фон меню: сценка — раз в полсекунды. Возвращает, кончилась ли она (play).</summary>
    private static bool Play((Sim Sim, Scene Scene) staged, double seconds)
    {
        var steps = (int)Math.Ceiling(seconds / Sim.Dt - 1e-9);
        for (var i = 0; i < steps; i++)
        {
            staged.Sim.Advance();
            if (i % 10 == 9 && staged.Scene.Drive(staged.Sim, 0.5) == false)
                return true;
        }
        return false;
    }

    [Fact(DisplayName = "стройка: бригада поднимает здания одно за другим")]
    public void ConstructionBuildsOneByOne()
    {
        var staged = Stage("construction");
        using var _sim = staged.Sim;
        var before = Count(staged.Sim, Names.Building.Name, Player);
        Play(staged, 90);
        Assert.True(Count(staged.Sim, Names.Building.Name, Player) > before + 2);
    }

    [Fact(DisplayName = "оборона: волны идут на стену, а турели их встречают")]
    public void DefenseMeetsWaves()
    {
        var staged = Stage("defense");
        using var _sim = staged.Sim;
        staged.Sim.Advance();
        var walls = Count(staged.Sim, Names.Building.Name, Player);
        var foes = 0;
        for (var i = 0; i < 60 && foes == 0; i++)
        {
            Play(staged, 1);
            foes = Count(staged.Sim, Names.Unit.Name, Training);
        }
        Assert.True(foes > 0);
        Play(staged, 60);
        // Бой был: кто-то из волны погиб или стена пострадала.
        Assert.True(Count(staged.Sim, Names.Unit.Name, Training) < foes || Count(staged.Sim, Names.Building.Name, Player) < walls);
    }

    [Fact(DisplayName = "добыча и бои встают на крошечной карте; бой кончается")]
    public void ScenesStageAndBattleEnds()
    {
        foreach (var name in new[] { "mining", "battle", "armor", "infantry", "air" })
        {
            var staged = Stage(name);
            using var _sim = staged.Sim;
            Assert.True(Count(staged.Sim, Names.Unit.Name, Player) + 1 > 1);
        }
        var infantry = Stage("infantry");
        using var sim = infantry.Sim;
        Assert.True(Play(infantry, 120));
    }

    [Fact(DisplayName = "в сценках боя все бойцы агрессивны: под огнём не стоят, а идут на врага")]
    public void BattleScenesAreAggressive()
    {
        var (sim, _) = Stage("battle");
        using var _sim = sim;
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
        var staged = Stage("construction");
        using var _sim = staged.Sim;
        var before = Count(staged.Sim, Names.Building.Name, Player);
        Play(staged, 400);
        Assert.True(Count(staged.Sim, Names.Building.Name, Player) - before >= Plan.Length);
    }
}
