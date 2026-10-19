using Base.Host.Kernel;
using Base.Host.Sections.Combat;
using Base.Host.Sections.Mining;
using Base.Host.Sections.Pipes;
using Base.Host.Sections.Research;
using Base.Host.Sections.Tactics;
using Base.Shared.Orders;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Showcase;

/// <summary>
/// Сценка фона меню (sim-ts/showcase.ts): заводит мир и потом ведёт его раз в полсекунды. Мир этого же вызова новых
/// зданий ещё не знает, поэтому завод идёт в два тика: <see cref="Create"/> ставит здания, <see cref="Settle"/> на
/// следующем тике — людей и всё, что ищет свободные места.
/// </summary>
internal abstract class Scene
{
    /// <summary>Заводит сценку; false — на этой местности не встала.</summary>
    public abstract bool Create(Call call, uint player);

    /// <summary>Второй тик завода.</summary>
    public virtual void Settle(Call call, uint player)
    {
    }

    /// <summary>Идёт ли ещё соединение зданий трубами (по одной сети за тик).</summary>
    public bool Connecting { get; set; }

    /// <summary>Ведёт мир seconds секунд; false — сценка кончилась.</summary>
    public virtual bool Drive(Call call, uint player, double seconds) => true;

    // ===== Общее =====

    /// <summary>Тайлы вокруг (x, y) расширяющимися квадратами до radius включительно (around).</summary>
    public static IEnumerable<(int X, int Y)> Around(int x, int y, int radius)
    {
        for (var ring = 0; ring <= radius; ring++)
            for (var dy = -ring; dy <= ring; dy++)
                for (var dx = -ring; dx <= ring; dx++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == ring)
                        yield return (x + dx, y + dy);
    }

    /// <summary>Скала без подножия обрыва целиком под прямоугольником.</summary>
    private static bool Rock(int x, int y, int w, int h)
    {
        for (var ty = y; ty < y + h; ty++)
            for (var tx = x; tx < x + w; tx++)
                if (!Tools.Terrain(tx + 0.5, ty + 0.5, out var info) || info.Terrain != 2 || info.Foot)
                    return false;
        return true;
    }

    /// <summary>
    /// Ближайшее к началу мира место, где встаёт здание с проходом gap вокруг (openSpot). В TS сценка без места в radius
    /// не встаёт, и меню сразу берёт другую; здесь поиск идёт дальше, до края карты, — так фон не бывает пустым.
    /// </summary>
    protected static (int X, int Y)? OpenSpot(Call call, string type, int gap, int radius = 40)
    {
        var spec = Factory.Building(type)!;
        var reach = Math.Max(radius, Core.Game.Size ?? radius);
        foreach (var tile in Around(0, 0, reach))
            if (Rock(tile.X, tile.Y, (int)spec.Width, (int)spec.Height) && Placement.CanPlace(call, type, tile.X, tile.Y, gap))
                return tile;
        return null;
    }

    /// <summary>Отряд player вокруг (x, y); в сценках все агрессивны (spawnGroup).</summary>
    protected static List<uint> SpawnGroup(Call call, IReadOnlyList<string> types, uint player, double x, double y, int from = 0)
    {
        Accounts.Ensure(call, player);
        var tiles = Tools.FreeTiles((int)Math.Floor(x), (int)Math.Floor(y), types.Count, from);
        var units = new List<uint>();
        for (var i = 0; i < types.Count && i < tiles.Length; i++)
            units.Add(Factory.SpawnUnit(call, types[i], player, tiles[i].X + 0.5, tiles[i].Y + 0.5));
        Stances.SetStance(call, player, units, Stances.Aggressive);
        return units;
    }

    protected static int Count(Call call, ComponentRef component, uint player) => Core.All(component, S.Owner).Count(entity => call.OwnerOf(entity) == player);

}

/// <summary>Добыча: работающая база песочницы — шахта, трубы, переработки и грузовики.</summary>
internal sealed class MiningScene : Scene
{
    public override bool Create(Call call, uint player) => Players.Sandbox.Spawn(call, player);
}

