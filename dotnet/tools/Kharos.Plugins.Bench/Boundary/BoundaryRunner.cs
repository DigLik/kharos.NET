using System.Diagnostics;
using Hello;
using Kharos.Plugins.Game;
using Kharos.Plugins.Tests;

namespace Kharos.Plugins.Bench.Boundary;

/// <summary>
/// Замер-ворота Н2 (docs/dotnet.md, §6, риск Р1): система «ходовая» над 10 и 100 тыс. сущностей — на C# в процессе
/// хоста и в моде hello.wasm через границу (столбцы туда, kh_system, столбцы обратно, разбор команд) — с разбивкой
/// на копирование и счёт. Точка отсчёта — Luau-пилот (docs/content.md): Rust 0,15 / 1,6 мс, Luau столбцами 0,26 / 2,9 мс.
/// </summary>
public static class BoundaryRunner
{
    private const double Dt = 0.05;

    /// <summary>Систем у base (sim-ts) — столько вызовов границы за тик.</summary>
    private const int BaseSystems = 39;

    /// <summary>Бюджет тика, мс.</summary>
    private const double TickBudget = 50;

    /// <summary>Запуск замера; sizes — числа сущностей.</summary>
    public static void Run(int[] sizes)
    {
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine("Граница wasm: система hello.drive (Position, Facing, Drive — 7 столбцов f64)");
        Console.WriteLine("--------------------------------------------------------------------------------");
        if (!HelloModule.Built)
        {
            Console.WriteLine(HelloModule.NotBuilt);
            return;
        }

        var wasm = File.ReadAllBytes(HelloModule.WasmPath);
        var sw = Stopwatch.StartNew();
        using var host = new GameModuleHost(new GameModuleOptions { CallTimeoutMs = 2000, MemoryLimitBytes = 256L << 20 });
        using var module = host.Load("hello", wasm, HelloModule.LoadInfo());
        var compileMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        using var instance = module.CreateInstance();
        var instantiateMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        using (module.CreateInstance())
        {
        }
        var secondMs = sw.Elapsed.TotalMilliseconds;

        Console.WriteLine($"Модуль: {wasm.Length:N0} байт, компиляция Cranelift {compileMs:F0} мс");
        Console.WriteLine($"Экземпляр: {instantiateMs:F1} мс (из них рантайм и kh_load {instance.StartupMs:F1} мс), второй — {secondMs:F1} мс; память {instance.MemoryBytes >> 20} МБ");
        Console.WriteLine($"Импорты ({module.Imports.Count}): {string.Join(", ", module.Imports)}");
        Console.WriteLine();

        var system = instance.SystemId("hello.drive");
        var call = MeasureEmptyCall(instance, system);
        Console.WriteLine($"Вызов границы без сущностей (kh_system, n = 0): {call * 1000:F2} мкс");
        Console.WriteLine();

        var rows = new List<Row>();
        foreach (var n in sizes)
            rows.Add(Measure(instance, system, n));

        Console.WriteLine("| мс на тик | " + string.Join(" | ", rows.Select(r => $"{r.N / 1000} тыс.")) + " |");
        Console.WriteLine("|---|" + string.Join("|", rows.Select(_ => "---")) + "|");
        Line("C# на хосте (JIT), те же столбцы", rows, r => r.Plain);
        Line("wasm через границу, всего", rows, r => r.Total);
        Line("  копирование туда (CopyTo)", rows, r => r.CopyIn);
        Line("  kh_system (счёт в wasm и вызов)", rows, r => r.Invoke);
        Line("  копирование обратно (Write)", rows, r => r.CopyOut);
        Line("  разбор команд (Emit доехавшим)", rows, r => r.Decode);
        Line("wasm без копирования (столбцы живут в моде)", rows, r => r.Resident);
        Line("Rust-пилот (docs/content.md)", rows, r => r.N == 10_000 ? 0.15 : r.N == 100_000 ? 1.6 : double.NaN);
        Line("Luau столбцами, JIT (docs/content.md)", rows, r => r.N == 10_000 ? 0.26 : r.N == 100_000 ? 2.9 : double.NaN);
        Console.WriteLine();
        foreach (var r in rows)
            Console.WriteLine($"{r.N:N0}: wasm/C# = {r.Total / r.Plain:F2}×, копирование {(r.CopyIn + r.CopyOut) / r.Total * 100:F0}% тика системы, " +
                              $"{(r.CopyIn + r.CopyOut) * 1e6 / (r.N * 7.0 * 2):F2} нс на значение f64; команд за тик ~{r.Commands:F0}");

        Console.WriteLine();
        Console.WriteLine($"Бюджет: {BaseSystems} систем base на тик {TickBudget} мс.");
        foreach (var r in rows)
        {
            var worst = BaseSystems * r.Total;
            var fixedCost = BaseSystems * call;
            Console.WriteLine($"  {r.N:N0} сущностей: худший случай — все {BaseSystems} систем как ходовая над всеми — {worst:F1} мс " +
                              $"({worst / TickBudget * 100:F0}% бюджета); постоянная цена {BaseSystems} вызовов — {fixedCost * 1000:F0} мкс.");
        }
        var verdict = rows.FirstOrDefault(r => r.N == 10_000) is { } ten && BaseSystems * ten.Total < TickBudget / 2;
        Console.WriteLine(verdict
            ? "Вывод: base как wasm укладывается в тик с запасом (худший случай на 10 тыс. — меньше половины бюджета)."
            : "Вывод: худший случай на 10 тыс. сущностей не оставляет запаса — нужны пакетирование систем или инструменты движка.");
    }

