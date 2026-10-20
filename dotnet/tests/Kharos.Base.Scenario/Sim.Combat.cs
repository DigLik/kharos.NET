using Base.Shared;
using Base.Shared.Data;
using Base.Shared.Orders;
using Base.Shared.Queries;

namespace Kharos.Base.Scenario;

// Бой (раздел Combat, С5а): запросы combat.*, стойка прямо в Tactics, показательный бой (battle.ts) на стороне теста.
public sealed partial class Sim
{
    /// <summary>Учебный противник (TRAINING_PLAYER).</summary>
    public const uint TrainingPlayer = 9999;

    /// <summary>Может ли игрок приказать атаковать цель (canAttack).</summary>
    public bool CanAttack(uint player, uint target) => Query<bool>(CombatQueries.CanAttack, new TargetArgs { Player = player, Target = target });

    /// <summary>Может ли сущность воевать — сама или турелями (canFight).</summary>
    public bool CanFight(uint entity) => Query<bool>(CombatQueries.CanFight, new EntityArgs { Entity = entity });

    /// <summary>Что юнитам делать с точкой (resolveOrder); fog false — как редактор, без тумана.</summary>
    public ResolvedOrder[] Resolve(uint player, uint[] units, double x, double y, bool fog = true) =>
        Query<ResolvedList>(CombatQueries.Resolve, new ResolveArgs { Player = player, Units = units, X = x, Y = y, Fog = fog }).Items;

    /// <summary>
    /// Стойка юнитов приказом stance от их хозяев (setStance): применится в начале следующего тика, как приказ игрока.
    /// Безоружные и чужие приказом отсеиваются.
    /// </summary>
    public void SetStance(string stance, params uint[] units)
    {
        foreach (var group in units.GroupBy(OwnerOf))
            Send(group.Key, new StanceOrder { Units = [.. group], Stance = stance });
    }

    /// <summary>Заводит игроку счёт с нулём кредитов, если его нет.</summary>
    public void EnsurePlayer(uint player)
    {
        if (player != 0 && Wallet(player) == 0)
            SetCredits(player, 0);
    }

    /// <summary>Цель стрелка (Armed.target); нет — 0.</summary>
    public uint TargetOf(uint gunner) => Has(gunner, Names.Armed.Name) ? (uint)Num(gunner, "Armed.target") : 0;

    /// <summary>Турели носителя по возрастанию номеров (Carrier.turrets).</summary>
    public List<uint> TurretsOf(uint carrier) => [.. With(Names.Mount.Name).Where(entity => Num(entity, "Mount.parent") == carrier)];

    // ===== Показательный бой (battle.ts) =====

    private static readonly Lazy<BaseTables> BattleTables = new(() => BaseTables.Load(name =>
    {
        var path = Path.Combine(ModDir, "data", name + ".json");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }));

    /// <summary>Сколько кредитов стоит армия одной стороны показательного боя.</summary>
    public const double ArmyBudget = 3500;

    /// <summary>На сколько тайлов от точки встречи стоит передний ряд каждой стороны.</summary>
    public const int BattleGap = 6;

    private const int FormationSpacing = 2, FormationDepth = 200;

    /// <summary>Вооружён ли вид юнита — сам или турелями (isFighter).</summary>
    public static bool IsFighter(string type)
    {
        var spec = BattleTables.Value.Units[type];
        return spec.Weapon is not null || spec.Mounts.Any(mount => BattleTables.Value.Turrets.GetValueOrDefault(mount.Turret)?.Weapon is not null);
    }

    /// <summary>
    /// Случайная армия из боевых юнитов на budget кредитов, без машин снабжения (randomArmy): weights — вес типа (0 — не
    /// берётся, нет — 1; все нулевые — типы равноправны), random — источник чисел 0..1.
    /// </summary>
    public static List<string> RandomArmy(double budget = ArmyBudget, IReadOnlyDictionary<string, double>? weights = null, Func<double>? random = null)
    {
        weights ??= new Dictionary<string, double>();
        random ??= Random.Shared.NextDouble;
        var units = BattleTables.Value.Units;
        double WeightOf(string type) => Math.Max(0, weights!.TryGetValue(type, out var weight) ? weight : 1);
        var armed = units.Keys.Where(type => IsFighter(type) && units[type].Supplies is null).ToList();
        if (armed.Any(type => WeightOf(type) > 0))
            armed = [.. armed.Where(type => WeightOf(type) > 0)];
        else
            weights = new Dictionary<string, double>();
        var army = new List<string>();
        for (; ; )
        {
            var affordable = armed.Where(type => units[type].Cost <= budget).ToList();
            if (affordable.Count == 0)
                return army;
            var pick = random() * affordable.Sum(WeightOf);
            var type = affordable[^1];
            foreach (var candidate in affordable)
            {
                pick -= WeightOf(candidate);
                if (pick < 0)
                {
                    type = candidate;
                    break;
                }
            }
            army.Add(type);
            budget -= units[type].Cost;
        }
    }

    private bool CanStand(bool air, int x, int y) => air ? Host.Tools.Navigation!.InBounds(x, y) : Walkable(x, y);

