using Base.Shared;
using Base.Shared.Orders;
using Kharos.Base.Scenario;
using static Kharos.Balance.Report;

namespace Kharos.Balance;

/// <summary>
/// Раздел «осада» (tools/harness/siege.ts): штурм узла обороны. Кольцо стен 7×7, внутри стрелковая, ракетная и пушечная
/// турели; отряд встаёт в 12 тайлах от стены и получает приказы, как дал бы игрок: пробить стену, потом добить турели.
/// У каждого отряда три строки: узел как есть; со строителем внутри (чинит); под куполом щита.
/// </summary>
public static class Siege
{
    /// <summary>Половина стороны кольца: стены стоят на кольце от −3 до 3, внутри 5×5.</summary>
    private const int Ring = 3;

    /// <summary>На сколько тайлов от стены встают атакующие.</summary>
    private const int Gap = 12;

    /// <summary>Сколько секунд отводится штурму.</summary>
    private const double Limit = 240;

    /// <summary>Как часто атакующим напоминают цель, в секундах: как в показательном бою.</summary>
    private const double OrdersEvery = 0.25;

    private static readonly int OrdersTicks = (int)Math.Round(OrdersEvery / Lib.Tick);

    /// <summary>Игрок-защитник и его кошелёк: ремонт тратит кредиты, и по трате видно, во что он обошёлся.</summary>
    private const uint Defender = 2;

    private const double RepairCredits = 2000;

    private sealed record Squad(string Name, string[] Units);

    private static string[] Many(int count, string type) => [.. Enumerable.Repeat(type, count)];

    private static readonly Squad[] Squads =
    [
        new("12 пехоты", Many(12, "infantry")),
        new("8 ракетчиков", Many(8, "rocketeer")),
        new("4 багги", Many(4, "buggy")),
        new("2 танка", Many(2, "tank")),
        new("4 танка", Many(4, "tank")),
        new("8 танков", Many(8, "tank")),
        new("3 артиллерии", Many(3, "artillery")),
        new("3 артиллерии и 4 багги", [.. Many(3, "artillery"), .. Many(4, "buggy")]),
        new("2 носителя", Many(2, "carrier")),
        new("3 бомбардировщика", Many(3, "bomber")),
        new("4 винтокрыла", Many(4, "gunship")),
    ];

    private readonly record struct Spot(string Type, int X, int Y);

    /// <summary>Кольцо стен 7×7 и три турели в ряд внутри.</summary>
    private static (List<Spot> Walls, Spot[] Turrets) Layout(int ax, int ay)
    {
        var walls = new List<Spot>();
        for (var dx = 0; dx <= 2 * Ring; dx++)
            for (var dy = 0; dy <= 2 * Ring; dy++)
                if (dx == 0 || dy == 0 || dx == 2 * Ring || dy == 2 * Ring)
                    walls.Add(new Spot("wall", ax + dx, ay + dy));
        Spot[] turrets = [new("turret", ax + 1, ay + Ring), new("rocketTurret", ax + Ring, ay + Ring), new("cannonTurret", ax + 2 * Ring - 1, ay + Ring)];
        return (walls, turrets);
    }

    /// <summary>Живое здание на этом месте: 0 — места пусто или там уже другое.</summary>
    private static uint EntityAt(Sim sim, Spot spot) => Lib.BuildingAt(sim, spot.Type, spot.X, spot.Y);

    /// <summary>
    /// Ровная скальная площадка под узел: проходимая вся и без песка под обороной. В отличие от TS, где правка ставит здание
    /// куда угодно (и стена могла встать на болото), здесь ещё и место, где стену можно поставить: правки .NET проверяют место.
    /// </summary>
    private static (int X, int Y) Ground(Sim sim, int width, int height)
    {
        for (var y = -200; y < 200; y++)
            for (var x = -200; x < 200; x++)
            {
                var fits = true;
                for (var dy = 0; dy < height && fits; dy++)
                    for (var dx = 0; dx < width && fits; dx++)
                        fits = Lib.IsWalkable(sim, x + dx, y + dy) && sim.Durability("wall", x + dx, y + dy) >= 1 && Lib.CanPlace(sim, "wall", x + dx, y + dy);
                if (fits)
                    return (x, y);
            }
        throw new InvalidOperationException($"Не нашлось скальной площадки {width}×{height} под узел обороны");
    }

