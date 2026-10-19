using System.Diagnostics;
using System.Globalization;
using Base.Shared.Orders;
using static Kharos.Base.Tests.Logistics.LogisticsScene;

namespace Kharos.Base.Tests.Combat;

/// <summary>
/// Ворота замера Н5.2 (не из TS): весь тик хоста при ~10 тыс. сущностей — две армии в бою и базы с логистикой и энергией.
/// Печатает тик целиком, сумму систем мода и движка и самые дорогие системы. Только по KHAROS_BENCH=1 (режим —
/// KHAROS_MODS); KHAROS_BENCH_ARMY — бюджет армии одной стороны (по умолчанию 300 тыс. кредитов).
/// </summary>
public sealed class CombatBench
{
    [Fact]
    public void TickAtScale()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KHAROS_BENCH") == "1", "замер — по KHAROS_BENCH=1");
        var budget = double.TryParse(Environment.GetEnvironmentVariable("KHAROS_BENCH_ARMY"), CultureInfo.InvariantCulture, out var b) ? b : 300000;
        using var sim = Blank(512);
        sim.SetCredits(1, 1_000_000);
        // Базы: по 10 зданий со складом, электростанции, турели и 5 грузовиков.
        var trucks = 0;
        for (var i = 0; i < 20; i++)
        {
            var (x, y) = (-450 + i % 5 * 90, 250 + i / 5 * 50);
            Place(sim, "command", x, y);
            Place(sim, "generator", x + 4, y);
            Place(sim, "generator", x + 4, y + 3);
            var smelters = new[] { Place(sim, "smelter", x + 8, y), Place(sim, "smelter", x + 14, y) };
            var yards = new[] { Place(sim, "metalYard", x, y + 8), Place(sim, "metalYard", x + 6, y + 8), Place(sim, "metalYard", x + 12, y + 8) };
            var bunker = Place(sim, "ammoBunker", x + 18, y + 8);
            var turrets = new[] { Place(sim, "turret", x + 22, y), Place(sim, "turret", x + 24, y), Place(sim, "laserTurret", x + 26, y) };
            foreach (var smelter in smelters)
            {
                sim.SetStock(smelter, "metalOre", 30);
                sim.SetStock(smelter, "metal", 20);
            }
            sim.SetStock(yards[0], "metal", 200);
            sim.SetStock(bunker, "ammo", 300);
            foreach (var turret in turrets.Take(2))
                sim.SetStock(turret, "ammo", 10);
            var own = new List<uint>();
            for (var t = 0; t < 5; t++)
                own.Add(Spawn(sim, "truck", 1, x + t * 2, y + 14));
            trucks += own.Count;
            sim.Send(1, new RouteOrder { Units = [own[^1], own[^2]], Stops = [yards[0], yards[2]] });
        }
        // Две армии лицом к лицу (показательный бой): агрессивные, сходятся сами.
        long seed = 7;
        var army = Sim.RandomArmy(budget, null, () => (seed = seed * 16807 % 2147483647) / 2147483647.0);
        sim.SpawnBattle(1, 0, -100, army, army, 4);
        // Разгон: армии сходятся и завязывают бой.
        for (var i = 0; i < 100; i++)
        {
            if (i % 20 == 0)
                sim.DriveBattle(1);
            sim.Advance();
        }
        var entities = sim.World.Count;
        var before = sim.Host.Scheduler.Timings().ToDictionary(t => t.Name);
        const int ticks = 200;
        var spent = new double[ticks];
        for (var i = 0; i < ticks; i++)
        {
            if (i % 20 == 0)
                sim.DriveBattle(1);
            var from = Stopwatch.GetTimestamp();
            sim.Advance();
            spent[i] = Stopwatch.GetElapsedTime(from).TotalMilliseconds;
        }
        var rows = sim.Host.Scheduler.Timings()
            .Select(t => (t.Name, Ms: ((t.Gather + t.Run + t.Scatter + t.Commands) - (before.TryGetValue(t.Name, out var was) ? was.Gather + was.Run + was.Scatter + was.Commands : 0)) / ticks,
                Run: (t.Run - (before.TryGetValue(t.Name, out var w2) ? w2.Run : 0)) / ticks))
            .OrderByDescending(row => row.Ms).ToList();
        var mod = rows.Where(row => !row.Name.StartsWith("engine.", StringComparison.Ordinal)).Sum(row => row.Ms);
        var engine = rows.Where(row => row.Name.StartsWith("engine.", StringComparison.Ordinal)).Sum(row => row.Ms);
        var lines = new List<string>
        {
            $"режим {sim.Mode}: сущностей {entities} (армия {army.Count}×2, грузовиков {trucks}), после замера {sim.World.Count}; {ticks} тиков",
            $"тик целиком: среднее {F(spent.Average())} мс, худший {F(spent.Max())} мс; системы мода {F(mod)} мс, движка {F(engine)} мс",
        };
        foreach (var row in rows.Take(14))
            lines.Add($"  {row.Name}: {F(row.Ms)} мс (система {F(row.Run)})");
        TestContext.Current.SendDiagnosticMessage(string.Join("\n", lines));
        Console.WriteLine(string.Join("\n", lines));
        // Вывод теста виден не всегда: итог — ещё и в файл (KHAROS_BENCH_OUT, по умолчанию — во временной папке).
        var file = Environment.GetEnvironmentVariable("KHAROS_BENCH_OUT") is { Length: > 0 } path ? path : Path.Combine(Path.GetTempPath(), "kharos-combat-bench.txt");
        File.AppendAllText(file, string.Join("\n", lines) + "\n\n");
    }

    private static string F(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
