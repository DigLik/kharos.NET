using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Kharos.Plugins.Abstractions;
using Kharos.Plugins.DependencyInjection;
using Kharos.Plugins.Models;
using Kharos.Plugins.Bench.Samples;

namespace Kharos.Plugins.Bench.Benchmarks;

/// <summary>
/// Средство нагрузочного и стрессового тестирования системы плагинов при масштабировании на тысячи экземпляров.
/// </summary>
public static partial class ScaleStressRunner
{
    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint uMilliseconds);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint uMilliseconds);

    /// <summary>
    /// Запускает полный цикл стресс-тестирования системы плагинов.
    /// </summary>
    /// <param name="pluginCount">Количество уникальных плагинов для регистрации.</param>
    /// <param name="targetCallsPerSec">Целевая фиксированная частота вызовов в секунду.</param>
    /// <param name="durationSeconds">Длительность каждого тестового прогона в секундах.</param>
    public static void Run(int pluginCount = 1000, int targetCallsPerSec = 100_000, int durationSeconds = 3)
    {
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine($"Нагрузочное тестирование: {pluginCount:N0} плагинов, целевая частота {targetCallsPerSec:N0} вызовов/с");
        Console.WriteLine("Конфигурация: WebAssembly (Wasmtime), MemoryPack, DI");
        Console.WriteLine("--------------------------------------------------------------------------------");

        // 1. Создание DI контейнера и регистрация плагин-системы
        var services = new ServiceCollection();
        services.AddWasmPluginSystem(opt =>
        {
            opt.WatchdogIntervalMs = 10;
            opt.DefaultInitialPoolSize = 1;
            opt.DefaultMaxPoolSize = 4;
            opt.DefaultExecutionTimeoutMs = 100;
        });

        using var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IPluginManager>();

        // 2. Регистрация уникальных плагинов
        Console.WriteLine($"\n[1/3] Регистрация {pluginCount:N0} экземпляров плагинов в системе...");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long ramBefore = Process.GetCurrentProcess().WorkingSet64;

        var swInit = Stopwatch.StartNew();
        var pluginIds = new string[pluginCount];

        for (int i = 0; i < pluginCount; i++)
        {
            string id = $"plugin-{i:D4}";
            pluginIds[i] = id;
            var desc = new PluginDescriptor
            {
                PluginId = id,
                Name = $"Computational Service #{i}",
                Version = "1.0.0",
                InitialPoolSize = 1,
                MaxPoolSize = 4,
                ExecutionTimeoutMs = 100
            };
            manager.RegisterPluginWat(desc, WasmPluginSamples.ComputeModuleWat);
        }
        swInit.Stop();

        long ramAfter = Process.GetCurrentProcess().WorkingSet64;
        long ramDeltaMb = (ramAfter - ramBefore) / (1024 * 1024);

        Console.WriteLine($"  Зарегистрировано {pluginCount:N0} плагинов за {swInit.ElapsedMilliseconds} мс.");
        Console.WriteLine($"  Выделено памяти под экземпляры: ~{ramDeltaMb} МБ ({ramDeltaMb * 1024.0 / pluginCount:F1} КБ на плагин).");

        // 3. Прогрев типизированных вызовов
        Console.WriteLine("\n[2/3] Прогрев JIT-компилятора и сериализатора MemoryPack...");
        var warmupPayload = new PluginPayload
        {
            Iterations = 10,
            Multiplier = 1.5,
            Result = 0,
            Timestamp = 0,
            Success = false
        };
        var warmupRes = manager.Execute<PluginPayload, PluginPayload>(pluginIds[0], in warmupPayload);
        Console.WriteLine($"  Прогрев завершен (Итераций: {warmupRes.Iterations}, Результат: {warmupRes.Result:F4}, Статус: {warmupRes.Success}).");

        // 4. Серия тестов с различной вычислительной нагрузкой
        Console.WriteLine("\n[3/3] Запуск профилей нагрузки...");

        var workloads = new (string Name, int Iterations)[]
        {
            ("Профиль 1: Низкая вычислительная сложность (10 итераций)", 10),
            ("Профиль 2: Средняя вычислительная сложность (100 итераций)", 100),
            ("Профиль 3: Высокая вычислительная сложность (1 000 итераций)", 1000)
        };

        foreach (var (wName, iterations) in workloads)
        {
            Console.WriteLine($"\n--- {wName} ---");

            // ТЕСТ А: Целевая фиксированная нагрузка с MemoryPack DTO
            Console.WriteLine($"  [A] Удержание частоты {targetCallsPerSec:N0} вызовов/с (MemoryPack DTO)...");
            RunPacedDtoTest(manager, pluginIds, targetCallsPerSec, iterations, durationSeconds);

            // ТЕСТ Б: Целевая фиксированная нагрузка на чистых Zero-Copy Span
            Console.WriteLine($"  [B] Удержание частоты {targetCallsPerSec:N0} вызовов/с (Span буфер без сериализации)...");
            RunPacedRawTest(manager, pluginIds, targetCallsPerSec, durationSeconds);

            // ТЕСТ В: Пиковый предел пропускной способности (Saturation Max Throughput)
            Console.WriteLine($"  [C] Измерение максимальной пропускной способности (без ограничений)...");
            RunSaturationDtoTest(manager, pluginIds, iterations, durationSeconds: 2);
        }
    }

    /// <summary>
    /// Тест удержания стабильной целевой частоты вызовов через MemoryPack DTO.
    /// Использует квантованный микро-батчинг и системный таймер ОС.
    /// </summary>
    private static void RunPacedDtoTest(
        IPluginManager manager,
        string[] pluginIds,
        int targetCallsPerSec,
        int iterations,
        int durationSeconds)
    {
        bool isWindows = OperatingSystem.IsWindows();
        if (isWindows)
        {
            TimeBeginPeriod(1);
        }

        try
        {
            int pluginCount = pluginIds.Length;
            const int intervalMs = 10;
            int batchSize = (targetCallsPerSec * intervalMs) / 1000;
            int totalIntervals = (durationSeconds * 1000) / intervalMs;

            var payload = new PluginPayload
            {
                Iterations = iterations,
                Multiplier = 1.05,
                Result = 0,
                Timestamp = 0,
                Success = false
            };

            long totalCalls = 0;
            var proc = Process.GetCurrentProcess();
            TimeSpan cpuBefore = proc.TotalProcessorTime;
            var wallSw = Stopwatch.StartNew();

            int pluginIndex = 0;
            for (int interval = 0; interval < totalIntervals; interval++)
            {
                int currentBatchStart = pluginIndex;
                Parallel.For(0, batchSize, i =>
                {
                    int idx = (currentBatchStart + i) % pluginCount;
                    manager.Execute<PluginPayload, PluginPayload>(pluginIds[idx], in payload);
                });

                pluginIndex = (pluginIndex + batchSize) % pluginCount;
                totalCalls += batchSize;

                long targetElapsedMs = (interval + 1) * (long)intervalMs;
                long currentElapsedMs = wallSw.ElapsedMilliseconds;
                int sleepMs = (int)(targetElapsedMs - currentElapsedMs);

                if (sleepMs > 0)
                {
                    Thread.Sleep(sleepMs);
                }
            }

            wallSw.Stop();
            TimeSpan cpuAfter = proc.TotalProcessorTime;
            double wallSec = wallSw.Elapsed.TotalSeconds;
            double actualCallsPerSec = totalCalls / wallSec;

            double totalCpuTimeSec = (cpuAfter - cpuBefore).TotalSeconds;
            double cpuPercent = (totalCpuTimeSec / (wallSec * Environment.ProcessorCount)) * 100.0;

            Console.WriteLine($"    [Результат MemoryPack DTO]:");
            Console.WriteLine($"      Выполнено вызовов: {totalCalls:N0} за {wallSec:F2} с");
            Console.WriteLine($"      Фактическая частота: {actualCallsPerSec:N0} вызовов/с (Цель: {targetCallsPerSec:N0})");
            Console.WriteLine($"      Соответствие норме: {(actualCallsPerSec / targetCallsPerSec) * 100.0:F1}%");
            Console.WriteLine($"      Утилизация CPU: {cpuPercent:F2}% (эквивалентно ~{(cpuPercent / 100.0) * Environment.ProcessorCount:F2} из {Environment.ProcessorCount} ядер)");
        }
        finally
        {
            if (isWindows)
            {
                TimeEndPeriod(1);
            }
        }
    }

    /// <summary>
    /// Тест удержания фиксированной частоты вызовов на прямых буферах Span.
    /// </summary>
    private static void RunPacedRawTest(
        IPluginManager manager,
        string[] pluginIds,
        int targetCallsPerSec,
        int durationSeconds)
    {
        bool isWindows = OperatingSystem.IsWindows();
        if (isWindows)
        {
            TimeBeginPeriod(1);
        }

        try
        {
            int pluginCount = pluginIds.Length;
            const int intervalMs = 10;
            int batchSize = (targetCallsPerSec * intervalMs) / 1000;
            int totalIntervals = (durationSeconds * 1000) / intervalMs;

            byte[] localIn = new byte[40];
            BitConverter.TryWriteBytes(localIn.AsSpan(0, 4), 10);

            long totalCalls = 0;
            var proc = Process.GetCurrentProcess();
            TimeSpan cpuBefore = proc.TotalProcessorTime;
            var wallSw = Stopwatch.StartNew();

            int pluginIndex = 0;
            for (int interval = 0; interval < totalIntervals; interval++)
            {
                int currentBatchStart = pluginIndex;
                Parallel.For(0, batchSize, () => new byte[40], (i, state, localOut) =>
                {
                    int idx = (currentBatchStart + i) % pluginCount;
                    manager.ExecuteRaw(pluginIds[idx], localIn, localOut);
                    return localOut;
                }, _ => { });

                pluginIndex = (pluginIndex + batchSize) % pluginCount;
                totalCalls += batchSize;

                long targetElapsedMs = (interval + 1) * (long)intervalMs;
                long currentElapsedMs = wallSw.ElapsedMilliseconds;
                int sleepMs = (int)(targetElapsedMs - currentElapsedMs);

                if (sleepMs > 0)
                {
                    Thread.Sleep(sleepMs);
                }
            }

            wallSw.Stop();
            TimeSpan cpuAfter = proc.TotalProcessorTime;
            double wallSec = wallSw.Elapsed.TotalSeconds;
            double actualCallsPerSec = totalCalls / wallSec;

            double totalCpuTimeSec = (cpuAfter - cpuBefore).TotalSeconds;
            double cpuPercent = (totalCpuTimeSec / (wallSec * Environment.ProcessorCount)) * 100.0;

            Console.WriteLine($"    [Результат Zero-Copy Span]:");
            Console.WriteLine($"      Выполнено вызовов: {totalCalls:N0} за {wallSec:F2} с");
            Console.WriteLine($"      Фактическая частота: {actualCallsPerSec:N0} вызовов/с (Цель: {targetCallsPerSec:N0})");
            Console.WriteLine($"      Соответствие норме: {(actualCallsPerSec / targetCallsPerSec) * 100.0:F1}%");
            Console.WriteLine($"      Утилизация CPU: {cpuPercent:F2}% (эквивалентно ~{(cpuPercent / 100.0) * Environment.ProcessorCount:F2} из {Environment.ProcessorCount} ядер)");
        }
        finally
        {
            if (isWindows)
            {
                TimeEndPeriod(1);
            }
        }
    }

    /// <summary>
    /// Замер максимально достижимой пропускной способности (Saturation Throughput) без искусственных задержек.
    /// </summary>
    private static void RunSaturationDtoTest(
        IPluginManager manager,
        string[] pluginIds,
        int iterations,
        int durationSeconds)
    {
        int threadCount = Math.Max(1, Environment.ProcessorCount);
        int pluginCount = pluginIds.Length;
        int chunkSize = pluginCount / threadCount;
        long totalCalls = 0;

        var sw = Stopwatch.StartNew();
        long targetEndMs = durationSeconds * 1000L;

        Parallel.For(0, threadCount, threadIdx =>
        {
            int startIdx = threadIdx * chunkSize;
            int endIdx = (threadIdx == threadCount - 1) ? pluginCount : startIdx + chunkSize;
            int currentIdx = startIdx;

            var payload = new PluginPayload
            {
                Iterations = iterations,
                Multiplier = 1.05,
                Result = 0,
                Timestamp = 0,
                Success = false
            };

            long calls = 0;
            while (sw.ElapsedMilliseconds < targetEndMs)
            {
                string pluginId = pluginIds[currentIdx];
                _ = manager.Execute<PluginPayload, PluginPayload>(pluginId, in payload);

                calls++;
                currentIdx++;
                if (currentIdx >= endIdx)
                {
                    currentIdx = startIdx;
                }
            }

            Interlocked.Add(ref totalCalls, calls);
        });

        sw.Stop();
        double elapsedSec = sw.Elapsed.TotalSeconds;
        double maxCallsPerSec = totalCalls / elapsedSec;
        double avgTimePerCallNs = (elapsedSec * 1_000_000_000.0) / totalCalls;

        Console.WriteLine($"    [Предельная пропускная способность]:");
        Console.WriteLine($"      Частота вызовов: {maxCallsPerSec:N0} вызовов/с");
        Console.WriteLine($"      Среднее время вызова: {avgTimePerCallNs:F1} нс ({(avgTimePerCallNs / 1000.0):F2} мкс)");
    }
}