    /// <summary>Места строя из count юнитов: колонны от (x, y) в сторону side, ближняя к противнику — первой (formation).</summary>
    private List<(int X, int Y)> Formation(int x, int y, int side, int count, bool air)
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

    /// <summary>
    /// Показательный бой (spawnBattle): армия own игрока player слева от (x, y), foe учебного противника справа; передний
    /// ряд — в gap тайлах. Обе стороны агрессивны (стойка — прямо в Tactics). Вести их в бой — <see cref="DriveBattle"/>.
    /// </summary>
    public void SpawnBattle(uint player, int x, int y, IReadOnlyList<string>? own = null, IReadOnlyList<string>? foe = null, int gap = BattleGap,
        Func<double>? random = null)
    {
        own ??= RandomArmy(random: random);
        foe ??= RandomArmy(random: random);
        EnsurePlayer(player);
        EnsurePlayer(TrainingPlayer);
        foreach (var (owner, side, army) in new[] { (player, -1, own), (TrainingPlayer, 1, foe) })
            foreach (var air in new[] { false, true })
            {
                var types = army.Where(type => BattleTables.Value.Units[type].Kind == "air" == air).ToList();
                var tiles = Formation(x + side * gap, y, side, types.Count, air);
                var units = new List<uint>();
                for (var i = 0; i < types.Count && i < tiles.Count; i++)
                    units.Add(PutUnit(types[i], owner, tiles[i].X + 0.5, tiles[i].Y + 0.5));
                SetStance("aggressive", [.. units]);
            }
    }

    /// <summary>Боец показательного боя: где, чей, летает ли, бьёт ли по земле и по воздуху.</summary>
    private readonly record struct Fighter(uint Entity, uint Player, double X, double Y, bool Air, bool Ground, bool Sky);

    /// <summary>
    /// Ведёт показательный бой (driveBattle): каждому стоящему вооружённому без живой цели — ближайшего врага, которого он
    /// может достать. false — бой кончился: у стороны не осталось юнитов или никто не может достать другую сторону.
    /// </summary>
    public bool DriveBattle(uint player)
    {
        var data = BattleTables.Value;
        var units = new List<Fighter>();
        var mounted = new Dictionary<uint, List<uint>>();
        foreach (var turret in With(Names.Mount.Name))
        {
            var parent = (uint)Num(turret, "Mount.parent");
            if (!mounted.TryGetValue(parent, out var list))
                mounted[parent] = list = [];
            list.Add(turret);
        }
        List<uint> TurretsOf(uint carrier) => mounted.GetValueOrDefault(carrier) ?? [];
        foreach (var entity in With(Names.Unit.Name, Names.Position.Name, Names.Owner.Name))
        {
            var type = Host.Content.Protos[(int)Num(entity, "Proto.id")].Name;
            var weapons = new List<WeaponSpec>();
            foreach (var gunner in TurretsOf(entity).Prepend(entity))
            {
                var name = gunner == entity ? data.Units[type].Weapon : data.Turrets.GetValueOrDefault(Host.Content.Protos[(int)Num(gunner, "Proto.id")].Name)?.Weapon;
                if (name is not null)
                    weapons.Add(data.Weapons[name]);
            }
            var (x, y) = At(entity);
            units.Add(new Fighter(entity, OwnerOf(entity), x, y, data.Units[type].Kind == "air", weapons.Any(weapon => !weapon.OnlyAir), weapons.Any(weapon => weapon.Air)));
        }
        // Свободен тот, кто стоит, а ни у него, ни у его турелей нет живой цели. Идущего ведёт приказ игрока.
        var idle = new List<Fighter>();
        foreach (var unit in units)
        {
            if (Has(unit.Entity, Names.Path.Name))
                continue;
            var targets = TurretsOf(unit.Entity).Prepend(unit.Entity).Where(gunner => Has(gunner, Names.Armed.Name)).Select(TargetOf).ToList();
            if (targets.Count > 0 && !targets.Any(target => target != 0 && Alive(target) && Has(target, Names.Position.Name)))
                idle.Add(unit);
        }
        // Приказы — пачкой на цель (свои юниты одного игрока, в порядке idle): приказ атаки действует на каждого юнита
        // отдельно, так что пачка равна приказам по одному, а приказ мода в wasm — вызов с заметной ценой на каждый.
        var orders = new List<(uint Player, uint Target, List<uint> Units)>();
        var byTarget = new Dictionary<(uint, uint), int>();
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
            if (nearest == 0)
                continue;
            if (!byTarget.TryGetValue((self.Player, nearest), out var at))
            {
                byTarget[(self.Player, nearest)] = at = orders.Count;
                orders.Add((self.Player, nearest, []));
            }
            orders[at].Units.Add(self.Entity);
        }
        foreach (var (owner, target, group) in orders)
            Send(owner, new AttackOrder { Units = [.. group], Target = target });
        static bool Reaches(Fighter from, Fighter to) => from.Player != to.Player && (to.Air ? from.Sky : from.Ground);
        return units.Any(unit => unit.Player == player) && units.Any(unit => unit.Player != player) && units.Any(from => units.Any(to => Reaches(from, to)));
    }
}