    private enum Guard { Bare, Repair, Shield }

    /// <summary>Узел обороны в симуляции: стены, турели и строитель внутри (repair) или щит с электростанцией (shield).</summary>
    private static (Sim Sim, Spot[] Turrets, Spot Gate) Node(Guard guard)
    {
        var sim = Lib.NewSim();
        var at = Ground(sim, 2 * Ring + 3, 2 * Ring + 3);
        var (walls, turrets) = Layout(at.X + 1, at.Y + 1);
        foreach (var spot in walls.Concat(turrets))
            sim.PutBuilding(spot.Type, spot.X, spot.Y, Defender);
        sim.SetCredits(Defender, RepairCredits);
        if (guard == Guard.Shield)
        {
            // Над рядом турелей: электростанция в левом верхнем углу кольца, генератор щита — в правом.
            sim.PutBuilding("generator", at.X + 2, at.Y + 2, Defender);
            var shield = sim.PutBuilding("shieldGenerator", at.X + 2 * Ring - 1, at.Y + 2, Defender);
            sim.Set(shield, "Shield.charge", Lib.Data.Buildings["shieldGenerator"].Shield!.Capacity);
        }
        if (guard == Guard.Repair)
        {
            // Строитель встаёт на первый свободный тайл внутри кольца и сам берётся чинить повреждённое рядом.
            (int X, int Y)? tile = null;
            for (var dy = 1; dy <= 2 * Ring - 1 && tile is null; dy++)
                for (var dx = 1; dx <= 2 * Ring - 1 && tile is null; dx++)
                    if (Lib.IsWalkable(sim, at.X + 1 + dx, at.Y + 1 + dy))
                        tile = (at.X + 1 + dx, at.Y + 1 + dy);
            tile ??= (at.X + 1 + Ring, at.Y + 1 + Ring);
            sim.PutUnit("builder", Defender, tile.Value.X + 0.5, tile.Value.Y + 0.5);
        }
        // Стена, которую будут пробивать: середина левой.
        var gate = walls.First(spot => spot.X == at.X + 1 && spot.Y == at.Y + 1 + Ring);
        return (sim, turrets, gate);
    }

    /// <summary>Итог одного штурма: секунда пролома (0 — не пробили), потери юнитов и кредитов, снесено турелей, траты на ремонт.</summary>
    private readonly record struct Result(double Breach, int LostUnits, double Lost, int Killed, double Spend);

    /// <summary>Один штурм: узел, отряд, приказы и замер.</summary>
    private static Result Assault(Squad squad, Guard guard)
    {
        var (sim, turrets, gate) = Node(guard);
        using var _ = sim;
        sim.SpawnBattle(1, gate.X, gate.Y, squad.Units, [], Gap);
        var kind = squad.Units[0];
        var air = Lib.Data.Units[kind].Kind == "air";
        var spendBefore = sim.Credits(Defender);

        double breach = 0;
        var killed = 0;
        for (var tick = 0; tick < (int)Math.Round(Limit / Lib.Tick); tick++)
        {
            if (tick % OrdersTicks == 0)
            {
                if (Lib.CountUnits(sim, 1, kind) == 0)
                    break;
                foreach (var spot in turrets)
                    if (EntityAt(sim, spot) is var turret and not 0 && sim.Has(turret, Names.Stock.Name))
                        sim.Set(turret, "Stock.ammo", sim.Num(turret, Names.Stock.Name, Names.Stock.Capacity));
                // Приказ как у игрока: сперва стена, после пролома — ближайшая живая турель. Летящие идут к турели сразу.
                var wall = EntityAt(sim, gate);
                var turretTarget = turrets.Select(spot => EntityAt(sim, spot)).FirstOrDefault(entity => entity != 0);
                var target = air || wall == 0 ? turretTarget : wall;
                if (target != 0)
                {
                    var units = sim.Of(1, Names.Unit.Name, Names.Proto.Name).Where(entity => Lib.TypeOf(sim, entity) == kind).ToArray();
                    sim.Send(1, new AttackOrder { Units = units, Target = target });
                }
            }
            sim.Advance();
            if (breach == 0 && EntityAt(sim, gate) == 0)
                breach = (tick + 1) * Lib.Tick;
            killed = turrets.Count(spot => EntityAt(sim, spot) == 0);
            if (killed == turrets.Length)
                break;
        }

        if (Environment.GetEnvironmentVariable("KHAROS_BALANCE_DEBUG") == "1")
        {
            Console.Error.WriteLine($"DBG {squad.Name} {guard} gate={gate} turrets={string.Join(",", turrets.Select(t => EntityAt(sim, t)))}");
            foreach (var unit in Lib.UnitsOf(sim, 1))
            {
                var (ux, uy) = sim.At(unit);
                Console.Error.WriteLine($"  unit {unit} {Lib.TypeOf(sim, unit)} at ({ux:0.0},{uy:0.0}) path={sim.Has(unit, Names.Path.Name)} target={sim.TargetOf(unit)} hp={sim.HealthOf(unit)}");
            }
        }
        var lostUnits = squad.Units.Length - Lib.CountUnits(sim, 1, kind);
        return new Result(breach, lostUnits, lostUnits * Lib.Data.Units[kind].Cost, killed,
            guard == Guard.Repair ? Economy.JsRound(spendBefore - sim.Credits(Defender)) : 0);
    }

