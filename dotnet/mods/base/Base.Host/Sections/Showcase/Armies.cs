using Base.Host.Kernel;
using Base.Host.Sections.Combat;
using Base.Host.Sections.Tactics;
using Base.Shared.Data;
using Base.Shared.Orders;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Showcase;

/// <summary>
/// Армии показательных боёв (battle.ts): случайный состав на бюджет, строй двух сторон и ведение боя. Перенос с тестовой
/// стороны (Sim.Combat) на Call мода: числа берёт <see cref="Call.Random"/>, а не Math.random.
/// </summary>
internal static class Armies
{
    /// <summary>Сколько кредитов стоит армия одной стороны (ARMY_BUDGET) и на сколько тайлов от встречи стоит передний ряд (BATTLE_GAP).</summary>
    public const double ArmyBudget = 3500;
    public const int BattleGap = 6;

    private const int FormationSpacing = 2, FormationDepth = 200;

    /// <summary>Вооружён ли вид юнита — сам или турелями (isFighter).</summary>
    public static bool IsFighter(string type)
    {
        var spec = Core.Tables.Units[type];
        return spec.Weapon is not null || spec.Mounts.Any(mount => Core.Tables.Turrets.GetValueOrDefault(mount.Turret)?.Weapon is not null);
    }

    /// <summary>Веса, где никто не берётся (NONE), с правками.</summary>
    public static Dictionary<string, double> Weights(params (string Type, double Weight)[] weights)
    {
        var all = Core.Tables.Units.Keys.ToDictionary(type => type, _ => 0.0);
        foreach (var (type, weight) in weights)
            all[type] = weight;
        return all;
    }

    /// <summary>Случайная армия из боевых юнитов на budget кредитов, без машин снабжения (randomArmy).</summary>
    public static List<string> RandomArmy(double budget, IReadOnlyDictionary<string, double>? weights, Func<double> random)
    {
        weights ??= new Dictionary<string, double>();
        var units = Core.Tables.Units;
        double WeightOf(IReadOnlyDictionary<string, double> from, string type) => Math.Max(0, from.TryGetValue(type, out var weight) ? weight : 1);
        var armed = units.Keys.Where(type => IsFighter(type) && units[type].Supplies is null).ToList();
        if (armed.Any(type => WeightOf(weights, type) > 0))
            armed = [.. armed.Where(type => WeightOf(weights, type) > 0)];
        else
            weights = new Dictionary<string, double>();
        var army = new List<string>();
        for (; ; )
        {
            var affordable = armed.Where(type => units[type].Cost <= budget).ToList();
            if (affordable.Count == 0)
                return army;
            var pick = random() * affordable.Sum(type => WeightOf(weights, type));
            var chosen = affordable[^1];
            foreach (var candidate in affordable)
            {
                pick -= WeightOf(weights, candidate);
                if (pick < 0)
                {
                    chosen = candidate;
                    break;
                }
            }
            army.Add(chosen);
            budget -= units[chosen].Cost;
        }
    }

    private static bool CanStand(bool air, int x, int y) => air ? Placement.InBounds(x, y) : Tools.Walkable(x, y);

    /// <summary>Места строя из count юнитов: колонны от (x, y) в сторону side, ближняя к противнику — первой (formation).</summary>
    private static List<(int X, int Y)> Formation(int x, int y, int side, int count, bool air)
    {
        var rows = Math.Max(3, (int)Math.Ceiling(Math.Sqrt(count * 2)));
        var tiles = new List<(int, int)>();
        for (var column = 0; column < FormationDepth && tiles.Count < count; column++)
            for (var row = 0; row < rows && tiles.Count < count; row++)
            {
                var offset = row % 2 == 1 ? (row + 1) / 2 : -row / 2;
                var (tileX, tileY) = (x + side * column * FormationSpacing, y + offset * FormationSpacing);
                if (CanStand(air, tileX, tileY))
                    tiles.Add((tileX, tileY));
            }
        return tiles;
    }

