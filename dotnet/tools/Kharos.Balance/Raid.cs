using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Rules;
using Kharos.Base.Scenario;
using static Kharos.Balance.Report;

namespace Kharos.Balance;

/// <summary>
/// Раздел «набег» (tools/harness/raid.ts): нападение на экономику. Живая линия добычи на трубах (шахта, плавильня, двор
/// металла, главное здание и электростанция в одной сети) разгоняется и меряется, потом к ней подходит отряд с одной
/// целью: труба у шахты, сама шахта или двор. Меряется: за сколько цель снесена, чего это стоит защитнику, сколько груза
/// осталось на земле и сколько отряд теряет, если цель стережёт одна турель с полным боезапасом.
/// </summary>
public static class Raid
{
    private const int Gap = 12;
    private const double Limit = 120;
    private const double OrdersEvery = 0.25;
    private static readonly int OrdersTicks = (int)Math.Round(OrdersEvery / Lib.Tick);
    private const int Warmup = 60;
    private const int Window = 60;
    private const int After = 20;

    /// <summary>Игрок-защитник: его линия добычи. Атакующие — игрок 1, иначе свои цели не бьются.</summary>
    private const uint Defender = 2;

    private sealed record Squad(string Name, string[] Units);

    private static string[] Many(int count, string type) => [.. Enumerable.Repeat(type, count)];

    private static readonly Squad[] Squads =
    [
        new("12 пехоты", Many(12, "infantry")),
        new("8 ракетчиков", Many(8, "rocketeer")),
        new("4 багги", Many(4, "buggy")),
        new("4 танка", Many(4, "tank")),
        new("4 винтокрыла", Many(4, "gunship")),
    ];

    private static readonly (string Target, string Name)[] Targets = [("pipe", "труба"), ("mine", "шахта"), ("yard", "двор")];

    private sealed record Line(Sim Sim, uint Mine, uint Yard, uint Pipe);

    /// <summary>Линия добычи на трубах у ближайшего к началу мира месторождения металла.</summary>
    private static Line LineScene()
    {
        var sim = Lib.NewSim();
        foreach (var cell in Lib.Rings(0, 0, 6))
        {
            if (sim.DepositIn(cell.X, cell.Y) is not { Kind: "metal" } spot)
                continue;
            // Главное здание — первым: с его сетью автоподключение соединяет остальные.
            var core = Economy.PlaceNearMine(sim, spot, "command", spot.X + 8, spot.Y, Defender);
            var mine = sim.PutBuilding("mine", spot.X, spot.Y, Defender);
            var yard = Economy.PlaceNearMine(sim, spot, "metalYard", spot.X + 4, spot.Y + 5, Defender);
            var refinery = Economy.PlaceNearMine(sim, spot, "smelter", spot.X + 4, spot.Y - 5, Defender);
            var generator = Economy.PlaceNearMine(sim, spot, "generator", spot.X + 8, spot.Y - 5, Defender);
            if (yard == 0 || core == 0 || refinery == 0 || generator == 0)
                break;
            sim.ConnectAll(Defender);
            var network = sim.Network(mine).Buildings;
            if (network.Length == 0 || !network.Contains(yard) || !network.Contains(refinery))
                break;
            // Труба у самой шахты: перерезать её — отрезать шахту от плавильни.
            var at = Lib.CenterOf(sim, mine);
            uint pipe = 0;
            var best = double.PositiveInfinity;
            foreach (var entity in network)
            {
                if (Lib.Data.Buildings[Lib.TypeOf(sim, entity)].Pipe is null)
                    continue;
                var center = Lib.CenterOf(sim, entity);
                var distance = Math.Sqrt((center.X - at.X) * (center.X - at.X) + (center.Y - at.Y) * (center.Y - at.Y));
                if (distance < best)
                    (pipe, best) = (entity, distance);
            }
            if (pipe == 0)
                break;
            return new Line(sim, mine, yard, pipe);
        }
        sim.Dispose();
        throw new InvalidOperationException("Не нашлось места под линию добычи у месторождения металла");
    }

    /// <summary>Сколько металла лежит во дворе.</summary>
    private static double YardMetal(Sim sim, uint yard) => Lib.Amount(sim, yard, "metal");

    /// <summary>Сколько стоит груз, брошенный на землю, по цене продажи.</summary>
    private static double LootValue(Sim sim)
    {
        double value = 0;
        foreach (var drop in sim.With(Names.Drop.Name, Names.Stock.Name))
            foreach (var (resource, spec) in Lib.Data.Resources)
                value += Lib.Amount(sim, drop, resource) * spec.Price;
        return value;
    }

    private readonly record struct Result(double Flow, double DownAt, double After, double Loot, double Lost);

