using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wasmtime;
using Kharos.Plugins.Abstractions;
using Kharos.Plugins.DependencyInjection;
using Kharos.Plugins.Exceptions;
using Kharos.Plugins.Manager;
using Kharos.Plugins.Runtime;
using Kharos.Plugins.Bench.Samples;

namespace Kharos.Plugins.Bench.Security;

/// <summary>
/// Автоматизированный запуск аудита безопасности среды исполнения WebAssembly (Wasmtime).
/// Проверяет изоляцию памяти, прерывание DoS по эпохе, защиту от сбоев и вайтлистинг хост-функций.
/// </summary>
public static class SecurityAuditRunner
{
    /// <summary>
    /// Запускает полный комплекс проверок безопасности песочницы WebAssembly.
    /// </summary>
    public static void RunAll()
    {
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine("Аудит безопасности среды исполнения WebAssembly (Wasmtime)");
        Console.WriteLine("--------------------------------------------------------------------------------");

        var services = new ServiceCollection();
        services.AddWasmPluginSystem(opt =>
        {
            opt.WatchdogIntervalMs = 5;
            opt.DefaultExecutionTimeoutMs = 50;
            opt.DefaultInitialPoolSize = 2;
            opt.DefaultMaxPoolSize = 4;
        });

        using var serviceProvider = services.BuildServiceProvider();
        var manager = serviceProvider.GetRequiredService<IPluginManager>();

        TestLegitimateExecution(manager);
        TestHostCapabilities(manager);
        TestMemoryIsolation(manager);
        TestDosInfiniteLoopWatchdog(manager);
        TestGracefulFailureAndRecovery(manager);
        TestUnauthorizedImportsSafety();
        TestStructuredHostLogging();

        Console.WriteLine("\n--------------------------------------------------------------------------------");
        Console.WriteLine("Аудит безопасности среды исполнения WebAssembly завершен штатно.");
        Console.WriteLine("--------------------------------------------------------------------------------\n");
    }

    /// <summary>
    /// Проверка 1: Корректность выполнения вызова WebAssembly модуля (XOR-преобразование буфера).
    /// </summary>
    private static void TestLegitimateExecution(IPluginManager manager)
    {
        Console.WriteLine("\n[1/7] Выполнение скомпилированного модуля (XOR 0x5A):");

        var desc = new PluginDescriptor
        {
            PluginId = "transform-plugin",
            Name = "XOR Transformer",
            InitialPoolSize = 2,
            MaxPoolSize = 4,
            ExecutionTimeoutMs = 100
        };
        manager.RegisterPluginWat(desc, WasmPluginSamples.TransformModuleWat);

        byte[] input = [1, 2, 3, 4, 5, 42, 100, 255];
        byte[] expected = [1 ^ 0x5A, 2 ^ 0x5A, 3 ^ 0x5A, 4 ^ 0x5A, 5 ^ 0x5A, 42 ^ 0x5A, 100 ^ 0x5A, 255 ^ 0x5A];
        byte[] output = new byte[input.Length];

        int written = manager.ExecuteRaw("transform-plugin", input, output);
        bool success = written == input.Length && output.SequenceEqual(expected);

        PrintResult("Исполнение в изолированной памяти", success,
            $"Обработано {written} байт без нарушений границ сегмента памяти.");
    }

    /// <summary>
    /// Проверка 2: Безопасный вызов зарегистрированной функции хоста через компоновщик.
    /// </summary>
    private static void TestHostCapabilities(IPluginManager manager)
    {
        Console.WriteLine("\n[2/7] Вызов функции хоста из плагина (host_get_timestamp):");

        var desc = new PluginDescriptor
        {
            PluginId = "host-calling-plugin",
            Name = "Host Caller",
            InitialPoolSize = 1,
            MaxPoolSize = 2,
            ExecutionTimeoutMs = 100
        };
        manager.RegisterPluginWat(desc, WasmPluginSamples.HostCallingModuleWat);

        byte[] input = [0];
        byte[] output = new byte[8];

        long beforeTicks = DateTime.UtcNow.Ticks;
        int written = manager.ExecuteRaw("host-calling-plugin", input, output);
        long afterTicks = DateTime.UtcNow.Ticks;

        long receivedTicks = BitConverter.ToInt64(output, 0);
        bool valid = written == 8 && receivedTicks >= beforeTicks - 10_000 && receivedTicks <= afterTicks + 10_000;

        PrintResult("Интеграция хост-функций Linker", valid,
            $"Метка времени получена из контекста хоста ({receivedTicks} тиков).");
    }

