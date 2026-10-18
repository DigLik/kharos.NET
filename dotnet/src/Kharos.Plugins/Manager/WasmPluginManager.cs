using System.Collections.Concurrent;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasmtime;
using Kharos.Plugins.Abstractions;
using Kharos.Plugins.Exceptions;
using Kharos.Plugins.Runtime;

namespace Kharos.Plugins.Manager;

/// <summary>
/// Промышленная реализация менеджера плагинов WebAssembly.
/// Интегрирует высокопроизводительный компилятор Wasmtime, пул инстансов <see cref="WasmInstancePool"/>,
/// фоновый сторож эпох <see cref="WasmEpochWatchdog"/> и сериализацию MemoryPack для прямого обмена данными.
/// </summary>
public sealed class WasmPluginManager : IPluginManager
{
    private readonly Engine _engine;
    private readonly Linker _linker;
    private readonly WasmEpochWatchdog _watchdog;
    private readonly HostCapabilityRegistry _capabilities;
    private readonly ILogger<WasmPluginManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, WasmInstancePool> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="WasmPluginManager"/> с заданным реестром хост-возможностей, интервалом сторожа и логгерами.
    /// </summary>
    /// <param name="capabilities">Реестр безопасных функций хоста.</param>
    /// <param name="watchdogIntervalMs">Интервал сторожа в миллисекундах (по умолчанию 10 мс).</param>
    /// <param name="logger">Экземпляр <see cref="ILogger{TCategoryName}"/> для менеджера плагинов.</param>
    /// <param name="loggerFactory">Фабрика <see cref="ILoggerFactory"/> для создания дочерних логгеров пулов и сторожа.</param>
    /// <exception cref="ArgumentNullException">Выбрасывается, если <paramref name="capabilities"/> равен <see langword="null"/>.</exception>
    public WasmPluginManager(
        HostCapabilityRegistry capabilities,
        int watchdogIntervalMs = 10,
        ILogger<WasmPluginManager>? logger = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _capabilities = capabilities;
        _logger = logger ?? NullLogger<WasmPluginManager>.Instance;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

        var config = new Config();
        config.WithEpochInterruption(true);
        _engine = new Engine(config);

        _linker = new Linker(_engine);
        _capabilities.ApplyTo(_linker);

        _watchdog = new WasmEpochWatchdog(_engine, watchdogIntervalMs, _loggerFactory.CreateLogger<WasmEpochWatchdog>());
    }

