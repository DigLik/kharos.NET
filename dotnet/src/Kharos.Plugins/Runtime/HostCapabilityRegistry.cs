using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasmtime;

namespace Kharos.Plugins.Runtime;

/// <summary>
/// Реестр безопасных функций хоста, разрешенных для импорта и вызова из модулей WebAssembly.
/// Реализует паттерн белого списка возможностей (Capability-based Security).
/// </summary>
public sealed class HostCapabilityRegistry
{
    private readonly object _syncLock = new();
    private readonly List<Action<Linker>> _registrars = [];
    private ILogger<HostCapabilityRegistry> _logger;
    private readonly Action<string>? _customLogAction;

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="HostCapabilityRegistry"/> со стандартным набором возможностей и логгером.
    /// </summary>
    /// <param name="logger">Экземпляр <see cref="ILogger{TCategoryName}"/> для структурированного логирования.</param>
    /// <param name="customLogAction">Опциональный дополнительный делегат обработки строковых сообщений.</param>
    public HostCapabilityRegistry(ILogger<HostCapabilityRegistry>? logger = null, Action<string>? customLogAction = null)
    {
        _logger = logger ?? NullLogger<HostCapabilityRegistry>.Instance;
        _customLogAction = customLogAction;
        AddBuiltInCapabilities();
    }

    /// <summary>
    /// Привязывает сконфигурированный экземпляр логгера к реестру возможностей.
    /// </summary>
    /// <param name="logger">Экземпляр логгера.</param>
    public void AttachLogger(ILogger<HostCapabilityRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="HostCapabilityRegistry"/> с пользовательским делегатом логирования.
    /// </summary>
    /// <param name="customLogAction">Делегат для вывода сообщений плагина.</param>
    public HostCapabilityRegistry(Action<string> customLogAction)
        : this(null, customLogAction)
    {
    }

    /// <summary>
    /// Регистрирует встроенные системные возможности хоста по умолчанию.
    /// </summary>
    private void AddBuiltInCapabilities()
    {
        // 1. host_get_timestamp() -> i64 (текущее время UTC в тиках)
        AddCustom(linker =>
        {
            linker.DefineFunction("env", "host_get_timestamp", () => DateTime.UtcNow.Ticks);
        });

        // 2. host_log(int strPtr, int strLen) -> void (безопасный логгер строковых сообщений)
        AddCustom(linker =>
        {
            linker.DefineFunction("env", "host_log", (Caller caller, int strPtr, int strLen) =>
            {
                var memory = caller.GetMemory("memory");
                if (memory == null)
                {
                    return;
                }

                // Строгая проверка границ сегмента памяти
                if (strPtr < 0 || strLen <= 0 || (long)strPtr + strLen > (long)memory.GetLength())
                {
                    return;
                }

                var msg = memory.ReadString(strPtr, strLen);
                _logger.LogInformation("Сообщение плагина WASM: {PluginMessage}", msg);
                _customLogAction?.Invoke(msg);
            });
        });
    }

    /// <summary>
    /// Добавляет пользовательскую безопасную хост-функцию в компоновщик плагинов.
    /// </summary>
    /// <param name="registerAction">Действие компоновщика для определения функции в <see cref="Linker"/>.</param>
    /// <exception cref="ArgumentNullException">Выбрасывается, если <paramref name="registerAction"/> равен <see langword="null"/>.</exception>
    public void AddCustom(Action<Linker> registerAction)
    {
        ArgumentNullException.ThrowIfNull(registerAction);

        lock (_syncLock)
        {
            _registrars.Add(registerAction);
        }
    }

    /// <summary>
    /// Применяет все зарегистрированные хост-функции к переданному компоновщику <see cref="Linker"/>.
    /// </summary>
    /// <param name="linker">Экземпляр компоновщика Wasmtime.</param>
    /// <exception cref="ArgumentNullException">Выбрасывается, если <paramref name="linker"/> равен <see langword="null"/>.</exception>
    public void ApplyTo(Linker linker)
    {
        ArgumentNullException.ThrowIfNull(linker);

        Action<Linker>[] snapshot;
        lock (_syncLock)
        {
            snapshot = [.. _registrars];
        }

        foreach (var register in snapshot)
        {
            register(linker);
        }
    }
}
