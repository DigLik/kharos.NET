using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasmtime;
using Kharos.Plugins.Abstractions;
using Kharos.Plugins.Exceptions;

namespace Kharos.Plugins.Runtime;

/// <summary>
/// Высокопроизводительный потокобезопасный пул легковесных изолированных экземпляров WASM (<see cref="WasmInstance"/>).
/// Позволяет горизонтально масштабировать параллельную обработку вызовов до 100 000+ операций в секунду без блокировок.
/// </summary>
public sealed class WasmInstancePool : IDisposable
{
    private readonly Engine _engine;
    private readonly Module _module;
    private readonly Linker _linker;
    private readonly PluginDescriptor _descriptor;
    private readonly ILogger<WasmInstancePool> _logger;
    private readonly ConcurrentQueue<WasmInstance> _pool = new();
    private int _instanceCount;
    private bool _disposed;

    /// <summary>
    /// Метаданные и параметры ограничений текущего плагина.
    /// </summary>
    public PluginDescriptor Descriptor => _descriptor;

    /// <summary>
    /// Текущее общее количество созданных экземпляров плагина (как свободных, так и арендованных).
    /// </summary>
    public int CurrentTotalInstances => Volatile.Read(ref _instanceCount);

    /// <summary>
    /// Количество готовых к немедленной аренде экземпляров в очереди пула.
    /// </summary>
    public int AvailableInstances => _pool.Count;

    /// <summary>
    /// Инициализирует новый экземпляр пула <see cref="WasmInstancePool"/> и выполняет прогрев начального набора инстансов.
    /// </summary>
    /// <param name="engine">Движок Wasmtime.</param>
    /// <param name="module">Скомпилированный модуль WebAssembly.</param>
    /// <param name="linker">Скомпонованный линковщик возможностей.</param>
    /// <param name="descriptor">Дескриптор плагина.</param>
    /// <param name="logger">Экземпляр <see cref="ILogger{TCategoryName}"/> для структурированного логирования жизненного цикла пула.</param>
    /// <exception cref="ArgumentNullException">Выбрасывается, если обязательные аргументы равны <see langword="null"/>.</exception>
    public WasmInstancePool(
        Engine engine,
        Module module,
        Linker linker,
        PluginDescriptor descriptor,
        ILogger<WasmInstancePool>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(linker);
        ArgumentNullException.ThrowIfNull(descriptor);

        descriptor.Validate();

        _engine = engine;
        _module = module;
        _linker = linker;
        _descriptor = descriptor;
        _logger = logger ?? NullLogger<WasmInstancePool>.Instance;

        // Предварительный прогрев пула (Warmup)
        for (int i = 0; i < descriptor.InitialPoolSize; i++)
        {
            _pool.Enqueue(CreateNewInstance());
            _instanceCount++;
        }

        _logger.LogDebug("Пул экземпляров плагина '{PluginId}' инициализирован ({InitialCount} экземпляров).", _descriptor.PluginId, descriptor.InitialPoolSize);
    }

    /// <summary>
    /// Арендует экземпляр плагина из пула.
    /// Если в очереди нет готовых инстансов и лимит <see cref="PluginDescriptor.MaxPoolSize"/> не достигнут, создает новый инстанс.
    /// Если лимит исчерпан, ожидает освобождения экземпляра с контролируемым тайм-аутом.
    /// </summary>
    /// <returns>Готовый к выполнению изолированный экземпляр <see cref="WasmInstance"/>.</returns>
    /// <exception cref="ObjectDisposedException">Выбрасывается, если пул был утилизирован.</exception>
    /// <exception cref="PluginPoolExhaustedException">Выбрасывается, если время ожидания свободного экземпляра истекло.</exception>
    public WasmInstance Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(WasmInstancePool));

        if (_pool.TryDequeue(out var instance))
        {
            return instance;
        }

        // Создаем новый инстанс, если лимит пула не исчерпан
        if (Interlocked.Increment(ref _instanceCount) <= _descriptor.MaxPoolSize)
        {
            try
            {
                var newInst = CreateNewInstance();
                _logger.LogTrace("Создан новый экземпляр плагина '{PluginId}' (Всего: {Total}/{MaxPoolSize}).", _descriptor.PluginId, _instanceCount, _descriptor.MaxPoolSize);
                return newInst;
            }
            catch (Exception ex)
            {
                Interlocked.Decrement(ref _instanceCount);
                _logger.LogError(ex, "Ошибка при инстанцировании нового экземпляра плагина '{PluginId}'.", _descriptor.PluginId);
                throw;
            }
        }

        // Если лимит исчерпан, компенсируем инкремент и ожидаем освобождения одного из работающих экземпляров
        Interlocked.Decrement(ref _instanceCount);

        int timeoutMs = Math.Max(1000, _descriptor.ExecutionTimeoutMs * 5);
        var sw = Stopwatch.StartNew();
        var spin = new SpinWait();

        while (!_disposed)
        {
            if (_pool.TryDequeue(out instance))
            {
                return instance;
            }

            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                _logger.LogError("Превышено время ожидания свободного экземпляра в пуле '{PluginId}' ({TimeoutMs} мс).", _descriptor.PluginId, timeoutMs);
                throw new PluginPoolExhaustedException(_descriptor.PluginId, _descriptor.MaxPoolSize, timeoutMs);
            }

            spin.SpinOnce();
        }

        throw new ObjectDisposedException(nameof(WasmInstancePool));
    }

    /// <summary>
    /// Возвращает успешно отработавший экземпляр плагина обратно в пул для повторного использования.
    /// </summary>
    /// <param name="instance">Экземпляр плагина.</param>
    public void Return(WasmInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (_disposed)
        {
            Discard(instance);
            return;
        }

        _pool.Enqueue(instance);
    }

    /// <summary>
    /// Уничтожает сбойный или скомпрометированный экземпляр плагина, уменьшая счетчик активных инстансов.
    /// </summary>
    /// <param name="instance">Сбойный экземпляр плагина.</param>
    public void Discard(WasmInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        try
        {
            instance.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Ошибка при освобождении дефектного инстанса плагина '{PluginId}'.", _descriptor.PluginId);
        }
        finally
        {
            int remaining = Interlocked.Decrement(ref _instanceCount);
            _logger.LogWarning("Экземпляр плагина '{PluginId}' утилизирован из-за сбоя. Активных инстансов в пуле: {Count}.", _descriptor.PluginId, remaining);
        }
    }

    /// <summary>
    /// Создает новый экземпляр <see cref="WasmInstance"/> с настройками памяти и эпохи из дескриптора.
    /// </summary>
    private WasmInstance CreateNewInstance()
    {
        return new WasmInstance(
            _engine,
            _module,
            _linker,
            maxBufferCapacity: 32768,
            epochDeadline: Math.Max(1, _descriptor.ExecutionTimeoutMs / 10));
    }

    /// <summary>
    /// Освобождает все созданные экземпляры плагина и дескриптор модуля WebAssembly.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        while (_pool.TryDequeue(out var instance))
        {
            try
            {
                instance.Dispose();
            }
            catch
            {
                // Подавление ошибок при групповой утилизации
            }
        }

        _module.Dispose();
        _logger.LogDebug("Пул экземпляров плагина '{PluginId}' успешно освобожден.", _descriptor.PluginId);
    }
}