    /// <summary>Показательный бой (spawnBattle): армия own игрока player слева от (x, y), foe учебного противника справа.</summary>
    public static void SpawnBattle(Call call, uint player, int x, int y, IReadOnlyList<string> own, IReadOnlyList<string> foe)
    {
        Accounts.Ensure(call, player);
        Accounts.Ensure(call, Battle.TrainingPlayer);
        foreach (var (owner, side, army) in new[] { (player, -1, own), (Battle.TrainingPlayer, 1, foe) })
            foreach (var air in new[] { false, true })
            {
                var types = army.Where(type => (Core.Tables.Units[type].Kind == "air") == air).ToList();
                var tiles = Formation(x + side * BattleGap, y, side, types.Count, air);
                var units = new List<uint>();
                for (var i = 0; i < types.Count && i < tiles.Count; i++)
                    units.Add(Factory.SpawnUnit(call, types[i], owner, tiles[i].X + 0.5, tiles[i].Y + 0.5));
                Stances.SetStance(call, owner, units, Stances.Aggressive);
            }
    }

    private readonly record struct Fighter(uint Entity, uint Player, double X, double Y, bool Air, bool Ground, bool Sky);

    /// <summary>
    /// Ведёт показательный бой (driveBattle): каждому стоящему вооружённому без живой цели — ближайшего врага, которого он
    /// может достать. false — бой кончился.
    /// </summary>
    public static bool DriveBattle(Call call, uint player)
    {
        var units = new List<Fighter>();
        var all = Core.All(S.Unit, S.Position, S.Owner);
        foreach (var entity in all)
        {
            if (call.UnitType(entity) is not { } type || Factory.Unit(type) is not { } spec)
                continue;
            var weapons = new List<WeaponSpec>();
            if (spec.Weapon is not null && Core.Tables.Weapons.TryGetValue(spec.Weapon, out var own))
                weapons.Add(own);
            foreach (var turret in Factory.TurretsOf(call, entity))
                if (Core.ProtoName((int)call.Num(turret, S.Proto, S.ProtoId, -1), "turrets") is { } name
                    && Core.Tables.Turrets.GetValueOrDefault(name)?.Weapon is { } gun && Core.Tables.Weapons.TryGetValue(gun, out var theirs))
                    weapons.Add(theirs);
            var (x, y) = call.At(entity);
            units.Add(new Fighter(entity, call.OwnerOf(entity), x, y, spec.Kind == "air", weapons.Any(weapon => !weapon.OnlyAir), weapons.Any(weapon => weapon.Air)));
        }
        var idle = new List<Fighter>();
        foreach (var unit in units)
        {
            if (call.Has(unit.Entity, S.Path))
                continue;
            var targets = Factory.TurretsOf(call, unit.Entity).Prepend(unit.Entity).Where(gunner => call.Has(gunner, S.Armed))
                .Select(gunner => (uint)call.Num(gunner, S.Armed, Refs.R.ArmedTarget)).ToList();
            if (targets.Count > 0 && !targets.Any(target => target != 0 && call.Alive(target) && call.Has(target, S.Position)))
                idle.Add(unit);
        }
        foreach (var self in idle)
        {
            if (!self.Ground && !self.Sky)
                continue;
            uint nearest = 0;
            var best = double.PositiveInfinity;
            foreach (var other in units)
            {
                if (other.Player == self.Player || !(other.Air ? self.Sky : self.Ground))
                    continue;
                var distance = (other.X - self.X) * (other.X - self.X) + (other.Y - self.Y) * (other.Y - self.Y);
                if (distance < best)
                {
                    best = distance;
                    nearest = other.Entity;
                }
            }
            if (nearest != 0)
                Commands.Accept(call, self.Player, AttackOrder.Name, new AttackOrder { Units = [self.Entity], Target = nearest });
        }
        static bool Reaches(Fighter from, Fighter to) => from.Player != to.Player && (to.Air ? from.Sky : from.Ground);
        return units.Any(unit => unit.Player == player) && units.Any(unit => unit.Player != player) && units.Any(from => units.Any(to => Reaches(from, to)));
    }
}
