using System.Globalization;
using Base.Shared.Orders;
using static Kharos.Base.Tests.Logistics.LogisticsScene;

namespace Kharos.Base.Tests.Logistics;

/// <summary>
/// Замер логистики на масштабе (не из TS): 10 баз по 10 зданий со складом (100 зданий) и 50 грузовиков — переработка
/// толкает готовое в хранилища, турели и станции заказывают, часть грузовиков ездит маршрутами. Печатает мс на тик систем
/// haul и fetchMaterials. Только по KHAROS_BENCH=1 (режим — KHAROS_MODS).
/// </summary>
public sealed class LogisticsBench
{
    [Fact]
    public void HaulAtScale()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KHAROS_BENCH") == "1", "замер — по KHAROS_BENCH=1");
        using var sim = Blank(256);
        sim.SetCredits(1, 100000);
        var trucks = new List<uint>();
        var buildings = 0;
        for (var b = 0; b < 10; b++)
        {
            var (x, y) = (-200 + b % 5 * 80, -100 + b / 5 * 80);
            Place(sim, "command", x, y);
            var smelters = new[] { Place(sim, "smelter", x + 6, y), Place(sim, "smelter", x + 12, y) };
            var yards = new[] { Place(sim, "metalYard", x, y + 8), Place(sim, "metalYard", x + 6, y + 8), Place(sim, "metalYard", x + 12, y + 8) };
            var bunker = Place(sim, "ammoBunker", x + 18, y + 8);
            var turrets = new[] { Place(sim, "turret", x + 20, y), Place(sim, "turret", x + 24, y) };
            var station = Place(sim, "requestStation", x + 18, y);
            buildings += 10;
            foreach (var smelter in smelters)
            {
                sim.SetStock(smelter, "metalOre", 30);
                sim.SetStock(smelter, "metal", 20);
            }
            sim.SetStock(yards[0], "metal", 200);
            sim.SetStock(bunker, "ammo", 300);
            foreach (var turret in turrets)
                sim.SetStock(turret, "ammo", 0);
            sim.Send(1, new WantOrder { Buildings = [station], Resource = "metal", Amount = 60 });
            for (var t = 0; t < 5; t++)
                trucks.Add(Spawn(sim, "truck", 1, x + t * 2, y + 14));
            // Двое из пяти — маршрутом между хранилищами.
            sim.Send(1, new RouteOrder { Units = [trucks[^1], trucks[^2]], Stops = [yards[0], yards[2]] });
        }
        Assert.Equal(100, buildings);
        sim.Advance(40);
        var before = sim.Host.Scheduler.Timings().ToDictionary(t => t.Name);
        const int ticks = 400;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        // Пик — тик диспетчера: раз в 10 тиков он раздаёт работу.
        var spent = new double[ticks];
        for (var i = 0; i < ticks; i++)
        {
            var was = Haul(sim);
            sim.Advance();
            spent[i] = Haul(sim) - was;
        }
        var peak = spent.Max();
        var dispatch = spent.Where((_, i) => (sim.Tick - (ulong)ticks + (ulong)i) % 10 == 0).Average();
        var total = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds / ticks;
        var lines = new List<string> { $"режим {sim.Mode}, {trucks.Count} грузовиков, {buildings} зданий, {ticks} тиков; тик целиком {F(total)} мс" };
        foreach (var timing in sim.Host.Scheduler.Timings())
        {
            if (!timing.Name.StartsWith("base:logistics/", StringComparison.Ordinal) && !timing.Name.StartsWith("logistics/", StringComparison.Ordinal))
                continue;
            var was = before[timing.Name];
            lines.Add($"{timing.Name}: сбор {F((timing.Gather - was.Gather) / ticks)}, система {F((timing.Run - was.Run) / ticks)}, " +
                $"раздача {F((timing.Scatter - was.Scatter) / ticks)}, команды {F((timing.Commands - was.Commands) / ticks)} мс на тик");
        }
        var moved = trucks.Count(truck => sim.Hauler(truck).From != 0 || sim.Hauler(truck).Route.Length > 0 || sim.Stock(truck, "metal") > 0);
        lines.Add($"haul: в тике диспетчера в среднем {F(dispatch)} мс, в самом долгом тике {F(peak)} мс (тик {Array.IndexOf(spent, peak)}); грузовиков при деле: {moved}");
        TestContext.Current.SendDiagnosticMessage(string.Join("\n", lines));
        Console.WriteLine(string.Join("\n", lines));
    }

    /// <summary>Сколько всего проработала система haul (мс).</summary>
    private static double Haul(Sim sim) => sim.Host.Scheduler.Timings().Where(t => t.Name.EndsWith("logistics/haul", StringComparison.Ordinal)).Sum(t => t.Run);

    private static string F(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);
}
