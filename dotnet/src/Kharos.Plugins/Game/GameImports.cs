using System.Buffers.Binary;
using System.Text;
using Kharos.Mod.Abi;
using Wasmtime;

namespace Kharos.Plugins.Game;

/// <summary>Состояние хранилища Wasmtime игрового экземпляра: его видят функции хоста через Caller.</summary>
internal sealed class GameStoreData(string modId, GameModuleOptions options, ulong seed)
{
    public string ModId { get; } = modId;
    public GameModuleOptions Options { get; } = options;
    public GameModuleInstance? Owner { get; set; }

    /// <summary>Байты для random_get (зерно хеширования строк рантайма и т. п.) — из зерна мира, не из ОС.</summary>
    public ModRandom WasiRandom { get; } = new(seed ^ 0x5741_5349_5241_4E44);

    /// <summary>Последняя строка stderr мода (сообщение рантайма перед выходом).</summary>
    public string? LastError { get; set; }

    /// <summary>Код proc_exit, если мод завершился.</summary>
    public int? ExitCode { get; set; }

    /// <summary>Недописанная строка stdout/stderr мода.</summary>
    public StringBuilder Output { get; } = new();
}

/// <summary>
/// Импорты игрового мода — белый список (docs/dotnet.md, §4.2): kh.log, kh.random, kh.tool и заглушки WASI
/// preview1, которые просит рантайм NativeAOT-LLVM. Заглушки детерминированы: часы стоят на нуле, среды и файлов
/// нет, random_get — из зерна мира, вывод — в журнал. Часов хоста (host_get_timestamp песочницы общего вида) здесь
/// нет: у игрового мода нет недетерминизма.
/// </summary>
internal static class GameImports
{
    private const string Wasi = "wasi_snapshot_preview1";

    private const int ErrnoSuccess = 0;
    private const int ErrnoBadF = 8;
    private const int ErrnoNoSys = 52;
    private const int ErrnoSPipe = 70;

