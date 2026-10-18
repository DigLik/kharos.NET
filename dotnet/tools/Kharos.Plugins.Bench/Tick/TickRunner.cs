using System.Diagnostics;
using System.Globalization;
using Base.Shared.Orders;
using Hello;
using Kharos.Content;
using Kharos.Engine.World;
using Kharos.Host;
using Kharos.Plugins.Game;
using Kharos.Protocol;
using MemoryPack;
using KHost = Kharos.Host.Host;

namespace Kharos.Plugins.Bench.Tick;

/// <summary>
/// Замер тика целиком через хост (docs/dotnet-n5.md, Д5): пустая партия skirmish мода base и 10 тыс. юнитов hello,
/// каждая — в режимах CLR и wasm. Тик хоста — приказы, системы мода и движка, обзор; затем сборка и кодирование дельты
/// для клиента (FullView). После — таблица по системам: <c>Scheduler.Timings()</c> за время замера, мс на тик.
/// Когда у base появятся механики, <c>--units N</c> поставит N юнитов правками edit.unit.
/// </summary>
public static class TickRunner
{
    private const int DefaultTicks = 200;
    private const int Warmup = 20;

    /// <summary>Параметры замера из командной строки.</summary>
    public sealed record Options(int Ticks, int Units, ModMode[] Modes, string[] Scenarios, int Top);

    /// <summary>Разбор <c>--tick [--ticks N] [--units N] [--mode clr|wasm] [--scenario base|hello] [--top N]</c>.</summary>
    public static Options Parse(string[] args)
    {
        int Int(string name, int fallback)
        {
            var at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var value) ? value : fallback;
        }

        string? Text(string name)
        {
            var at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }

