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
/// недетерминизма. Вне wasm (тесты на хосте) вызывать нельзя.
/// </summary>
public static unsafe class Host
{
    /// <summary>Случайное в [0, 1) из зерна мира.</summary>
    public static double Random() => ImportRandom();

    /// <summary>Запись в журнал хоста.</summary>
    public static void Log(string message, LogLevel level = LogLevel.Info)
    {
        var count = Encoding.UTF8.GetByteCount(message);
        Span<byte> bytes = count <= 512 ? stackalloc byte[count] : new byte[count];
        Encoding.UTF8.GetBytes(message, bytes);
        fixed (byte* p = bytes)
            ImportLog((int)level, p, count);
    }

    /// <summary>
    /// Инструмент движка (пакетный поиск, пути, обзор): номер, байты на вход, место под ответ. Возвращает, сколько
    /// байт записано, или отрицательный код ошибки. Набор инструментов — дело движка (Н3+), сейчас это заготовка.
    /// </summary>
    public static int Tool(int id, ReadOnlySpan<byte> input, Span<byte> output)
    {
        fixed (byte* i = input)
        fixed (byte* o = output)
            return ImportTool(id, i, input.Length, o, output.Length);
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
