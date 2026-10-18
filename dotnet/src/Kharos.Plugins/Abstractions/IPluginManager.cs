namespace Kharos.Plugins.Abstractions;

/// <summary>
/// Основной интерфейс управления жизненным циклом и высокоскоростного исполнения WebAssembly плагинов.
/// Предоставляет поддержку сериализации MemoryPack для строго типизированных DTO и прямой обмен через <see cref="ReadOnlySpan{T}"/>.
/// </summary>
public interface IPluginManager : IDisposable
{
    /// <summary>
    /// Выполняет строго типизированный вызов точки входа плагина с автоматической сериализацией через MemoryPack.
    /// </summary>
    /// <typeparam name="TIn">Тип входного объекта данных, поддерживающий сериализацию MemoryPack.</typeparam>
    /// <typeparam name="TOut">Тип выходного объекта данных, поддерживающий десериализацию MemoryPack.</typeparam>
    /// <param name="pluginId">Уникальный идентификатор вызываемого плагина.</param>
    /// <param name="input">Входной объект запроса.</param>
    /// <returns>Экземпляр десериализованного ответа типа <typeparamref name="TOut"/>.</returns>
    /// <exception cref="Exceptions.PluginNotFoundException">Выбрасывается, если плагин с указанным <paramref name="pluginId"/> не зарегистрирован.</exception>
    /// <exception cref="Exceptions.PluginBufferOverflowException">Выбрасывается, если сериализованный запрос превышает вместимость буфера плагина.</exception>
    /// <exception cref="Exceptions.PluginExecutionException">Выбрасывается при ошибке выполнения внутри модуля WebAssembly.</exception>
    /// <exception cref="ObjectDisposedException">Выбрасывается, если менеджер плагинов уже был освобожден.</exception>
    TOut Execute<TIn, TOut>(string pluginId, in TIn input);

    /// <summary>
    /// Выполняет безопасный строго типизированный вызов плагина с перехватом исключений.
    /// </summary>
    /// <typeparam name="TIn">Тип входного объекта данных.</typeparam>
    /// <typeparam name="TOut">Тип выходного объекта данных.</typeparam>
    /// <param name="pluginId">Уникальный идентификатор вызываемого плагина.</param>
    /// <param name="input">Входной объект запроса.</param>
    /// <param name="output">Выходной объект ответа в случае успеха или значение по умолчанию при ошибке.</param>
    /// <returns><see langword="true"/>, если вызов завершился успешно; иначе <see langword="false"/>.</returns>
    bool TryExecute<TIn, TOut>(string pluginId, in TIn input, out TOut? output);

    /// <summary>
    /// Выполняет низкоуровневый прямой вызов функции плагина без выделения памяти в куче (Zero-Allocation).
    /// </summary>
    /// <param name="pluginId">Уникальный идентификатор вызываемого плагина.</param>
    /// <param name="input">Входной бинарный буфер данных.</param>
    /// <param name="output">Выходной буфер для записи результата исполнения.</param>
    /// <returns>Количество байт, фактически записанных модулем в выходной буфер.</returns>
    /// <exception cref="Exceptions.PluginNotFoundException">Выбрасывается, если плагин не найден.</exception>
    /// <exception cref="Exceptions.PluginBufferOverflowException">Выбрасывается, если размер буферов превышает лимит инстанса плагина.</exception>
    /// <exception cref="Exceptions.PluginExecutionException">Выбрасывается при ошибке выполнения модуля.</exception>
    /// <exception cref="ObjectDisposedException">Выбрасывается, если менеджер плагинов освобожден.</exception>
    int ExecuteRaw(string pluginId, ReadOnlySpan<byte> input, Span<byte> output);

    /// <summary>
    /// Регистрирует плагин из скомпилированного бинарного потока WebAssembly (.wasm).
    /// </summary>
    /// <param name="descriptor">Метаданные и параметры ограничений плагина.</param>
    /// <param name="wasmStream">Поток байт скомпилированного модуля .wasm.</param>
    /// <exception cref="ArgumentNullException">Выбрасывается, если дескриптор или поток равен <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Выбрасывается при некорректных параметрах в дескрипторе или невозможности чтения из потока.</exception>
    /// <exception cref="ObjectDisposedException">Выбрасывается, если менеджер плагинов освобожден.</exception>
    void RegisterPlugin(PluginDescriptor descriptor, Stream wasmStream);

    /// <summary>
    /// Регистрирует плагин из текстового представления WebAssembly (формат WAT).
    /// </summary>
    /// <param name="descriptor">Метаданные и параметры ограничений плагина.</param>
    /// <param name="watText">Текстовый код модуля в формате WebAssembly Text.</param>
    /// <exception cref="ArgumentNullException">Выбрасывается, если дескриптор равен <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Выбрасывается при некорректных параметрах дескриптора или пустом тексте WAT.</exception>
    /// <exception cref="ObjectDisposedException">Выбрасывается, если менеджер плагинов освобожден.</exception>
    void RegisterPluginWat(PluginDescriptor descriptor, string watText);

    /// <summary>
    /// Выгружает зарегистрированный плагин и освобождает все ресурсы связанного пула инстансов.
    /// </summary>
    /// <param name="pluginId">Идентификатор удаляемого плагина.</param>
    /// <returns><see langword="true"/>, если плагин был успешно найден и выгружен; иначе <see langword="false"/>.</returns>
    bool UnregisterPlugin(string pluginId);

    /// <summary>
    /// Проверяет, зарегистрирован ли плагин с указанным идентификатором.
    /// </summary>
    /// <param name="pluginId">Идентификатор плагина.</param>
    /// <returns><see langword="true"/>, если плагин присутствует в системе; иначе <see langword="false"/>.</returns>
    bool ContainsPlugin(string pluginId);

    /// <summary>
    /// Возвращает дескриптор зарегистрированного плагина или <see langword="null"/>, если плагин не найден.
    /// </summary>
    /// <param name="pluginId">Идентификатор плагина.</param>
    /// <returns>Дескриптор <see cref="PluginDescriptor"/> или <see langword="null"/>.</returns>
    PluginDescriptor? TryGetDescriptor(string pluginId);

    /// <summary>
    /// Возвращает коллекцию дескрипторов всех зарегистрированных в данный момент плагинов.
    /// </summary>
    /// <returns>Коллекция метаданных активных плагинов.</returns>
    IReadOnlyCollection<PluginDescriptor> GetPlugins();
}
