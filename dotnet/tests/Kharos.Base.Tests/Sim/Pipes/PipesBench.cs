using System.Globalization;
using Base.Shared.Orders;
using static Kharos.Base.Tests.Logistics.LogisticsScene;

namespace Kharos.Base.Tests.Pipes;

/// <summary>
/// Замер труб на масштабе (не из TS): 10 баз, в каждой — 12 зданий со складом на общей трубе (≈ 50 тайлов трубы):
/// переработка развозит готовое по хранилищам, цеха и станция запроса заказывают сырьё, турели — патроны. Печатает мс на
/// тик системы pipeFlow. Только по KHAROS_BENCH=1 (режим — KHAROS_MODS; итог — и в файл KHAROS_BENCH_OUT).
/// </summary>
public sealed class PipesBench
{
    [Fact]
    public void PipeFlowAtScale()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KHAROS_BENCH") == "1", "замер — по KHAROS_BENCH=1");
        using var sim = Blank(256);
        sim.SetCredits(1, 100000);
        var buildings = 0;
        var pipes = 0;
        for (var b = 0; b < 10; b++)
        {
            var (x, y) = (-200 + b % 5 * 80, -100 + b / 5 * 80);
            // Ряд зданий над трубой (нижним краем к ней) и ряд под ней: всё в одной сети.
            Place(sim, "command", x, y);
            Place(sim, "generator", x + 4, y + 1);
            Place(sim, "generator", x + 7, y + 1);
            var smelters = new[] { Place(sim, "smelter", x + 10, y), Place(sim, "smelter", x + 16, y) };
            var plants = new[] { Place(sim, "blockPlant", x + 22, y + 1), Place(sim, "ammoPlant", x + 26, y + 1) };
            var station = Place(sim, "requestStation", x + 30, y + 1);
            var turrets = new[] { Place(sim, "turret", x + 36, y + 2), Place(sim, "turret", x + 40, y + 2) };
            var yards = new[] { Place(sim, "metalYard", x, y + 4), Place(sim, "metalYard", x + 6, y + 4), Place(sim, "metalYard", x + 12, y + 4) };
            var silicon = Place(sim, "siliconStore", x + 18, y + 4);
            Place(sim, "blockYard", x + 24, y + 4);
            var bunker = Place(sim, "ammoBunker", x + 30, y + 4);
            buildings += 12;
            for (var dx = 0; dx < 44; dx++, pipes++)
                Place(sim, "pipe", x + dx, y + 3);
            foreach (var smelter in smelters)
            {
                sim.SetStock(smelter, "metalOre", 30);
                sim.SetStock(smelter, "metal", 20);
            }
            sim.SetStock(yards[0], "metal", 300);
            sim.SetStock(silicon, "silicon", 200);
            sim.SetStock(bunker, "ammo", 300);
            foreach (var turret in turrets)
                sim.SetStock(turret, "ammo", 0);
            foreach (var plant in plants)
                sim.Send(1, new WorkOrder { Building = plant, On = true });
            sim.Send(1, new WantOrder { Buildings = [station], Resource = "metal", Amount = 60 });
        }
        sim.Advance(40);
        var before = sim.Host.Scheduler.Timings().ToDictionary(t => t.Name);
        const int ticks = 400;
        var batches = 0;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < ticks; i++)
        {
            sim.Advance();
            batches += sim.Count("Batch");
        }
        var total = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds / ticks;
        var lines = new List<string> { $"режим {sim.Mode}, 10 баз, {buildings} зданий, {pipes} тайлов трубы, {ticks} тиков; пачек в пути в среднем {F((double)batches / ticks)}; тик целиком {F(total)} мс" };
        foreach (var timing in sim.Host.Scheduler.Timings())
        {
            if (!timing.Name.EndsWith("pipeFlow", StringComparison.Ordinal) && !timing.Name.EndsWith("haul", StringComparison.Ordinal))
                continue;
            var was = before[timing.Name];
            lines.Add($"{timing.Name}: сбор {F((timing.Gather - was.Gather) / ticks)}, система {F((timing.Run - was.Run) / ticks)}, " +
                $"раздача {F((timing.Scatter - was.Scatter) / ticks)}, команды {F((timing.Commands - was.Commands) / ticks)} мс на тик");
        }
        TestContext.Current.SendDiagnosticMessage(string.Join("\n", lines));
        Console.WriteLine(string.Join("\n", lines));
        // KHAROS_BENCH_OUT — файл, куда дописать итог (вывод тестов xunit в консоль не попадает).
        if (Environment.GetEnvironmentVariable("KHAROS_BENCH_OUT") is { Length: > 0 } file)
            File.AppendAllText(file, string.Join("\n", lines) + "\n");
    }

    private static string F(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);
}