    private static object[] Row(Squad squad, double cost, Result result, Guard guard)
    {
        var withRepair = guard == Guard.Repair;
        return
        [
            guard == Guard.Bare ? squad.Name : $"{squad.Name} /{(withRepair ? "ремонт" : "щит")}",
            cost,
            result.Breach != 0 ? Clock(result.Breach) : "—",
            $"{result.LostUnits}/{squad.Units.Length}",
            result.Lost,
            withRepair ? result.Spend : "—",
            $"{result.Killed}/3",
        ];
    }

    /// <summary>Печатает таблицу штурмов и выводы по числам.</summary>
    public static void PrintSieges()
    {
        Log("\nШтурм узла обороны: кольцо стен 7×7, внутри стрелковая, ракетная и пушечная турели");
        Log($"  атакующие встают в {Gap} тайлах и бьют в стену, после пролома — в турели; винтокрылы летят через стену");
        var only = Environment.GetEnvironmentVariable("KHAROS_BALANCE_ONLY");
        var squads = Squads.Where(squad => only is not { Length: > 0 } || squad.Name.Contains(only)).ToList();
        // Все штурмы (отряд и оборона) — разом.
        Guard[] guards = [Guard.Bare, Guard.Repair, Guard.Shield];
        var assaults = squads.SelectMany(squad => guards.Select(guard => (Squad: squad, Guard: guard))).ToList();
        var results = Work.Map(assaults, item =>
        {
            Progress($"  штурм: {item.Squad.Name}, {item.Guard}");
            return Assault(item.Squad, item.Guard);
        });
        var all = squads.Select((squad, i) => (Squad: squad, Cost: squad.Units.Sum(type => Lib.Data.Units[type].Cost),
            Bare: results[3 * i], Repair: results[3 * i + 1], Shield: results[3 * i + 2])).ToList();
        var rows = new List<object[]>();
        foreach (var item in all)
        {
            rows.Add(Row(item.Squad, item.Cost, item.Bare, Guard.Bare));
            rows.Add(Row(item.Squad, item.Cost, item.Repair, Guard.Repair));
            rows.Add(Row(item.Squad, item.Cost, item.Shield, Guard.Shield));
        }
        Table(["отряд", "цена", "стена", "потери", "потери кр", "ремонт", "турелей"], rows, 20, 9);
        Log("  стена — когда пробили кольцо; потери — юнитов и кредитов у атакующего;");
        Log($"  ремонт — сколько защитник потратил из своих {Num(RepairCredits)} кр; турелей — сколько снёс из трёх");
        foreach (var item in all)
        {
            if (item.Bare.Killed == 3 && item.Bare.Breach == 0)
                Log($"  {item.Squad.Name}: турели снесены, не тронув стены");
            else if (item.Bare.Breach > 0 && item.Bare.Killed == 0)
                Log($"  {item.Squad.Name}: стену пробили за {Clock(item.Bare.Breach)}, но турели устояли");
        }
    }

    /// <summary>Раздел целиком.</summary>
    public static void Run() => PrintSieges();
}
