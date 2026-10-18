namespace Kharos.Plugins.Abstractions;

/// <summary>
/// Описание метаданных, конфигурации ресурсов и ограничений выполнения WebAssembly плагина.
/// </summary>
public sealed class PluginDescriptor
{
    /// <summary>
    /// Уникальный строковый идентификатор плагина (например, "plugin-0001", "crypto-hasher").
    /// </summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>
    /// Читаемое имя плагина. Если не задано, по умолчанию используется <see cref="PluginId"/>.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Версия плагина в формате SemVer (по умолчанию "1.0.0").
    /// </summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>
    /// Описание назначения и возможностей плагина.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Максимальный размер линейной памяти в страницах WebAssembly (1 страница = 64 КБ).
    /// По умолчанию: 2 страницы (128 КБ).
    /// </summary>
    public int MaxMemoryPages { get; set; } = 2;

    /// <summary>
    /// Предельное время непрерывного выполнения вызова в миллисекундах до принудительного прерывания сторожем (Epoch Interruption).
    /// По умолчанию: 100 мс.
    /// </summary>
    public int ExecutionTimeoutMs { get; set; } = 100;

    /// <summary>
    /// Начальное количество предварительно созданных изолированных экземпляров в пуле (Warmup).
    /// По умолчанию: 4.
    /// </summary>
    public int InitialPoolSize { get; set; } = 4;

    /// <summary>
    /// Предельно допустимое количество параллельных экземпляров плагина в пуле при пиковой нагрузке.
    /// По умолчанию: 64.
    /// </summary>
    public int MaxPoolSize { get; set; } = 64;

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="PluginDescriptor"/> со значениями по умолчанию.
    /// </summary>
    public PluginDescriptor()
    {
    }

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="PluginDescriptor"/> с заданными параметрами.
    /// </summary>
    /// <param name="pluginId">Уникальный идентификатор плагина.</param>
    /// <param name="name">Читаемое имя плагина.</param>
    /// <param name="version">Версия плагина.</param>
    /// <param name="description">Описание плагина.</param>
    /// <param name="maxMemoryPages">Максимальный объем памяти в страницах (64 КБ на страницу).</param>
    /// <param name="executionTimeoutMs">Таймаут выполнения в миллисекундах.</param>
    /// <param name="initialPoolSize">Стартовый размер пула инстансов.</param>
    /// <param name="maxPoolSize">Максимальный размер пула инстансов.</param>
    public PluginDescriptor(
        string pluginId,
        string name = "",
        string version = "1.0.0",
        string description = "",
        int maxMemoryPages = 2,
        int executionTimeoutMs = 100,
        int initialPoolSize = 4,
        int maxPoolSize = 64)
    {
        PluginId = pluginId;
        Name = string.IsNullOrWhiteSpace(name) ? pluginId : name;
        Version = version;
        Description = description;
        MaxMemoryPages = maxMemoryPages;
        ExecutionTimeoutMs = executionTimeoutMs;
        InitialPoolSize = initialPoolSize;
        MaxPoolSize = maxPoolSize;
    }

    /// <summary>
    /// Проверяет корректность параметров дескриптора.
    /// </summary>
    /// <exception cref="ArgumentException">Выбрасывается, если идентификатор пуст или параметры имеют некорректные значения.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PluginId))
        {
            throw new ArgumentException("Идентификатор плагина (PluginId) не может быть пустым.", nameof(PluginId));
        }

        if (MaxMemoryPages <= 0)
        {
            throw new ArgumentException("Количество страниц памяти (MaxMemoryPages) должно быть строго больше 0.", nameof(MaxMemoryPages));
        }

        if (ExecutionTimeoutMs <= 0)
        {
            throw new ArgumentException("Таймаут выполнения (ExecutionTimeoutMs) должен быть строго больше 0 мс.", nameof(ExecutionTimeoutMs));
        }

        if (InitialPoolSize < 0)
        {
            throw new ArgumentException("Начальный размер пула (InitialPoolSize) не может быть отрицательным.", nameof(InitialPoolSize));
        }

        if (MaxPoolSize <= 0)
        {
            throw new ArgumentException("Максимальный размер пула (MaxPoolSize) должен быть строго больше 0.", nameof(MaxPoolSize));
        }

        if (InitialPoolSize > MaxPoolSize)
        {
            throw new ArgumentException(
                $"Начальный размер пула ({InitialPoolSize}) не может превышать максимальный размер пула ({MaxPoolSize}).",
                nameof(InitialPoolSize));
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = PluginId;
        }
    }
}
