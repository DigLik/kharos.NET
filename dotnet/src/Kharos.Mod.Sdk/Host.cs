using System.Runtime.InteropServices;
using System.Text;

namespace Kharos.Mod.Sdk;

/// <summary>Уровень записи журнала.</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// Импорты хоста (модуль wasm <c>kh</c>) — всё, что мод может попросить у движка. Часов нет: у мода нет
/// недетерминизма. В режиме CLR (тесты: мод — сборка .NET в процессе хоста) те же вызовы идут к делегатам, которые
/// поставил загрузчик хоста (<see cref="Runtime.ClrBridge"/>); вне мода (без хоста) вызывать нельзя.
/// </summary>
public static unsafe class Host
{
    /// <summary>Случайное в [0, 1) из зерна мира.</summary>
    public static double Random() => Runtime.ClrBridge.Random is { } random ? random() : ImportRandom();

    /// <summary>Запись в журнал хоста.</summary>
    public static void Log(string message, LogLevel level = LogLevel.Info)
    {
        if (Runtime.ClrBridge.Log is { } log)
        {
            log((int)level, message);
            return;
        }
        var count = Encoding.UTF8.GetByteCount(message);
        Span<byte> bytes = count <= 512 ? stackalloc byte[count] : new byte[count];
        Encoding.UTF8.GetBytes(message, bytes);
        fixed (byte* p = bytes)
            ImportLog((int)level, p, count);
    }

    /// <summary>
    /// Инструмент движка напрямую: номер (ModAbi.Tool*), байты на вход, место под ответ. Возвращает длину ответа (если
    /// она больше места — ничего не записано) или отрицательный код ошибки. Удобнее — типизированные <see cref="Tools"/>.
    /// </summary>
    public static int Tool(int id, ReadOnlySpan<byte> input, Span<byte> output)
    {
        fixed (byte* i = input)
        fixed (byte* o = output)
            return Runtime.ClrBridge.Tool is { } tool
                ? tool(id, (nint)i, input.Length, (nint)o, output.Length)
                : ImportTool(id, i, input.Length, o, output.Length);
    }

    [DllImport("kh", EntryPoint = "random")]
    [WasmImportLinkage]
    private static extern double ImportRandom();

    [DllImport("kh", EntryPoint = "log")]
    [WasmImportLinkage]
    private static extern void ImportLog(int level, byte* message, int length);

    [DllImport("kh", EntryPoint = "tool")]
    [WasmImportLinkage]
    private static extern int ImportTool(int id, byte* input, int inputLength, byte* output, int outputCapacity);
}