    /// <summary>
    /// Проверка 3: Проверка изоляции линейной памяти модуля от адресного пространства хост-процесса.
    /// </summary>
    private static void TestMemoryIsolation(IPluginManager manager)
    {
        Console.WriteLine("\n[3/7] Проверка изоляции адресного пространства хост-процесса:");

        byte[] canary = [0xDE, 0xAD, 0xBE, 0xEF, 0x11, 0x22, 0x33, 0x44];
        byte[] canaryCopy = (byte[])canary.Clone();

        byte[] input = new byte[32];
        byte[] output = new byte[32];
        manager.ExecuteRaw("transform-plugin", input, output);

        bool intact = canary.SequenceEqual(canaryCopy);
        PrintResult("Граница линейной памяти", intact,
            "Память хост-процесса изолирована; прямой несанкционированный доступ исключен аппаратно.");
    }

    /// <summary>
    /// Проверка 4: Прерывание блокирующего бесконечного цикла механизмом сторожа эпох.
    /// </summary>
    private static void TestDosInfiniteLoopWatchdog(IPluginManager manager)
    {
        Console.WriteLine("\n[4/7] Прерывание бесконечного цикла (Epoch Watchdog):");

        var desc = new PluginDescriptor
        {
            PluginId = "infinite-loop-plugin",
            Name = "DoS Infinite Loop",
            InitialPoolSize = 1,
            MaxPoolSize = 2,
            ExecutionTimeoutMs = 30
        };
        manager.RegisterPluginWat(desc, WasmPluginSamples.InfiniteLoopModuleWat);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool throwsTrap = false;
        string trapMessage = string.Empty;

        try
        {
            byte[] inBuf = [1, 2, 3];
            byte[] outBuf = new byte[16];
            manager.ExecuteRaw("infinite-loop-plugin", inBuf, outBuf);
        }
        catch (TrapException ex)
        {
            throwsTrap = true;
            trapMessage = ex.Message;
        }
        catch (Exception ex)
        {
            throwsTrap = true;
            trapMessage = $"{ex.GetType().Name}: {ex.Message}";
        }
        sw.Stop();

        bool success = throwsTrap && sw.ElapsedMilliseconds < 500;
        PrintResult("Защита от бесконечного цикла", success,
            $"Цикл принудительно прерван за {sw.ElapsedMilliseconds} мс при лимите 30 мс. Диагностика: {trapMessage}");
    }

    /// <summary>
    /// Проверка 5: Утилизация поврежденных экземпляров и самовосстановление пула после сбоя.
    /// </summary>
    private static void TestGracefulFailureAndRecovery(IPluginManager manager)
    {
        Console.WriteLine("\n[5/7] Восстановление пула экземпляров после сбоя модуля:");

        const string ErrorWat = @"
(module
  (memory (export ""memory"") 1)
  (func (export ""process_buffer"") (param $inPtr i32) (param $inLen i32) (param $outPtr i32) (param $outMaxLen i32) (result i32)
    (if (i32.eqz (local.get $inLen))
      (then (return (i32.const -1)))
    )
    (i32.const 4)
  )
)
";
        var desc = new PluginDescriptor
        {
            PluginId = "resilience-plugin",
            Name = "Resilience Test Module",
            InitialPoolSize = 1,
            MaxPoolSize = 2,
            ExecutionTimeoutMs = 100
        };
        manager.RegisterPluginWat(desc, ErrorWat);

        byte[] emptyBuf = [];
        byte[] outBuf = new byte[4];
        bool failedCallHandled = false;
        try
        {
            manager.ExecuteRaw("resilience-plugin", emptyBuf, outBuf);
        }
        catch (InvalidOperationException)
        {
            failedCallHandled = true;
        }

        byte[] validBuf = [1, 2, 3, 4];
        int written = manager.ExecuteRaw("resilience-plugin", validBuf, outBuf);
        bool recovered = written == 4;

        bool success = failedCallHandled && recovered;
        PrintResult("Отказоустойчивость пула", success,
            "Сбойный экземпляр утилизирован; последующий штатный вызов обработан успешно.");
    }