    /// <summary>Разрешённые импорты «модуль.имя».</summary>
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        $"{ModAbi.ImportModule}.log",
        $"{ModAbi.ImportModule}.random",
        $"{ModAbi.ImportModule}.tool",
        $"{Wasi}.environ_get",
        $"{Wasi}.environ_sizes_get",
        $"{Wasi}.clock_time_get",
        $"{Wasi}.fd_close",
        $"{Wasi}.fd_fdstat_get",
        $"{Wasi}.fd_prestat_get",
        $"{Wasi}.fd_prestat_dir_name",
        $"{Wasi}.fd_seek",
        $"{Wasi}.fd_write",
        $"{Wasi}.poll_oneoff",
        $"{Wasi}.proc_exit",
        $"{Wasi}.sched_yield",
        $"{Wasi}.random_get",
    };

    /// <summary>Запрещённые импорты модуля; пусто — можно грузить.</summary>
    public static List<string> Forbidden(Module module)
    {
        var forbidden = new List<string>();
        foreach (var import in module.Imports)
        {
            var name = $"{import.ModuleName}.{import.Name}";
            if (import is not FunctionImport || !Allowed.Contains(name))
                forbidden.Add(name);
        }
        return forbidden;
    }

    /// <summary>Определяет все разрешённые импорты в компоновщике.</summary>
    public static void Define(Linker linker)
    {
        const string kh = ModAbi.ImportModule;

        linker.DefineFunction(kh, "log", (Caller caller, int level, int ptr, int len) =>
        {
            var data = Data(caller);
            if (data.Options.Log is null || len <= 0)
                return;
            var memory = Memory(caller);
            var text = Encoding.UTF8.GetString(memory.GetSpan(ptr, Math.Min(len, 64 * 1024)));
            data.Options.Log(new ModLogEntry(data.ModId, level, text));
        });

        linker.DefineFunction(kh, "random", (Caller caller) =>
        {
            var owner = Data(caller).Owner;
            return owner is null ? 0.0 : owner.NextRandom();
        });

        // Инструмент движка: свой обработчик экземпляра или общий набор. Адреса вне памяти и исключения инструмента —
        // код ErrorBadInput, а не ловушка: мод продолжает работать.
        linker.DefineFunction(kh, "tool", (Caller caller, int id, int inPtr, int inLen, int outPtr, int outCap) =>
        {
            var data = Data(caller);
            var memory = Memory(caller);
            var size = memory.GetLength();
            if (inLen < 0 || outCap < 0 || inPtr < 0 || outPtr < 0 || (long)inPtr + inLen > size || (long)outPtr + outCap > size)
                return ModAbi.ErrorBadInput;
            var input = memory.GetSpan(inPtr, inLen);
            var output = memory.GetSpan(outPtr, outCap);
            try
            {
                if (data.Owner?.Tools is { } handler)
                    return handler(id, input, output);
                return data.Options.Tools.TryGetValue(id, out var tool) ? tool(input, output) : ModAbi.ErrorUnknownId;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                data.Options.Log?.Invoke(new ModLogEntry(data.ModId, 3, $"инструмент {id}: {ex.Message}"));
                return ModAbi.ErrorBadInput;
            }
        });

        linker.DefineFunction(Wasi, "environ_get", (int environ, int buffer) => ErrnoSuccess);

        linker.DefineFunction(Wasi, "environ_sizes_get", (Caller caller, int countPtr, int sizePtr) =>
        {
            var memory = Memory(caller);
            memory.WriteInt32(countPtr, 0);
            memory.WriteInt32(sizePtr, 0);
            return ErrnoSuccess;
        });

        // Часов у мода нет: время всегда ноль.
        linker.DefineFunction(Wasi, "clock_time_get", (Caller caller, int clock, long precision, int timePtr) =>
        {
            Memory(caller).WriteInt64(timePtr, 0);
            return ErrnoSuccess;
        });

        linker.DefineFunction(Wasi, "fd_close", (int fd) => ErrnoBadF);
        linker.DefineFunction(Wasi, "fd_fdstat_get", (int fd, int statPtr) => ErrnoBadF);
        // EBADF на первом же дескрипторе — открытых каталогов нет.
        linker.DefineFunction(Wasi, "fd_prestat_get", (int fd, int prestatPtr) => ErrnoBadF);
        linker.DefineFunction(Wasi, "fd_prestat_dir_name", (int fd, int pathPtr, int pathLen) => ErrnoBadF);
        linker.DefineFunction(Wasi, "fd_seek", (int fd, long offset, int whence, int resultPtr) => ErrnoSPipe);

        // stdout и stderr мода — в журнал построчно.
        linker.DefineFunction(Wasi, "fd_write", (Caller caller, int fd, int iovs, int iovsLen, int writtenPtr) =>
        {
            if (fd is not (1 or 2))
                return ErrnoBadF;
            var data = Data(caller);
            var memory = Memory(caller);
            var total = 0;
            for (var k = 0; k < iovsLen; k++)
            {
                var ptr = memory.ReadInt32(iovs + k * 8);
                var len = memory.ReadInt32(iovs + k * 8 + 4);
                if (len <= 0)
                    continue;
                data.Output.Append(Encoding.UTF8.GetString(memory.GetSpan(ptr, len)));
                total += len;
            }
            FlushLines(data, fd == 2 ? 3 : 1);
            memory.WriteInt32(writtenPtr, total);
            return ErrnoSuccess;
        });

        linker.DefineFunction(Wasi, "poll_oneoff", (int input, int output, int count, int eventsPtr) => ErrnoNoSys);

        // Выход мода: код запоминается, дальше libc сама упадёт в unreachable — ловушка. Исключение из функции хоста
        // здесь не бросается: под некоторыми хостами (xUnit) оно роняло процесс.
        linker.DefineFunction(Wasi, "proc_exit", (Caller caller, int code) =>
        {
            Data(caller).ExitCode = code;
        });

        linker.DefineFunction(Wasi, "sched_yield", () => ErrnoSuccess);

        linker.DefineFunction(Wasi, "random_get", (Caller caller, int ptr, int len) =>
        {
            var random = Data(caller).WasiRandom;
            var span = Memory(caller).GetSpan(ptr, len);
            Span<byte> word = stackalloc byte[8];
            for (var at = 0; at < span.Length; at += 8)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(word, (ulong)(random.Next() * (1UL << 53)) ^ random.State);
                word[..Math.Min(8, span.Length - at)].CopyTo(span[at..]);
            }
            return ErrnoSuccess;
        });
    }

    private static GameStoreData Data(Caller caller) =>
        caller.GetData() as GameStoreData ?? throw new InvalidOperationException("хранилище без данных игрового мода");

    private static Wasmtime.Memory Memory(Caller caller) =>
        caller.GetMemory("memory") ?? throw new InvalidOperationException("модуль без памяти memory");

    private static void FlushLines(GameStoreData data, int level)
    {
        var text = data.Output.ToString();
        var end = text.LastIndexOf('\n');
        if (end < 0)
            return;
        foreach (var raw in text[..end].Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (level >= 3 && line.Length > 0)
                data.LastError = line;
            data.Options.Log?.Invoke(new ModLogEntry(data.ModId, level, line));
        }
        data.Output.Remove(0, end + 1);
    }
}
