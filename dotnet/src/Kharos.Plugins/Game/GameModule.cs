using System.Collections.Concurrent;
using Kharos.Mod.Abi;
using MemoryPack;
using Wasmtime;

namespace Kharos.Plugins.Game;

/// <summary>
/// Раскладка кадра системы мода: сколько столбцов f64 и какие из них вернуть в мир. Блок столбцов — как
/// QueryColumns.Data движка: компонент за компонентом в порядке Query, внутри — поля по Slot, у каждого n значений.
/// </summary>
/// <param name="Info">Объявление системы из манифеста.</param>
/// <param name="Width">Столбцов f64 в кадре.</param>
/// <param name="Write">Столбцы, которые вернуть в мир: (первый столбец, сколько) — по компоненту из Write.</param>
public sealed record ModSystemLayout(ModSystemInfo Info, int Width, IReadOnlyList<(int First, int Count)> Write);

/// <summary>
/// Скомпилированный модуль мода с его ModLoadInfo. Экземпляры без состояния: их можно держать пулом и выбрасывать
/// после ловушки. Перерасходы времени считаются на мод: после <see cref="GameModuleOptions.MaxStrikes"/> он выключен.
/// </summary>
public sealed class GameModule : IDisposable
{
    private readonly ConcurrentQueue<GameModuleInstance> _pool = new();
    private int _strikes;
    private bool _disposed;

    internal GameModule(GameModuleHost host, string modId, Module module, ModLoadInfo load)
    {
        Host = host;
        ModId = modId;
        Module = module;
        LoadInfo = load;
        LoadBytes = MemoryPackSerializer.Serialize(load);
        Imports = [.. module.Imports.Select(i => $"{i.ModuleName}.{i.Name}")];
    }

    /// <summary>Имя мода.</summary>
    public string ModId { get; }

    /// <summary>Что отдаётся моду в kh_load.</summary>
    public ModLoadInfo LoadInfo { get; }

    /// <summary>Импорты модуля «модуль.имя» (все — из белого списка).</summary>
    public IReadOnlyList<string> Imports { get; }

    /// <summary>Перерасходов времени.</summary>
    public int Strikes => Volatile.Read(ref _strikes);

    /// <summary>Выключен ли мод.</summary>
    public bool Disabled => Strikes >= Host.Options.MaxStrikes;

    internal GameModuleHost Host { get; }
    internal Module Module { get; }
    internal byte[] LoadBytes { get; }

    /// <summary>Новый экземпляр: запуск рантайма мода и kh_load.</summary>
    /// <exception cref="ModDisabledException">Мод выключен.</exception>
    public GameModuleInstance CreateInstance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Disabled)
            throw new ModDisabledException(ModId, Strikes);
        return new GameModuleInstance(this);
    }

    /// <summary>Экземпляр из пула или новый.</summary>
    public GameModuleInstance Rent()
    {
        while (_pool.TryDequeue(out var instance))
            if (!instance.Faulted)
                return instance;
            else
                instance.Dispose();
        return CreateInstance();
    }

    /// <summary>Вернуть экземпляр в пул; негодный (после ловушки) — выбрасывается.</summary>
    public void Return(GameModuleInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (_disposed || instance.Faulted || Disabled)
            instance.Dispose();
        else
            _pool.Enqueue(instance);
    }

    /// <summary>
    /// Засчитать сбой: перерасход времени (считает сам экземпляр) или то, что заметил движок, — неверный буфер команд,
    /// ловушка. После <see cref="GameModuleOptions.MaxStrikes"/> мод выключен.
    /// </summary>
    public void Strike() => Interlocked.Increment(ref _strikes);

    /// <summary>Раскладки систем по манифесту и схеме из ModLoadInfo.</summary>
    internal ModSystemLayout[] Layouts(ModManifest manifest)
    {
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var component in LoadInfo.Components)
            columns[component.Name] = component.Fields.Count(f => f.Kind != ModAbi.KindBytes);
        var layouts = new ModSystemLayout[manifest.Systems.Length];
        for (var id = 0; id < layouts.Length; id++)
        {
            var info = manifest.Systems[id];
            var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
            var width = 0;
            foreach (var component in info.Query)
            {
                if (!columns.TryGetValue(component, out var count))
                    throw new ModAbiException(ModId, $"система {info.Name}: нет компонента {component}");
                if (!offsets.TryAdd(component, width))
                    throw new ModAbiException(ModId, $"система {info.Name}: {component} в запросе дважды");
                width += count;
            }
            var write = new List<(int, int)>();
            foreach (var component in info.Write)
            {
                if (!offsets.TryGetValue(component, out var first))
                    throw new ModAbiException(ModId, $"система {info.Name}: Write {component} — не из запроса");
                write.Add((first, columns[component]));
            }
            layouts[id] = new ModSystemLayout(info, width, write);
        }
        return layouts;
    }

    /// <summary>Выбрасывает экземпляры пула и модуль.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        while (_pool.TryDequeue(out var instance))
            instance.Dispose();
        Module.Dispose();
    }
}