    private sealed record Row(int N, double Plain, double Total, double CopyIn, double Invoke, double CopyOut, double Decode, double Resident, double Commands);

    private static void Line(string name, List<Row> rows, Func<Row, double> pick) =>
        Console.WriteLine($"| {name} | " + string.Join(" | ", rows.Select(r => double.IsNaN(pick(r)) ? "—" : pick(r).ToString("F3"))) + " |");

    private static double MeasureEmptyCall(GameModuleInstance instance, int system)
    {
        instance.PrepareSystem(system, 0);
        const int calls = 20_000;
        for (var i = 0; i < 1000; i++)
            instance.InvokeSystem(system, 0, new ModCall((ulong)i, Dt, 1));
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < calls; i++)
            instance.InvokeSystem(system, 0, new ModCall((ulong)i, Dt, 1));
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds / calls;
    }

    private static Row Measure(GameModuleInstance instance, int system, int n)
    {
        var ticks = n >= 100_000 ? 100 : 200;
        const int warmup = 20;

        // (а) C# на хосте над тем же блоком столбцов.
        var (entities, plain) = HelloModule.Units(n);
        var arrivals = new Count();
        double plainMs = 0;
        for (var t = 0; t < warmup + ticks; t++)
        {
            var start = Stopwatch.GetTimestamp();
            Wheels.Drive(
                Col(plain, n, HelloModule.X), Col(plain, n, HelloModule.Y), Col(plain, n, HelloModule.Angle),
                Col(plain, n, HelloModule.Speed), Col(plain, n, HelloModule.Turn), Col(plain, n, HelloModule.Tx), Col(plain, n, HelloModule.Ty),
                entities, (ulong)t, Dt, 0.5, ref arrivals);
            if (t >= warmup)
                plainMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        // (б) wasm через границу: копия туда, kh_system, копия обратно, разбор команд.
        var (_, data) = HelloModule.Units(n);
        var layout = instance.Systems[system];
        double copyIn = 0, invoke = 0, copyOut = 0, decode = 0, commands = 0;
        for (var t = 0; t < warmup + ticks; t++)
        {
            var call = new ModCall((ulong)t, Dt, (uint)n + 1);
            var t0 = Stopwatch.GetTimestamp();
            var frame = instance.PrepareSystem(system, n);
            data.AsSpan().CopyTo(frame.Data);
            entities.CopyTo(frame.Entities);
            var t1 = Stopwatch.GetTimestamp();
            var buffer = instance.InvokeSystem(system, n, call);
            var t2 = Stopwatch.GetTimestamp();
            var back = instance.Frame(system, n).Data;
            foreach (var (first, count) in layout.Write)
                back.Slice(first * n, count * n).CopyTo(data.AsSpan(first * n, count * n));
            var t3 = Stopwatch.GetTimestamp();
            var records = ModCommands.Count(buffer);
            var t4 = Stopwatch.GetTimestamp();
            if (t < warmup)
                continue;
            copyIn += Ms(t0, t1);
            invoke += Ms(t1, t2);
            copyOut += Ms(t2, t3);
            decode += Ms(t3, t4);
            commands += records;
        }

        // Сверка: те же числа, что у C# (с допуском на libm).
        var maxDiff = 0.0;
        for (var i = 0; i < data.Length; i++)
            maxDiff = Math.Max(maxDiff, Math.Abs(data[i] - plain[i]));
        if (maxDiff > 1e-6)
            Console.WriteLine($"ВНИМАНИЕ: {n:N0}: wasm и C# разошлись на {maxDiff:E2}");

        // (в) wasm без копирования: столбцы остаются в памяти мода между тиками.
        instance.PrepareSystem(system, n);
        double resident = 0;
        for (var t = 0; t < warmup + ticks; t++)
        {
            var start = Stopwatch.GetTimestamp();
            instance.InvokeSystem(system, n, new ModCall((ulong)t, Dt, (uint)n + 1));
            if (t >= warmup)
                resident += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        return new Row(n, plainMs / ticks, (copyIn + invoke + copyOut + decode) / ticks, copyIn / ticks, invoke / ticks,
            copyOut / ticks, decode / ticks, resident / ticks, commands / ticks);
    }

    private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    private static Span<double> Col(double[] data, int n, int column) => data.AsSpan(column * n, n);

    private struct Count : IArrivals
    {
        public int Total;

        void IArrivals.Arrived(int row, uint entity, double x, double y) => Total++;
    }
}