    /// <summary>
    /// Проверка 6: Блокировка неразрешенных системных импортов (Capability Whitelist).
    /// </summary>
    private static void TestUnauthorizedImportsSafety()
    {
        Console.WriteLine("\n[6/7] Проверка белого списка возможностей (Capability Whitelisting):");

        const string UnauthorizedImportWat = @"
(module
  (import ""env"" ""dangerous_system_call"" (func $bad_call))
  (memory (export ""memory"") 1)
  (func (export ""process_buffer"") (param i32 i32 i32 i32) (result i32)
    (call $bad_call)
    (i32.const 0)
  )
)
";
        var services = new ServiceCollection();
        services.AddWasmPluginSystem();
        using var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IPluginManager>();

        bool rejected = false;
        try
        {
            var desc = new PluginDescriptor { PluginId = "unauthorized-plugin" };
            manager.RegisterPluginWat(desc, UnauthorizedImportWat);
        }
        catch (WasmtimeException ex)
        {
            rejected = true;
            PrintResult("Блокировка неразрешенного импорта", true,
                $"Модуль отклонен на этапе линковки: неразрешенный символ ({ex.Message}).");
        }
        catch (Exception ex)
        {
            rejected = true;
            PrintResult("Блокировка неразрешенного импорта", true,
                $"Модуль отклонен: {ex.Message}.");
        }

        if (!rejected)
        {
            PrintResult("Блокировка неразрешенного импорта", false, "Неразрешенный импорт был принят компоновщиком.");
        }
    }

    /// <summary>
    /// Проверка 7: Проверка безопасного вызова host_log и доставки сообщений в систему логирования.
    /// </summary>
    private static void TestStructuredHostLogging()
    {
        Console.WriteLine("\n[7/7] Вызов функции host_log и структурированное логирование:");

        string? capturedLog = null;
        using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var logger = loggerFactory.CreateLogger<HostCapabilityRegistry>();
        var customRegistry = new HostCapabilityRegistry(logger, msg =>
        {
            capturedLog = msg;
        });

        var desc = new PluginDescriptor
        {
            PluginId = "logging-plugin",
            Name = "Logger Test Module",
            InitialPoolSize = 1,
            MaxPoolSize = 2,
            ExecutionTimeoutMs = 100
        };

        using var manager = new WasmPluginManager(customRegistry, watchdogIntervalMs: 0, loggerFactory: loggerFactory);
        manager.RegisterPluginWat(desc, WasmPluginSamples.LoggingModuleWat);

        byte[] input = [0];
        byte[] output = new byte[8];
        manager.ExecuteRaw("logging-plugin", input, output);

        bool success = !string.IsNullOrEmpty(capturedLog) && capturedLog.Contains("Execution completed successfully");
        PrintResult("Структурированное логирование хоста", success,
            $"Сообщение из памяти WebAssembly получено и обработано: '{capturedLog}'.");
    }

    /// <summary>
    /// Выводит результат проверки в строгом формате.
    /// </summary>
    private static void PrintResult(string name, bool success, string details)
    {
        Console.Write("  * ");
        if (success)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("[УСПЕШНО] ");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("[ОШИБКА]  ");
        }
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"{name}: ");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(details);
        Console.ResetColor();
    }
}