    /// <inheritdoc/>
    public TOut Execute<TIn, TOut>(string pluginId, in TIn input)
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(WasmPluginManager));
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        if (!_plugins.TryGetValue(pluginId, out var pool))
        {
            _logger.LogWarning("Попытка вызова незарегистрированного плагина '{PluginId}'.", pluginId);
            throw new PluginNotFoundException(pluginId);
        }

        var instance = pool.Rent();
        instance.ResetEpochDeadline((ulong)Math.Max(1, pool.Descriptor.ExecutionTimeoutMs / 10));
        bool success = false;

        try
        {
            var memSpan = instance.GetMemorySpan();

            // 1. Сериализация запроса через MemoryPack
            byte[] inputBytes = MemoryPackSerializer.Serialize(in input);
            if (inputBytes.Length > instance.MaxBufferCapacity)
            {
                throw new PluginBufferOverflowException(pluginId, inputBytes.Length, instance.MaxBufferCapacity);
            }

            inputBytes.CopyTo(memSpan.Slice(instance.InBufferOffset, inputBytes.Length));

            // 2. Исполнение точки входа плагина
            int resultLen = instance.Invoke(inputBytes.Length, instance.MaxBufferCapacity);
            if (resultLen < 0)
            {
                throw new PluginExecutionException(pluginId, $"Плагин '{pluginId}' завершился с кодом ошибки {resultLen}.", resultLen);
            }

            // 3. Прямая десериализация ответа из памяти WASM
            var outSlice = memSpan.Slice(instance.OutBufferOffset, resultLen);
            var response = MemoryPackSerializer.Deserialize<TOut>(outSlice);

            success = true;
            return response ?? throw new PluginExecutionException(pluginId, "Десериализация ответа плагина вернула null.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Сбой при исполнении плагина '{PluginId}'. Экземпляр будет отбракован.", pluginId);
            throw;
        }
        finally
        {
            if (success)
            {
                pool.Return(instance);
            }
            else
            {
                pool.Discard(instance);
            }
        }
    }

    /// <inheritdoc/>
    public bool TryExecute<TIn, TOut>(string pluginId, in TIn input, out TOut? output)
    {
        try
        {
            output = Execute<TIn, TOut>(pluginId, in input);
            return true;
        }
        catch
        {
            output = default;
            return false;
        }
    }

    /// <inheritdoc/>
    public int ExecuteRaw(string pluginId, ReadOnlySpan<byte> input, Span<byte> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(WasmPluginManager));
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        if (!_plugins.TryGetValue(pluginId, out var pool))
        {
            _logger.LogWarning("Попытка вызова незарегистрированного плагина '{PluginId}'.", pluginId);
            throw new PluginNotFoundException(pluginId);
        }

        var instance = pool.Rent();
        instance.ResetEpochDeadline((ulong)Math.Max(1, pool.Descriptor.ExecutionTimeoutMs / 10));
        bool success = false;

        try
        {
            if (input.Length > instance.MaxBufferCapacity)
            {
                throw new PluginBufferOverflowException(pluginId, input.Length, instance.MaxBufferCapacity);
            }

            if (output.Length > instance.MaxBufferCapacity)
            {
                throw new PluginBufferOverflowException(pluginId, output.Length, instance.MaxBufferCapacity);
            }

            var memSpan = instance.GetMemorySpan();
            input.CopyTo(memSpan.Slice(instance.InBufferOffset, input.Length));

            int bytesWritten = instance.Invoke(input.Length, output.Length);
            if (bytesWritten < 0)
            {
                throw new PluginExecutionException(pluginId, $"Плагин '{pluginId}' завершился с кодом ошибки {bytesWritten}.", bytesWritten);
            }

            if (bytesWritten > 0)
            {
                memSpan.Slice(instance.OutBufferOffset, bytesWritten).CopyTo(output);
            }

            success = true;
            return bytesWritten;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Сбой при исполнении ExecuteRaw плагина '{PluginId}'. Экземпляр будет отбракован.", pluginId);
            throw;
        }
        finally
        {
            if (success)
            {
                pool.Return(instance);
            }
            else
            {
                pool.Discard(instance);
            }
        }
    }

    /// <inheritdoc/>
    public void RegisterPlugin(PluginDescriptor descriptor, Stream wasmStream)
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(WasmPluginManager));
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(wasmStream);

        descriptor.Validate();

        if (!wasmStream.CanRead)
        {
            throw new ArgumentException("Входной поток WebAssembly модуля недоступен для чтения.", nameof(wasmStream));
        }

        using var ms = new MemoryStream();
        wasmStream.CopyTo(ms);
        byte[] bytes = ms.ToArray();

        var module = Module.FromBytes(_engine, descriptor.PluginId, bytes);
        var pool = new WasmInstancePool(_engine, module, _linker, descriptor, _loggerFactory.CreateLogger<WasmInstancePool>());

        _plugins.AddOrUpdate(descriptor.PluginId, pool, (_, oldPool) =>
        {
            oldPool.Dispose();
            return pool;
        });

        _logger.LogInformation(
            "Плагин '{PluginId}' v{Version} зарегистрирован. Начальный пул: {InitialPool}, Макс: {MaxPool}, Таймаут: {Timeout} мс.",
            descriptor.PluginId, descriptor.Version, descriptor.InitialPoolSize, descriptor.MaxPoolSize, descriptor.ExecutionTimeoutMs);
    }

    /// <inheritdoc/>
    public void RegisterPluginWat(PluginDescriptor descriptor, string watText)
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(WasmPluginManager));
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(watText);

        descriptor.Validate();

        var module = Module.FromText(_engine, descriptor.PluginId, watText);
        var pool = new WasmInstancePool(_engine, module, _linker, descriptor, _loggerFactory.CreateLogger<WasmInstancePool>());

        _plugins.AddOrUpdate(descriptor.PluginId, pool, (_, oldPool) =>
        {
            oldPool.Dispose();
            return pool;
        });

        _logger.LogInformation(
            "Плагин '{PluginId}' v{Version} (WAT) зарегистрирован. Начальный пул: {InitialPool}, Макс: {MaxPool}, Таймаут: {Timeout} мс.",
            descriptor.PluginId, descriptor.Version, descriptor.InitialPoolSize, descriptor.MaxPoolSize, descriptor.ExecutionTimeoutMs);
    }

    /// <inheritdoc/>
    public bool UnregisterPlugin(string pluginId)
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(WasmPluginManager));
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        if (_plugins.TryRemove(pluginId, out var pool))
        {
            pool.Dispose();
            _logger.LogInformation("Плагин '{PluginId}' успешно выгружен из системы.", pluginId);
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public bool ContainsPlugin(string pluginId)
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(WasmPluginManager));
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        return _plugins.ContainsKey(pluginId);
    }

    /// <inheritdoc/>
    public PluginDescriptor? TryGetDescriptor(string pluginId)
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(WasmPluginManager));
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        return _plugins.TryGetValue(pluginId, out var pool) ? pool.Descriptor : null;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<PluginDescriptor> GetPlugins()
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(WasmPluginManager));
        return _plugins.Values.Select(p => p.Descriptor).ToList();
    }

    /// <summary>
    /// Освобождает все ресурсы менеджера плагинов, включая сторож, пулы экземпляров, линковщик и движок WASM.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watchdog.Dispose();

        foreach (var pool in _plugins.Values)
        {
            try
            {
                pool.Dispose();
            }
            catch
            {
                // Подавление ошибок при освобождении пулов
            }
        }

        _plugins.Clear();
        _linker.Dispose();
        _engine.Dispose();

        _logger.LogInformation("Менеджер плагинов WasmPluginManager успешно освобожден.");
    }
}