/// <summary>Стройка: главное здание и бригада строителей, которая поднимает вокруг него базу здание за зданием.</summary>
internal sealed class Construction : Scene
{
    /// <summary>Что строят по очереди, по дереву технологий (PLAN).</summary>
    public static readonly string[] Plan =
        ["generator", "radar", "generator", "barracks", "smelter", "metalYard", "matter", "turret", "rocketTurret", "siliconWorks", "siliconStore", "cannonTurret"];

    private int _step;
    private double _done;
    private (int X, int Y) _core;

    public override bool Create(Call call, uint player)
    {
        if (OpenSpot(call, "command", 6) is not { } spot)
            return false;
        Factory.PlaceBuilding(call, "command", spot.X, spot.Y, player);
        _core = (spot.X + 1, spot.Y + 1);
        Accounts.Add(call, player, 50000);
        // Исследования сценка не показывает: всё открыто сразу (grantResearch).
        Knowledge.SetDone(call, Accounts.Find(call, player), Core.Tables.Research.Keys);
        return true;
    }

    public override void Settle(Call call, uint player) =>
        SpawnGroup(call, ["builder", "builder", "builder", "builder", "builder", "truck", "truck", "infantry", "infantry"], player, _core.X, _core.Y, 3);

    public override bool Drive(Call call, uint player, double seconds)
    {
        // Новая площадка — только когда прежние достроены: бригада работает вместе.
        if (Count(call, S.Site, player) > 0)
            return true;
        if (_step >= Plan.Length)
            return (_done += seconds) < 12;
        var builders = Core.All(S.Unit, S.Owner).Where(entity => call.OwnerOf(entity) == player && call.UnitType(entity) == "builder").ToArray();
        var type = Plan[_step++];
        foreach (var (x, y) in Around(_core.X, _core.Y, 12))
        {
            if (Math.Abs(x - _core.X) < 4 && Math.Abs(y - _core.Y) < 4)
                continue;
            if (!Placement.CanPlace(call, type, x, y, 1))
                continue;
            if (Commands.Accept(call, player, BuildOrder.Name, new BuildOrder { Building = type, X = x, Y = y, Builders = builders }))
                break;
        }
        return true;
    }
}

/// <summary>Оборона: главное здание, перед ним случайное укрепление, защитники и волны учебного противника.</summary>
internal sealed class Defense : Scene
{
    private static readonly (int X, int Y)[] Sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    private static readonly Dictionary<string, double> WaveWeights =
        Armies.Weights(("infantry", 2), ("rocketeer", 1), ("buggy", 1), ("lancer", 1), ("tank", 1), ("tesla", 0.5));

    private static readonly Dictionary<string, double> GuardWeights =
        Armies.Weights(("infantry", 2), ("rocketeer", 2), ("buggy", 1), ("lancer", 1), ("tank", 1), ("tesla", 0.5));

    private int _wave, _reach;
    private double _calm;
    private Func<int, int, (int X, int Y)> _at = (u, v) => (u, v);

    public override bool Create(Call call, uint player)
    {
        if (OpenSpot(call, "command", 10) is not { } spot)
            return false;
        Accounts.Ensure(call, player);
        Accounts.Ensure(call, Battle.TrainingPlayer);
        var side = Sides[(int)Math.Floor(call.Random() * Sides.Length)];
        var core = (X: spot.X + 1, Y: spot.Y + 1);
        _at = (u, v) => (core.X + side.X * u - side.Y * v, core.Y + side.Y * u + side.X * v);
        Factory.PlaceBuilding(call, "command", spot.X, spot.Y, player);
        var pieces = Fortify.Fortification(call.Random, DetMath.Cos, DetMath.Sin);
        _reach = pieces.Max(piece => piece.U);
        foreach (var piece in pieces)
        {
            var (x, y) = _at(piece.U, piece.V);
            if (Placement.CanPlace(call, piece.Type, x, y))
                Factory.PlaceBuilding(call, piece.Type, x, y, player);
        }
        return true;
    }

