using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wasmtime;

namespace Kharos.Plugins.Runtime;

/// <summary>
/// Фоновый сторож на базе механизма Wasmtime Epoch Interruption.
/// Периодически увеличивает глобальный счетчик эпох движка WebAssembly,
/// обеспечивая аппаратное и кооперативное прерывание бесконечных циклов (защита от DoS)
/// с нулевыми накладными расходами на вызов инструкций самого плагина.
/// </summary>
public sealed class WasmEpochWatchdog : IDisposable
{
    private readonly Engine _engine;
    private readonly ILogger<WasmEpochWatchdog> _logger;
    private readonly CancellationTokenSource? _cts;
    private readonly Task? _watchdogTask;
    private bool _disposed;

    /// <summary>
    /// Инициализирует новый экземпляр сторожа эпох <see cref="WasmEpochWatchdog"/>.
    /// </summary>
    /// <param name="engine">Экземпляр движка Wasmtime с включенной опцией Epoch Interruption.</param>
    /// <param name="intervalMs">Интервал инкремента эпохи в миллисекундах. Значение 0 или меньше отключает периодический таймер.</param>
    /// <param name="logger">Экземпляр <see cref="ILogger{TCategoryName}"/> для логирования жизненного цикла сторожа.</param>
    /// <exception cref="ArgumentNullException">Выбрасывается, если параметр <paramref name="engine"/> равен <see langword="null"/>.</exception>
    public WasmEpochWatchdog(Engine engine, int intervalMs = 10, ILogger<WasmEpochWatchdog>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _logger = logger ?? NullLogger<WasmEpochWatchdog>.Instance;

        if (intervalMs > 0)
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _logger.LogDebug("Сторож эпох WASM запущен с интервалом {IntervalMs} мс.", intervalMs);

            _watchdogTask = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
                try
                {
                    while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                    {
                        _engine.IncrementEpoch();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ожидаемое завершение при утилизации сторожа
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Непредвиденная ошибка в цикле сторожа эпох WASM.");
                }
            }, token);
        }
        else
        {
            _logger.LogDebug("Сторож эпох WASM отключен (интервал равен {IntervalMs} мс).", intervalMs);
        }
    }

    /// <summary>
    /// Освобождает ресурсы сторожа эпох и останавливает фоновую задачу таймера.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_cts != null)
        {
            try
            {
                _cts.Cancel();
                _watchdogTask?.Wait(TimeSpan.FromMilliseconds(100));
                _logger.LogDebug("Сторож эпох WASM успешно остановлен.");
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Ошибка при остановке фоновой задачи сторожа эпох.");
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }
}
