using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using MemoryPack;
using Kharos.Plugins.Abstractions;
using Kharos.Plugins.DependencyInjection;
using Kharos.Plugins.Models;
using Kharos.Plugins.Bench.Samples;

namespace Kharos.Plugins.Bench.Benchmarks;

/// <summary>
/// Набор наносекундных микро-бенчмарков для сравнения накладных расходов выполнения в песочнице Wasmtime,
/// сериализации MemoryPack и прямого выполнения чистого C# кода.
/// </summary>
[MemoryDiagnoser]
public class MicroBenchmarks
{
    private ServiceProvider _serviceProvider = null!;
    private IPluginManager _manager = null!;
    private PluginPayload _payload;
    private byte[] _rawInput = null!;
    private byte[] _rawOutput = null!;

    /// <summary>
    /// Инициализация тестовой среды, регистрация DI и прогрев JIT/Wasmtime.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddWasmPluginSystem(opt =>
        {
            opt.WatchdogIntervalMs = 0; // отключить таймер для микротестов
            opt.DefaultInitialPoolSize = 1;
            opt.DefaultMaxPoolSize = 2;
        });

        _serviceProvider = services.BuildServiceProvider();
        _manager = _serviceProvider.GetRequiredService<IPluginManager>();

        var desc = new PluginDescriptor
        {
            PluginId = "micro-plugin",
            Name = "Micro Benchmark Plugin",
            InitialPoolSize = 1,
            MaxPoolSize = 2,
            ExecutionTimeoutMs = 1000
        };
        _manager.RegisterPluginWat(desc, WasmPluginSamples.ComputeModuleWat);

        _payload = new PluginPayload
        {
            Iterations = 10,
            Multiplier = 1.05,
            Result = 0,
            Timestamp = 0,
            Success = false
        };

        _rawInput = new byte[40];
        BitConverter.TryWriteBytes(_rawInput.AsSpan(0, 4), 10);
        _rawOutput = new byte[40];

        // Прогрев
        _manager.Execute<PluginPayload, PluginPayload>("micro-plugin", in _payload);
    }

    /// <summary>
    /// Освобождение ресурсов менеджера плагинов и DI-контейнера после прогона.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _manager.Dispose();
        _serviceProvider.Dispose();
    }

    /// <summary>
    /// Базовый тест: прямое вычисление математического цикла в C# без песочницы.
    /// </summary>
    /// <returns>Рассчитанное значение.</returns>
    [Benchmark(Baseline = true, Description = "Direct C# Math (Без WASM/Sandbox)")]
    public double Baseline_DirectCSharpMath()
    {
        double res = _payload.Multiplier;
        for (int k = 0; k < _payload.Iterations; k++)
        {
            res = (res * 1.0001) + 0.05;
        }
        return res;
    }

    /// <summary>
    /// Замер чистой сериализации и десериализации MemoryPack в памяти хоста.
    /// </summary>
    /// <returns>Восстановленный экземпляр структуры.</returns>
    [Benchmark(Description = "MemoryPack Serialize + Deserialize (В C# памяти)")]
    public PluginPayload MemoryPack_Roundtrip()
    {
        byte[] bytes = MemoryPackSerializer.Serialize(in _payload);
        return MemoryPackSerializer.Deserialize<PluginPayload>(bytes);
    }

    /// <summary>
    /// Замер исполнения WebAssembly через сырые Span буферы без сериализации (Zero-Copy).
    /// </summary>
    /// <returns>Число записанных байт.</returns>
    [Benchmark(Description = "Wasmtime Zero-Copy Raw Span (ExecuteRaw)")]
    public int Wasmtime_ExecuteRaw()
    {
        return _manager.ExecuteRaw("micro-plugin", _rawInput, _rawOutput);
    }

    /// <summary>
    /// Полный сквозной вызов WASM плагина со строго типизированным DTO и сериализацией MemoryPack.
    /// </summary>
    /// <returns>Результат выполнения вычислений в виде структуры.</returns>
    [Benchmark(Description = "Wasmtime + MemoryPack DTO (Execute<PluginPayload>)")]
    public PluginPayload Wasmtime_MemoryPack_Execute()
    {
        return _manager.Execute<PluginPayload, PluginPayload>("micro-plugin", in _payload);
    }
}
