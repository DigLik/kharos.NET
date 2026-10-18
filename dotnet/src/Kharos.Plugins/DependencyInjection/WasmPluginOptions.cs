namespace Kharos.Plugins.DependencyInjection;

/// <summary>
/// Параметры конфигурации среды исполнения и пула плагинов WebAssembly.
/// </summary>
public sealed class WasmPluginOptions
{
    /// <summary>
    /// Интервал тика фонового сторожа прерывания эпох (Epoch Interruption) в миллисекундах.
    /// Значение 0 или меньше отключает сторож.
    /// По умолчанию: 10 мс.
    /// </summary>
    public int WatchdogIntervalMs { get; set; } = 10;

    /// <summary>
    /// Начальный размер пула экземпляров по умолчанию (Warmup).
    /// По умолчанию: 8.
    /// </summary>
    public int DefaultInitialPoolSize { get; set; } = 8;

    /// <summary>
    /// Максимальный размер пула экземпляров плагина по умолчанию при пиковых нагрузках.
    /// По умолчанию: 64.
    /// </summary>
    public int DefaultMaxPoolSize { get; set; } = 64;

    /// <summary>
    /// Таймаут выполнения вызова плагина по умолчанию в миллисекундах.
    /// По умолчанию: 100 мс.
    /// </summary>
    public int DefaultExecutionTimeoutMs { get; set; } = 100;

    /// <summary>
    /// Максимальный объем линейной памяти плагина по умолчанию в страницах WebAssembly (1 страница = 64 КБ).
    /// По умолчанию: 2 страницы (128 КБ).
    /// </summary>
    public int DefaultMemoryPages { get; set; } = 2;

    /// <summary>
    /// Проверяет корректность установленных параметров конфигурации.
    /// </summary>
    /// <exception cref="ArgumentException">Выбрасывается, если один из параметров содержит недопустимое значение.</exception>
    public void Validate()
    {
        if (DefaultInitialPoolSize < 0)
        {
            throw new ArgumentException("Начальный размер пула (DefaultInitialPoolSize) не может быть отрицательным.", nameof(DefaultInitialPoolSize));
        }

        if (DefaultMaxPoolSize <= 0)
        {
            throw new ArgumentException("Максимальный размер пула (DefaultMaxPoolSize) должен быть больше 0.", nameof(DefaultMaxPoolSize));
        }

        if (DefaultInitialPoolSize > DefaultMaxPoolSize)
        {
            throw new ArgumentException(
                $"Начальный размер пула ({DefaultInitialPoolSize}) не может превышать максимальный размер ({DefaultMaxPoolSize}).",
                nameof(DefaultInitialPoolSize));
        }

        if (DefaultExecutionTimeoutMs <= 0)
        {
            throw new ArgumentException("Таймаут выполнения (DefaultExecutionTimeoutMs) должен быть строго больше 0.", nameof(DefaultExecutionTimeoutMs));
        }

        if (DefaultMemoryPages <= 0)
        {
            throw new ArgumentException("Количество страниц памяти (DefaultMemoryPages) должно быть строго больше 0.", nameof(DefaultMemoryPages));
        }
    }
}