        var modes = Text("--mode")?.ToLowerInvariant() switch
        {
            "clr" => new[] { ModMode.Clr },
            "wasm" => new[] { ModMode.Wasm },
            _ => new[] { ModMode.Clr, ModMode.Wasm },
        };
        var scenarios = Text("--scenario")?.ToLowerInvariant() switch
        {
            "base" => new[] { "base" },
            "hello" => new[] { "hello" },
            _ => new[] { "base", "hello" },
        };
        return new Options(Int("--ticks", DefaultTicks), Int("--units", 0), modes, scenarios, Int("--top", 12));
    }

    /// <summary>Запуск: все сценарии во всех режимах.</summary>
    public static void Run(Options options)
    {
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine($"Тик через хост: {options.Ticks} тиков после разогрева {Warmup}; бюджет тика 50 мс");
        Console.WriteLine("--------------------------------------------------------------------------------");
        foreach (var scenario in options.Scenarios)
            foreach (var mode in options.Modes)
            {
                Console.WriteLine();
                try
                {
                    var result = scenario == "base" ? MeasureBase(mode, options) : MeasureHello(mode, options);
                    if (result is not null)
                        Print(result, options.Top);
                }
                catch (Exception error) when (error is HostException or ContentException or ModException)
                {
                    Console.WriteLine($"[{scenario} / {mode}] не поднимается: {error.Message}");
                }
            }
    }

    // ===== Сценарии =====

    private static Result? MeasureBase(ModMode mode, Options options)
    {
        var dir = Path.Combine(DotnetRoot(), "mods", "base");
        if (Unavailable(dir, mode) is { } reason)
        {
            Console.WriteLine($"[base / {mode}] пропущено: {reason}");
            return null;
        }
        using var host = new KHost(GameContent.Load([ModPackage.Read(dir)]), new HostOptions
        {
            Game = "skirmish",
            ModMode = mode,
            Seed = 1,
            Modules = new GameModuleOptions { CallTimeoutMs = 10_000 },
        });
        host.Start();
        host.Join(1);
        host.Join(2);
        var title = "base skirmish, пустая партия, 2 игрока";
        Action? spawn = null;
        if (options.Units > 0)
        {
            if (host.HasOrder(EditUnitOrder.Name))
            {
                title = $"base skirmish, {options.Units} юнитов buggy, 2 игрока (правки edit.unit)";
                spawn = () =>
                {
                    // Сетка через два тайла вокруг начала карты, юниты поровну у двух игроков.
                    var side = (int)Math.Ceiling(Math.Sqrt(options.Units));
                    for (var i = 0; i < options.Units; i++)
                        host.Send(0, EditUnitOrder.Name, MemoryPackSerializer.Serialize(new EditUnitOrder
                        {
                            Type = "buggy",
                            X = (i % side - side / 2) * 2 + 0.5,
                            Y = (i / side - side / 2) * 2 + 0.5,
                            Player = (uint)(i % 2 + 1),
                        }), OrderRights.Editor | OrderRights.Admin);
                    host.Step();
                    host.TakeDelta();
                };
            }
            else
            {
                Console.WriteLine($"  --units {options.Units}: у base пока нет обработчика edit.unit (ждёт Б1), мир пустой");
            }
        }
        return Measure(host, title, mode, options.Ticks, spawn);
    }

    private static Result? MeasureHello(ModMode mode, Options options)
    {
        var dir = Path.Combine(DotnetRoot(), "mods", "hello");
        if (Unavailable(dir, mode) is { } reason)
        {
            Console.WriteLine($"[hello 10k / {mode}] пропущено: {reason}");
            return null;
        }
        using var host = new KHost(GameContent.Load([ModPackage.Read(dir)]), new HostOptions
        {
            ModMode = mode,
            Seed = 1,
            Modules = new GameModuleOptions { CallTimeoutMs = 10_000 },
        });
        host.Start();
        host.Join(1);
        // 10 кучек по 1000 юнитов — как TickBenchTests: ходовая, теснота и дельта на 10 тыс. сущностей.
        void Spawn()
        {
            for (var k = 0; k < 10; k++)
                host.Send(1, "hello.spawn", MemoryPackSerializer.Serialize(new SpawnOrder { Count = 1000, X = (k % 5 - 2) * 60, Y = (k / 5 - 0.5) * 80, Spread = 50 }));
            host.Step();
            host.TakeDelta();
        }
        return Measure(host, "hello, 10 003 юнита (ходовая и теснота в моде)", mode, options.Ticks, Spawn);
    }

    /// <summary>Нет ли причины не мерить: host.wasm (или Hello.Host.dll / Base.Host.dll) не собран.</summary>
    private static string? Unavailable(string modDir, ModMode mode)
    {
        if (!Directory.Exists(modDir))
            return $"нет {modDir}";
        return mode switch
        {
            ModMode.Wasm when !File.Exists(Path.Combine(modDir, "host.wasm")) => "host.wasm не собран (dotnet/tools/build-mods.ps1)",
            ModMode.Clr when ClrModule.Find(modDir) is null => "сборка мода (*.Host.dll) не собрана: dotnet build dotnet/Kharos.slnx",
            _ => null,
        };
    }

    // ===== Замер =====

    private sealed record Result(string Title, ModMode Mode, double StartupMs, int Ticks, double[] Step, double Delta, double Encode, long Bytes,
        List<(string Name, double Gather, double Run, double Scatter, double Commands)> Systems, int Errors, int Entities);

    private static Result Measure(KHost host, string title, ModMode mode, int ticks, Action? spawn)
    {
        spawn?.Invoke();
        var view = new FullView(1);
        for (var k = 0; k < Warmup; k++)
        {
            host.Step();
            host.TakeDelta();
        }
        var before = host.Scheduler.Timings().ToDictionary(t => t.Name);
        var step = new double[ticks];
        double delta = 0, encode = 0;
        long bytes = 0;
        for (var k = 0; k < ticks; k++)
        {
            var started = Stopwatch.GetTimestamp();
            host.Step();
            var stepped = Stopwatch.GetTimestamp();
            var taken = host.TakeDelta();
            var built = Stopwatch.GetTimestamp();
            bytes += DeltaCodec.Encode(taken, host.World, host.Layers, view, host.Format).Length;
            var ended = Stopwatch.GetTimestamp();
            step[k] = Stopwatch.GetElapsedTime(started, stepped).TotalMilliseconds;
            delta += Stopwatch.GetElapsedTime(stepped, built).TotalMilliseconds;
            encode += Stopwatch.GetElapsedTime(built, ended).TotalMilliseconds;
        }
        var systems = host.Scheduler.Timings().Select(t =>
        {
            var b = before[t.Name];
            return (t.Name, (t.Gather - b.Gather) / ticks, (t.Run - b.Run) / ticks, (t.Scatter - b.Scatter) / ticks, (t.Commands - b.Commands) / ticks);
        }).ToList();
        return new Result(title, mode, host.Mods.Sum(mod => mod.StartupMs), ticks, step, delta / ticks, encode / ticks, bytes / ticks, systems, host.Errors.Count, host.World.Count);
    }

    private static string F(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    private static void Print(Result r, int top)
    {
        var sorted = (double[])r.Step.Clone();
        Array.Sort(sorted);
        var mean = r.Step.Average();
        Console.WriteLine($"[{r.Title}] режим {r.Mode}; сущностей {r.Entities:N0}; запуск модов {r.StartupMs:F0} мс");
        Console.WriteLine($"  тик хоста (приказы, системы, обзор): среднее {F(mean)} мс, медиана {F(sorted[sorted.Length / 2])}, 95% {F(sorted[sorted.Length * 95 / 100])}, наибольший {F(sorted[^1])}");
        Console.WriteLine($"  дельта: сборка {F(r.Delta)} мс, кодирование {F(r.Encode)} мс, {r.Bytes / 1024.0:F1} КБ на тик; всего с рассылкой {F(mean + r.Delta + r.Encode)} мс");
        if (r.Errors > 0)
            Console.WriteLine($"  ОШИБОК хоста: {r.Errors}");
        var rows = r.Systems.Select(s => (s.Name, s.Gather, s.Run, s.Scatter, s.Commands, Total: s.Gather + s.Run + s.Scatter + s.Commands)).OrderByDescending(s => s.Total).ToList();
        var all = rows.Sum(s => s.Total);
        Console.WriteLine($"  системы: {rows.Count}, сумма {F(all)} мс на тик (в {rows.Count(s => s.Total > 0.0005)} заметных); худшие {Math.Min(top, rows.Count)}:");
        Console.WriteLine($"    {"система",-28} {"сборка",8} {"система",8} {"возврат",8} {"команды",8} {"всего",8}  мс на тик");
        foreach (var s in rows.Take(top))
            Console.WriteLine($"    {s.Name,-28} {F(s.Gather),8} {F(s.Run),8} {F(s.Scatter),8} {F(s.Commands),8} {F(s.Total),8}");
        Console.WriteLine($"    {"остальные",-28} {"",8} {"",8} {"",8} {"",8} {F(rows.Skip(top).Sum(s => s.Total)),8}");
    }

    private static string DotnetRoot()
    {
        if (Environment.GetEnvironmentVariable("KHAROS_DOTNET_ROOT") is { Length: > 0 } root)
            return root;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Kharos.slnx")))
                return dir.FullName;
        throw new DirectoryNotFoundException("не найден Kharos.slnx выше " + AppContext.BaseDirectory);
    }
}
