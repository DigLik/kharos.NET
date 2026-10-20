using System.Collections.Concurrent;
using System.Security.Cryptography;
using Kharos.Plugins.Runtime;
using Wasmtime;

namespace Kharos.Plugins.Game;

/// <summary>
/// Общие на процесс Engine и скомпилированные Module. Компиляция host.wasm (около 10 МБ) стоит секунды, поэтому
/// каждый различный двоичный файл мода компилируется один раз на (конфигурацию движка, SHA-256 байтов), а дальше
/// модуль переиспользуют все хозяева и все экземпляры. Модуль неизменяем и безопасен между Store; состояние
/// (Store, пределы памяти, дедлайны эпох, удары) остаётся у экземпляра и GameModule. Живёт до конца процесса:
/// Engine, сторож эпох и Module никто не освобождает. Движок один на интервал сторожа — единственный параметр
/// конфигурации, зависящий от опций; пределы памяти и таймауты заданы на уровне Store.
/// Дисковый кэш компиляции Wasmtime (между запусками) необязателен: каталог — KHAROS_WASM_CACHE
/// («off» выключает), иначе KHAROS_HOME/wasmtime-cache, иначе LocalApplicationData/Kharos/wasmtime-cache; любая неудача — работа без него.
/// </summary>
internal static class SharedWasm
{
    private sealed record EngineEntry(Engine Engine, WasmEpochWatchdog? Watchdog);

    private static readonly ConcurrentDictionary<int, Lazy<EngineEntry>> Engines = new();
    private static readonly ConcurrentDictionary<(int Interval, string Hash), Lazy<Module>> Modules = new();

    /// <summary>Общий движок для интервала сторожа (0 — без таймера).</summary>
    public static Engine EngineFor(int watchdogIntervalMs) => Entry(watchdogIntervalMs).Engine;

    /// <summary>Скомпилированный модуль: из кэша процесса или новый. Не освобождать.</summary>
    public static Module Compile(int watchdogIntervalMs, string name, ReadOnlySpan<byte> wasm)
    {
        var hash = Convert.ToHexString(SHA256.HashData(wasm));
        var engine = EngineFor(watchdogIntervalMs);
        var bytes = wasm.ToArray();
        var lazy = Modules.GetOrAdd((watchdogIntervalMs, hash), _ => new Lazy<Module>(
            () => Module.FromBytes(engine, name, bytes), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            // Неудавшуюся компиляцию не запоминаем.
            Modules.TryRemove(new KeyValuePair<(int, string), Lazy<Module>>((watchdogIntervalMs, hash), lazy));
            throw;
        }
    }

    private static EngineEntry Entry(int interval) => Engines.GetOrAdd(interval, static i => new Lazy<EngineEntry>(() =>
    {
        Engine engine;
        var cache = CacheConfigPath();
        try
        {
            engine = new Engine(NewConfig(cache));
        }
        catch when (cache is not null)
        {
            engine = new Engine(NewConfig(null));
        }
        return new EngineEntry(engine, new WasmEpochWatchdog(engine, i));
    }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static Config NewConfig(string? cacheConfig)
    {
        var config = new Config()
            .WithEpochInterruption(true)
            .WithOptimizationLevel(OptimizationLevel.Speed);
        if (cacheConfig is not null)
            config.WithCacheConfig(cacheConfig);
        return config;
    }

    /// <summary>Файл настроек дискового кэша Wasmtime; null — кэша нет.</summary>
    private static string? CacheConfigPath()
    {
        try
        {
            var dir = Environment.GetEnvironmentVariable("KHAROS_WASM_CACHE");
            if (string.Equals(dir, "off", StringComparison.OrdinalIgnoreCase))
                return null;
            if (string.IsNullOrWhiteSpace(dir) && Environment.GetEnvironmentVariable("KHAROS_HOME") is { Length: > 0 } home)
                dir = Path.Combine(Path.GetFullPath(home), "wasmtime-cache");
            if (string.IsNullOrWhiteSpace(dir))
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local))
                    return null;
                dir = Path.Combine(local, "Kharos", "wasmtime-cache");
            }
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "config.toml");
            var toml = "[cache]\ndirectory = \"" + Path.Combine(dir, "data").Replace('\\', '/') + "\"\n";
            if (!File.Exists(file) || File.ReadAllText(file) != toml)
            {
                var tmp = file + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(tmp, toml);
                File.Move(tmp, file, overwrite: true);
            }
            return file;
        }
        catch
        {
            return null;
        }
    }
}