    /// <summary>Один набег: живая линия, отряд, цель и замер. guarded — цель стережёт турель с полным боезапасом.</summary>
    private static Result Attempt(Squad squad, string target, bool guarded)
    {
        var (sim, mine, yard, pipe) = LineScene();
        using var _ = sim;
        sim.Seconds(Warmup);
        var before = YardMetal(sim, yard);
        sim.Seconds(Window);
        var flow = (YardMetal(sim, yard) - before) / Window;

        var aim = target == "pipe" ? pipe : target == "mine" ? mine : yard;
        var type = Lib.TypeOf(sim, aim);
        var (placeX, placeY) = sim.At(aim);
        var center = Lib.CenterOf(sim, aim);
        if (guarded)
        {
            var turret = Lib.PlaceNear(sim, "turret", center.X + 2, center.Y + 2, Defender);
            if (turret != 0 && sim.Has(turret, Names.Stock.Name))
                sim.Set(turret, "Stock.ammo", sim.Num(turret, Names.Stock.Name, Names.Stock.Capacity));
        }
        sim.SpawnBattle(1, (int)Economy.JsRound(center.X), (int)Economy.JsRound(center.Y), squad.Units, [], Gap);
        double Cost(IEnumerable<uint> units) => units.Sum(entity => Lib.Data.Units[Lib.TypeOf(sim, entity)].Cost);
        var sent = Cost(Lib.UnitsOf(sim, 1));
        uint Standing()
        {
            foreach (var entity in sim.With(Names.Building.Name, Names.Position.Name))
            {
                var (x, y) = sim.At(entity);
                if (x == placeX && y == placeY && Lib.TypeOf(sim, entity) == type)
                    return entity;
            }
            return 0;
        }
        double downAt = 0;
        for (var tick = 0; tick < (int)Math.Round(Limit / Lib.Tick) && downAt == 0; tick++)
        {
            if (tick % OrdersTicks == 0)
            {
                var units = Lib.UnitsOf(sim, 1);
                var alive = Standing();
                if (units.Count == 0 || alive == 0)
                    break;
                sim.Send(1, new AttackOrder { Units = [.. units], Target = alive });
            }
            sim.Advance();
            if (Standing() == 0)
                downAt = (tick + 1) * Lib.Tick;
        }
        var lost = sent - Cost(Lib.UnitsOf(sim, 1));
        var loot = LootValue(sim);
        // Двор снесли — мерить поток негде: линия без хранилища стоит.
        double after = 0;
        if (sim.Has(yard, Names.Stock.Name) && target != "yard")
        {
            var left = YardMetal(sim, yard);
            sim.Seconds(After);
            after = Math.Max(0, (YardMetal(sim, yard) - left) / After);
        }
        return new Result(flow, downAt, after, loot, lost);
    }

    /// <summary>Печатает таблицу набегов.</summary>
    public static void Run()
    {
        Log("\nНабег на экономику: шахта, плавильня и двор металла в одной сети труб");
        Log($"  линия разгоняется {Warmup + Window} с, потом отряд в {Gap} тайлах получает одну цель; «с турелью» — цель стережёт пулемётная турель");
        var rows = new List<object[]>();
        double flow = 0;
        var costs = new Dictionary<string, string> { ["pipe"] = "pipe", ["mine"] = "mine", ["yard"] = "metalYard" };
        var metalPrice = Lib.Data.Resources["metal"].Price;
        foreach (var squad in Squads)
        {
            var price = squad.Units.Sum(type => Lib.Data.Units[type].Cost);
            foreach (var (target, name) in Targets)
            {
                Progress($"  набег: {squad.Name}, {name}");
                var open = Attempt(squad, target, false);
                var guarded = Attempt(squad, target, true);
                flow = open.Flow;
                var rebuild = Lib.Data.Buildings[costs[target]].Cost;
                // Простой: линия стоит, пока один строитель отстраивает снесённое. Труба режет линию только пока её нет.
                var stopped = target == "yard" || open.After < open.Flow * 0.5;
                var idle = stopped ? rebuild / BuildRules.BuildRate * open.Flow * metalPrice : 0;
                var damage = rebuild + idle;
                var gain = damage + guarded.Loot;
                rows.Add([
                    squad.Name, price, name,
                    open.DownAt != 0 ? Clock(open.DownAt) : "—",
                    rebuild, Round(idle), Round(open.Loot),
                    guarded.DownAt != 0 ? Clock(guarded.DownAt) : "—",
                    Round(guarded.Lost),
                    guarded.DownAt == 0 ? "отбит" : guarded.Lost <= gain ? "да" : "нет"]);
            }
        }
        Table(["отряд", "цена", "цель", "снесли", "стройка", "простой", "добыча", "с турелью", "потери", "окупился"], rows, 14, 10);
        Log("  снесли — за сколько без обороны; стройка — сколько защитнику отстроить; простой — выручка, потерянная, пока");
        Log("  один строитель отстраивает; добыча — груз на земле по цене продажи: дроп ничей, его увозит любой грузовик;");
        Log("  с турелью — за сколько снесли под огнём турели, потери — сколько кредитов отряда при этом погибло;");
        Log("  окупился — потери не больше ущерба (стройка + простой) и добычи; «отбит» — цель под турелью устояла");
        Log($"  линия добывает металла в секунду: {Num(Round(flow, 2))}, это {Num(Round(flow * metalPrice, 1))} кр/с выручки, пока цела");
    }
}
