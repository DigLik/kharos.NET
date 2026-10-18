using System.Text;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Kharos.Plugins.Bench.Benchmarks;
using Kharos.Plugins.Bench.Security;

namespace Kharos.Plugins.Bench;

/// <summary>
/// Главный класс консольного приложения и точка входа системы бенчмарков и тестирования плагинов WebAssembly.
/// </summary>
public static class Program
{
    /// <summary>
    /// Главная точка входа приложения.
    /// </summary>
    /// <param name="args">Параметры командной строки.</param>
    public static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Console.Error.WriteLine($"Критическая ошибка: {e.ExceptionObject}");
        };

        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return;
        }

        if (args.Contains("--security"))
        {
            SecurityAuditRunner.RunAll();
            return;
        }

        if (args.Contains("--stress"))
        {
            int plugins = 1000;
            int ops = 100000;
            int duration = 3;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--plugins" && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out plugins);
                }

                if (args[i] == "--ops" && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out ops);
                }

                if (args[i] == "--duration" && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out duration);
                }
            }

            ScaleStressRunner.Run(plugins, ops, duration);
            return;
        }

        if (args.Contains("--benchmark"))
        {
            var config = ManualConfig.Create(DefaultConfig.Instance)
                .AddJob(Job.InProcess);
            BenchmarkRunner.Run<MicroBenchmarks>(config);
            return;
        }

        // По умолчанию запускаем аудит безопасности и нагрузочный тест
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine("Среда исполнения плагинов WebAssembly (.NET 11 / Wasmtime / MemoryPack)");
        Console.WriteLine("--------------------------------------------------------------------------------");

        Console.WriteLine("\n[1/2] Запуск аудита безопасности песочницы...");
        SecurityAuditRunner.RunAll();

        Console.WriteLine("\n[2/2] Запуск нагрузочного тестирования пула плагинов...");
        ScaleStressRunner.Run(pluginCount: 1000, targetCallsPerSec: 100_000, durationSeconds: 3);

        Console.WriteLine("\nКоманда для запуска микро-бенчмарков BenchmarkDotNet:");
        Console.WriteLine("dotnet run --project dotnet/tools/Kharos.Plugins.Bench -c Release -- --benchmark");
    }

    /// <summary>
    /// Выводит справочную информацию по аргументам командной строки.
    /// </summary>
    private static void PrintHelp()
    {
        Console.WriteLine("Использование: dotnet run [опции]");
        Console.WriteLine("Опции:");
        Console.WriteLine("  --security                    Запуск аудита безопасности песочницы WebAssembly.");
        Console.WriteLine("  --stress                      Запуск нагрузочного стресс-теста пула плагинов.");
        Console.WriteLine("    --plugins <число>           Количество плагинов (по умолчанию: 1000).");
        Console.WriteLine("    --ops <число>               Целевая частота вызовов в секунду (по умолчанию: 100000).");
        Console.WriteLine("    --duration <секунды>        Длительность теста в секундах (по умолчанию: 3).");
        Console.WriteLine("  --benchmark                   Запуск микро-бенчмарков BenchmarkDotNet.");
        Console.WriteLine("  --help, -h                    Отображение данной справки.");
    }
}
