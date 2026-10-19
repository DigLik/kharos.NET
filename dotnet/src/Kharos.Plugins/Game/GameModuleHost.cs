using Kharos.Mod.Abi;
using Kharos.Plugins.Runtime;
using Wasmtime;

namespace Kharos.Plugins.Game;

/// <summary>
/// Хозяин игровых модов (docs/dotnet.md, §4.2): движок Wasmtime с прерыванием по эпохам, сторож эпох и компоновщик
/// с белым списком импортов. Один на процесс (или на мир); модули и экземпляры — от него. Не зависит от
/// Kharos.Engine: столбцы — блоки f64, команды — байты.
/// </summary>
public sealed class GameModuleHost : IDisposable
{
    private bool _disposed;

    /// <param name="options">Пределы и журнал; null — по умолчанию.</param>
    public GameModuleHost(GameModuleOptions? options = null)
    {
        Options = options ?? new GameModuleOptions();
        Options.Validate();
        Engine = SharedWasm.EngineFor(Options.WatchdogIntervalMs);
        Linker = new Linker(Engine);
        GameImports.Define(Linker);
    }

    /// <summary>Версия ABI хоста.</summary>
    public static int AbiVersion => ModAbi.Version;

    /// <summary>Разрешённые импорты модулей «модуль.имя».</summary>
    public static IReadOnlySet<string> AllowedImports => GameImports.Allowed;

    /// <summary>Пределы и журнал.</summary>
    public GameModuleOptions Options { get; }
    internal Engine Engine { get; }
    internal Linker Linker { get; }

    /// <summary>Компилирует модуль мода и проверяет импорты; экземпляры — <see cref="GameModule.CreateInstance"/>.</summary>
    /// <param name="modId">Имя мода (для журнала и ошибок).</param>
    /// <param name="wasm">Байты ядрового модуля wasm.</param>
    /// <param name="load">Что отдать моду в kh_load: схема мира, данные, зерно.</param>
    /// <exception cref="ModImportException">Модуль просит импорты вне белого списка.</exception>
    public GameModule Load(string modId, ReadOnlySpan<byte> wasm, ModLoadInfo load)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(modId);
        ArgumentNullException.ThrowIfNull(load);
        // Модуль общий на процесс (SharedWasm): здесь он не освобождается.
        var module = SharedWasm.Compile(Options.WatchdogIntervalMs, modId, wasm);
        var forbidden = GameImports.Forbidden(module);
        if (forbidden.Count > 0)
            throw new ModImportException(modId, forbidden);
        return new GameModule(this, modId, module, load);
    }

    /// <summary>То же из файла.</summary>
    public GameModule LoadFile(string modId, string path, ModLoadInfo load) => Load(modId, File.ReadAllBytes(path), load);

    /// <summary>Шагов эпохи на время timeoutMs.</summary>
    internal ulong EpochTicks(int timeoutMs) => (ulong)Math.Max(1, (timeoutMs + Options.WatchdogIntervalMs - 1) / Options.WatchdogIntervalMs) + 1;

    /// <summary>Останавливает сторожа, освобождает компоновщик и движок Wasmtime.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Engine, сторож и модули общие на процесс — не трогаем.
        Linker.Dispose();
    }
}
