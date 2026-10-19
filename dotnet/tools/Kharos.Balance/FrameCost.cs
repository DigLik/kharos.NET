using System.Diagnostics;
using Base.Shared.Orders;
using Kharos.Base.Scenario;
using Kharos.Client.Game;

namespace Kharos.Balance;

/// <summary>
/// Цена кадра клиента (docs/dotnet-n6.md, Н6.6: kh_frame ≤ 4 мс при 1000 видимых юнитов): база из 50 зданий и два войска
/// по 500 юнитов на экране 1280 × 800 сходятся в бой — выстрелы, взрывы, дым, следы. На каждый тик хоста — три кадра
/// клиента (60 к/с); замер — после разгона. Печатает среднее, p95 и худшее: весь кадр клиента без видеокарты
/// (ClientGame.Step), приём, моды (вход, kh_frame, разбор выхода), сам kh_frame и сборку кадра рендера.
/// <c>dotnet run --project tools/Kharos.Balance -c Release -- frame [тиков]</c>; KHAROS_MODS=clr или wasm.
/// </summary>
public static class FrameCost
{
    private static readonly string[] BuildingTypes = ["generator", "smelter", "factory", "turret", "command", "barracks", "radar", "metalYard", "lab", "accumulator"];
    private static readonly string[] UnitTypes = ["tank", "infantry", "buggy", "rocketeer", "lancer"];

    public static int Run(int ticks)
    {
        var mode = Sim.DefaultMode;
        Console.WriteLine($"режим {mode}: строю сцену");
        using var sim = Sim.Create(new SimOptions { Game = "frames", Size = 128, Fog = false, Land = new { blank = true }, Players = [1, 2] });
        var (buildings, tried) = (0, 0);
        for (var row = 0; row < 4 && buildings < 50; row++)
            for (var x = -38; x < 38 && buildings < 50; x += 4)
            {
                var type = BuildingTypes[tried++ % BuildingTypes.Length];
                var (w, _) = ViewerScenes.Footprint(sim, type);
                if (x + w > 38)
                    break;
                try
                {
                    ViewerScenes.Place(sim, type, x, -24 + row * 4, buildings % 3 == 0 ? 2u : 1u);
                    buildings++;
                }
                catch (InvalidOperationException error)
                {
                    Console.WriteLine($"{type} ({x}, {-24 + row * 4}): {error.Message}");
                }
            }
        var own = new List<uint>();
        var foes = new List<uint>();
        for (var i = 0; i < 1000; i++)
        {
            var side = i % 2;
            var (column, line) = (i / 2 % 50, i / 2 / 50);
            var unit = sim.PutUnit(UnitTypes[i / 2 % UnitTypes.Length], side == 0 ? 1u : 2u, -25 + column + 0.5, (side == 0 ? -8 : 6) + line + 0.5);
            (side == 0 ? own : foes).Add(unit);
        }
        sim.Send(1, new StanceOrder { Units = [.. own], Stance = "aggressive" });
        sim.Send(2, new StanceOrder { Units = [.. foes], Stance = "aggressive" });
        sim.Send(1, new MoveOrder { Units = [.. own], X = 0, Y = 6 });
        sim.Send(2, new MoveOrder { Units = [.. foes], X = 0, Y = -6 });

        using var viewer = sim.Viewer(1, new ViewerOptions { Width = 1280, Height = 800 });
        viewer.Camera = new CameraState(0, -6, 16);
        // Разгон: снимок, JIT, первые выстрелы.
        viewer.Run(1);
        var stats = new List<ClientFrameStats>();
        var visible = 0;
        var wall = Stopwatch.StartNew();
        for (var tick = 0; tick < ticks; tick++)
        {
            sim.Advance();
            for (var i = 0; i < 3; i++)
            {
                viewer.Frame();
                stats.Add(viewer.Game.Stats);
            }
            visible = Math.Max(visible, viewer.Game.Stats.Sprites);
        }
        wall.Stop();
        var alive = sim.With("Unit").Count;
        Console.WriteLine($"режим {mode}: зданий {buildings}, юнитов в начале 1000, в конце {alive}; кадров {stats.Count}, спрайтов в кадре до {visible}; {wall.Elapsed.TotalSeconds:F1} с вместе с тиками хоста");
        Print("кадр клиента (Step)", stats.Select(s => s.Total));
        Print("  приём", stats.Select(s => s.Pump));
        Print("  моды (вход, код, разбор)", stats.Select(s => s.Mods));
        Print("    kh_frame", stats.Select(s => s.Code));
        Print("  сборка кадра", stats.Select(s => s.Build));
        return 0;
    }

    private static void Print(string name, IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        Console.WriteLine($"{name,-30} среднее {sorted.Average(),6:F2} мс  p95 {sorted[(int)(sorted.Length * 0.95)],6:F2}  худший {sorted[^1],6:F2}");
    }
}
