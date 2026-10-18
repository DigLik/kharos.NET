namespace Kharos.Plugins.Exceptions;

/// <summary>
/// Исключение, возникающее при попытке обращения к незарегистрированному плагину.
/// Наследует <see cref="KeyNotFoundException"/> для сохранения обратной совместимости.
/// </summary>
public class PluginNotFoundException : KeyNotFoundException
{
    /// <summary>
    /// Идентификатор плагина, который не был найден.
    /// </summary>
    public string PluginId { get; }

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="PluginNotFoundException"/>.
    /// </summary>
    /// <param name="pluginId">Идентификатор плагина.</param>
    public PluginNotFoundException(string pluginId)
        : base($"Плагин с идентификатором '{pluginId}' не зарегистрирован в системе.")
    {
        PluginId = pluginId;
    }

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="PluginNotFoundException"/> с кастомным сообщением.
    /// </summary>
    /// <param name="pluginId">Идентификатор плагина.</param>
    /// <param name="message">Сообщение об ошибке.</param>
    public PluginNotFoundException(string pluginId, string message)
        : base(message)
    {
        PluginId = pluginId;
    }

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="PluginNotFoundException"/> с кастомным сообщением и внутренним исключением.
    /// </summary>
    /// <param name="pluginId">Идентификатор плагина.</param>
    /// <param name="message">Сообщение об ошибке.</param>
    /// <param name="innerException">Внутреннее исключение.</param>
    public PluginNotFoundException(string pluginId, string message, Exception? innerException)
        : base(message, innerException)
    {
        PluginId = pluginId;
    }
}

/// <summary>
/// Исключение, возникающее при сбое выполнения модуля WebAssembly или возврате кода ошибки.
/// Наследует <see cref="InvalidOperationException"/> для сохранения обратной совместимости.
/// </summary>
public class PluginExecutionException : InvalidOperationException
{
    /// <summary>
    /// Идентификатор плагина, вызвавшего ошибку.
    /// </summary>
    public string PluginId { get; }

    /// <summary>
    /// Код ошибки, возвращенный модулем WebAssembly (если применимо).
    /// </summary>
    public int? ExitCode { get; }

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="PluginExecutionException"/>.
    /// </summary>
    /// <param name="pluginId">Идентификатор плагина.</param>
    /// <param name="message">Сообщение об ошибке.</param>
    /// <param name="exitCode">Код возврата плагина.</param>
    public PluginExecutionException(string pluginId, string message, int? exitCode = null)
        : base(message)
    {
        PluginId = pluginId;
        ExitCode = exitCode;
    }

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="PluginExecutionException"/> с внутренним исключением.
    /// </summary>
    /// <param name="pluginId">Идентификатор плагина.</param>
    /// <param name="message">Сообщение об ошибке.</param>
    /// <param name="innerException">Внутреннее исключение (например, Wasmtime.TrapException).</param>
    /// <param name="exitCode">Код возврата плагина.</param>
    public PluginExecutionException(string pluginId, string message, Exception? innerException, int? exitCode = null)
        : base(message, innerException)
    {
        PluginId = pluginId;
        ExitCode = exitCode;
    }
}

/// <summary>
/// Исключение, возникающее при превышении размера буфера обмена данными с памятью WebAssembly.
/// Наследует <see cref="ArgumentException"/> для сохранения обратной совместимости.
/// </summary>
public class PluginBufferOverflowException : ArgumentException
{
    /// <summary>
    /// Идентификатор плагина.
    /// </summary>
    public string PluginId { get; }

    /// <summary>
    /// Запрошенный или фактический размер данных в байтах.
    /// </summary>
    public int RequestedSize { get; }

    /// <summary>
    /// Максимальная вместимость буфера плагина в байтах.
    /// </summary>
    public int MaxCapacity { get; }

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="PluginBufferOverflowException"/>.
    /// </summary>
    /// <param name="pluginId">Идентификатор плагина.</param>
    /// <param name="requestedSize">Размер передаваемых данных.</param>
    /// <param name="maxCapacity">Максимально допустимая вместимость буфера.</param>
    public PluginBufferOverflowException(string pluginId, int requestedSize, int maxCapacity)
        : base($"Размер данных ({requestedSize} байт) для плагина '{pluginId}' превышает максимальную вместимость буфера ({maxCapacity} байт).")
    {
        PluginId = pluginId;
        RequestedSize = requestedSize;
        MaxCapacity = maxCapacity;
    }
}

/// <summary>
/// Исключение, возникающее при исчерпании доступных инстансов в пуле плагина и истечении времени ожидания.
/// Наследует <see cref="TimeoutException"/> для сохранения обратной совместимости.
/// </summary>
public class PluginPoolExhaustedException : TimeoutException
{
    /// <summary>
    /// Идентификатор плагина.
    /// </summary>
    public string PluginId { get; }

    /// <summary>
    /// Максимальный размер пула экземпляров.
    /// </summary>
    public int MaxPoolSize { get; }

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="PluginPoolExhaustedException"/>.
    /// </summary>
    /// <param name="pluginId">Идентификатор плагина.</param>
    /// <param name="maxPoolSize">Максимальный размер пула.</param>
    /// <param name="timeoutMs">Время ожидания в миллисекундах.</param>
    public PluginPoolExhaustedException(string pluginId, int maxPoolSize, int timeoutMs)
        : base($"Превышено время ожидания ({timeoutMs} мс) свободного экземпляра плагина '{pluginId}'. Все {maxPoolSize} экземпляров заняты.")
    {
        PluginId = pluginId;
        MaxPoolSize = maxPoolSize;
    }
}
