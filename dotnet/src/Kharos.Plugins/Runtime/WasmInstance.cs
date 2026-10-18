using Wasmtime;

namespace Kharos.Plugins.Runtime;

/// <summary>
/// Изолированный экземпляр плагина WebAssembly (<see cref="Wasmtime.Store"/>, <see cref="Wasmtime.Instance"/>, <see cref="Wasmtime.Memory"/>).
/// Обеспечивает изолированное линейное адресное пространство и строго однопоточный вызов функций модуля.
/// </summary>
public sealed class WasmInstance : IDisposable
{
    private readonly Function _processFunc;
    private readonly Func<int, int, int, int, int>? _fastProcessFunc;
    private bool _disposed;

    /// <summary>
    /// Хранилище состояния среды исполнения Wasmtime.
    /// </summary>
    public Store Store { get; }

    /// <summary>
    /// Экземпляр скомпилированного модуля WebAssembly.
    /// </summary>
    public Instance Instance { get; }

    /// <summary>
    /// Линейная изолированная память плагина.
    /// </summary>
    public Memory Memory { get; }

    /// <summary>
    /// Смещение начала входного буфера в линейной памяти WASM (в байтах).
    /// </summary>
    public int InBufferOffset { get; }

    /// <summary>
    /// Смещение начала выходного буфера в линейной памяти WASM (в байтах).
    /// </summary>
    public int OutBufferOffset { get; }

    /// <summary>
    /// Максимальная вместимость одного буфера (входного/выходного) в байтах.
    /// </summary>
    public int MaxBufferCapacity { get; }

    /// <summary>
    /// Инициализирует новый экземпляр изолированного плагина <see cref="WasmInstance"/>.
    /// </summary>
    /// <param name="engine">Движок Wasmtime.</param>
    /// <param name="module">Скомпилированный модуль WebAssembly.</param>
    /// <param name="linker">Компоновщик с зарегистрированными хост-функциями.</param>
    /// <param name="maxBufferCapacity">Максимальная вместимость каждого буфера в байтах.</param>
    /// <param name="epochDeadline">Лимит тиков эпохи до прерывания выполнения.</param>
    /// <exception cref="ArgumentNullException">Выбрасывается, если обязательные аргументы равны <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Выбрасывается, если вместимость буфера меньше либо равна 0.</exception>
    /// <exception cref="InvalidOperationException">Выбрасывается, если модуль не экспортирует память 'memory' или точку входа 'process_buffer'/'process'.</exception>
    public WasmInstance(
        Engine engine,
        Module module,
        Linker linker,
        int maxBufferCapacity = 65536,
        int epochDeadline = 10)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(linker);

        if (maxBufferCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBufferCapacity), "Вместимость буфера должна быть строго положительным числом.");
        }

        MaxBufferCapacity = maxBufferCapacity;
        Store = new Store(engine);
        Store.SetEpochDeadline((ulong)Math.Max(1, epochDeadline));

        Instance = linker.Instantiate(Store, module);
        Memory = Instance.GetMemory("memory") ??
                 throw new InvalidOperationException("WASM модуль обязан экспортировать сегмент памяти 'memory'.");

        _processFunc = Instance.GetFunction("process_buffer") ??
                       Instance.GetFunction("process") ??
                       throw new InvalidOperationException("WASM модуль обязан экспортировать функцию 'process_buffer' или 'process'.");

        _fastProcessFunc = _processFunc.WrapFunc<int, int, int, int, int>();

        InBufferOffset = 0;
        OutBufferOffset = maxBufferCapacity;

        // Проверяем, достаточно ли выделено линейной памяти под входной и выходной буферы
        ulong requiredBytes = (ulong)(OutBufferOffset + MaxBufferCapacity);
        if ((ulong)Memory.GetLength() < requiredBytes)
        {
            ulong currentBytes = (ulong)Memory.GetLength();
            ulong missingBytes = requiredBytes - currentBytes;
            long pagesToGrow = (long)((missingBytes + 65535) / 65536);
            try
            {
                Memory.Grow(pagesToGrow);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Линейная память WASM ({currentBytes} байт) недостаточна для размещения буферов ({requiredBytes} байт), и попытка расширения памяти не удалась.",
                    ex);
            }
        }
    }

    /// <summary>
    /// Вызывает экспортированную функцию обработки буфера внутри WebAssembly модуля.
    /// </summary>
    /// <param name="inLen">Фактическая длина входных данных в байтах.</param>
    /// <param name="outMaxLen">Максимальный допустимый размер выходных данных в байтах.</param>
    /// <returns>Количество байт, записанных модулем в выходной буфер, либо отрицательный код ошибки.</returns>
    /// <exception cref="ObjectDisposedException">Выбрасывается, если инстанс был освобожден.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Выбрасывается, если длины выходят за допустимые границы.</exception>
    public int Invoke(int inLen, int outMaxLen)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (inLen < 0 || inLen > MaxBufferCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(inLen), $"Длина входных данных ({inLen}) выходит за пределы [0, {MaxBufferCapacity}].");
        }

        if (outMaxLen < 0 || outMaxLen > MaxBufferCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(outMaxLen), $"Максимальная длина выходных данных ({outMaxLen}) выходит за пределы [0, {MaxBufferCapacity}].");
        }

        if (_fastProcessFunc != null)
        {
            return _fastProcessFunc(InBufferOffset, inLen, OutBufferOffset, outMaxLen);
        }

        var res = _processFunc.Invoke(InBufferOffset, inLen, OutBufferOffset, outMaxLen);
        return (int)res!;
    }

    /// <summary>
    /// Предоставляет прямой доступ к срезу линейной памяти WASM в виде <see cref="Span{T}"/>.
    /// </summary>
    /// <returns>Срез всей доступной памяти экземпляра.</returns>
    /// <exception cref="ObjectDisposedException">Выбрасывается, если инстанс был освобожден.</exception>
    public Span<byte> GetMemorySpan()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Memory.GetSpan(0, (int)Memory.GetLength());
    }

    /// <summary>
    /// Обновляет лимит тиков эпохи сторожа для текущего вызова.
    /// </summary>
    /// <param name="ticks">Количество тиков эпохи до срабатывания сторожа.</param>
    /// <exception cref="ObjectDisposedException">Выбрасывается, если инстанс был освобожден.</exception>
    public void ResetEpochDeadline(ulong ticks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Store.SetEpochDeadline(ticks);
    }

    /// <summary>
    /// Освобождает ресурсы среды исполнения Wasmtime и изолированного хранилища.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Store.Dispose();
    }
}