    public override void Settle(Call call, uint player)
    {
        // Турели получают патроны по трубам: укрепление сразу собрано в одну сеть.
        Connecting = true;
        var guard = _at(3, 0);
        SpawnGroup(call, [.. Armies.RandomArmy(1500, GuardWeights, call.Random), "builder", "builder"], player, guard.X, guard.Y, 1);
    }

    public override bool Drive(Call call, uint player, double seconds)
    {
        var turrets = 0;
        var targets = new List<(uint Entity, double X, double Y)>();
        foreach (var entity in Core.All(S.Building, S.Position, S.Owner))
        {
            if (call.OwnerOf(entity) != player || call.BuildingType(entity) is not { } type || Factory.Building(type) is not { } spec)
                continue;
            if (spec.Ammo)
            {
                turrets++;
                // Патроны подвозят «за кадром»: сценка про бой, а не про снабжение.
                if (call.Has(entity, S.Stock))
                    Stocks.Put(call, entity, "ammo", 20);
            }
            var (x, y) = call.At(entity);
            targets.Add((entity, x + spec.Width / 2, y + spec.Height / 2));
        }
        foreach (var entity in Core.All(S.Unit, S.Position, S.Owner))
            if (call.OwnerOf(entity) == player)
                targets.Add((entity, call.At(entity).X, call.At(entity).Y));
        var foes = 0;
        foreach (var entity in Core.All(S.Unit, S.Position, S.Owner))
        {
            if (call.OwnerOf(entity) != Battle.TrainingPlayer)
                continue;
            foes++;
            var target = call.Has(entity, S.Armed) ? (uint)call.Num(entity, S.Armed, Refs.R.ArmedTarget) : 0;
            if (call.Has(entity, S.Path) || (target != 0 && call.Alive(target) && call.Has(target, S.Position)))
                continue;
            var (ax, ay) = call.At(entity);
            var nearest = targets.OrderBy(item => (item.X - ax) * (item.X - ax) + (item.Y - ay) * (item.Y - ay)).Select(item => item.Entity).FirstOrDefault();
            if (nearest != 0)
                Commands.Accept(call, Battle.TrainingPlayer, AttackOrder.Name, new AttackOrder { Units = [entity], Target = nearest });
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
        var army = Armies.RandomArmy(1200 + _wave * 500, WaveWeights, call.Random);
        var from = _at(_reach + 20, (int)Math.Floor((call.Random() * 2 - 1) * 8 + 0.5));
        SpawnGroup(call, army, Battle.TrainingPlayer, from.X, from.Y);
        _wave++;
        return true;
    }
}

/// <summary>Бой двух армий одной цены (battle).</summary>
internal sealed class Fight(double budget, Dictionary<string, double>? weights) : Scene
{
    public override bool Create(Call call, uint player)
    {
        Armies.SpawnBattle(call, player, 0, 0, Armies.RandomArmy(budget, weights, call.Random), Armies.RandomArmy(budget, weights, call.Random));
        return true;
    }

    public override bool Drive(Call call, uint player, double seconds) => Armies.DriveBattle(call, player);
}

/// <summary>Сценки по именам (SCENES).</summary>
internal static class Scenes
{
    public static readonly string[] Names = ["mining", "construction", "defense", "battle", "armor", "infantry", "air"];

    public static Scene? Make(string? name) => name switch
    {
        "mining" => new MiningScene(),
        "construction" => new Construction(),
        "defense" => new Defense(),
        "battle" => new Fight(3500, null),
        "armor" => new Fight(4000, Armies.Weights(("tank", 2), ("lancer", 1), ("tesla", 1), ("carrier", 1), ("buggy", 1))),
        "infantry" => new Fight(1500, Armies.Weights(("infantry", 2), ("rocketeer", 1))),
        "air" => new Fight(3000, Armies.Weights(("drone", 2), ("gunship", 1), ("rocketeer", 1))),
        _ => null,
    };
}
