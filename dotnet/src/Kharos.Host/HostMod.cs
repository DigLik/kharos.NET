using Kharos.Content;
using Kharos.Engine.World;
using Kharos.Mod.Abi;
using Kharos.Plugins.Game;

namespace Kharos.Host;

/// <summary>
/// Мод на хосте: пакет, модуль wasm и один его экземпляр (docs/dotnet.md, Р8: пул — только для параллельных систем).
/// Экземпляр после ловушки выбрасывается и создаётся заново при следующем вызове: состояния у мода нет (Р3). Сбои
/// считает модуль (<see cref="GameModule.Strikes"/>): перерасход времени, ловушка, неверный буфер команд; после трёх
/// мод выключен — его системы и обработчики молчат.
/// </summary>
public sealed class HostMod : IDisposable
{
    private readonly Host _host;
    private GameModuleInstance? _instance;

    internal HostMod(Host host, ModPackage package, GameModule module)
    {
        _host = host;
        Package = package;
        Module = module;
        _instance = Create();
        Manifest = _instance.Manifest;
        Layouts = _instance.Systems;
    }

    public string Name => Package.Name;
    public ModPackage Package { get; }
    public GameModule Module { get; }

    /// <summary>Что мод объявил в kh_load.</summary>
    public ModManifest Manifest { get; }

    /// <summary>Раскладки кадров систем мода; номер — id системы.</summary>
    public IReadOnlyList<ModSystemLayout> Layouts { get; }

    /// <summary>Сущность памяти мода (компонент Storage); 0 — нет.</summary>
    public uint StorageEntity { get; internal set; }

    /// <summary>Выключен после трёх сбоев.</summary>
    public bool Disabled => Module.Disabled;

    /// <summary>Сколько раз экземпляр создавался заново после ловушки.</summary>
    public int Restarts { get; private set; }

    /// <summary>Годный экземпляр; мод выключен или не поднимается — null.</summary>
    internal GameModuleInstance? Acquire()
    {
        if (Module.Disabled)
            return null;
        if (_instance is { Faulted: false })
            return _instance;
        _instance?.Dispose();
        _instance = null;
        try
        {
            _instance = Create();
            Restarts++;
            return _instance;
        }
        catch (ModException error)
        {
            Module.Strike();
            _host.Report(this, "экземпляр", error.Message, error);
            return null;
        }
    }

    private GameModuleInstance Create()
    {
        var instance = Module.CreateInstance();
        instance.RandomSource = _host.NextRandom;
        instance.Tools = (id, input, output) => _host.Tools.Call(this, id, input, output);
        return instance;
    }

    /// <summary>Тик, длина тика и следующий номер сущности — для общего блока мода.</summary>
    internal static ModCall Call(Context ctx) => new(ctx.Tick, ctx.Dt, ctx.World.NextEntity);

    public void Dispose()
    {
        _instance?.Dispose();
        _instance = null;
        Module.Dispose();
    }

    public override string ToString() => Name;
}

/// <summary>
/// Система мода в порядке тика: столбцы запроса (QueryColumns.Data) — в кадр мода одним копированием, kh_system,
/// столбцы компонентов из Write — обратно, буфер команд — в команды движка (проверит и применит Scheduler).
/// </summary>
internal sealed class ModSystem(HostMod mod, int id) : ISystem
{
    public void Run(SystemContext ctx)
    {
        if (mod.Acquire() is not { } instance)
            return;
        var n = ctx.Count;
        var layout = instance.Systems[id];
        var data = ctx.Columns.Data;
        var frame = instance.PrepareSystem(id, n);
        data.CopyTo(frame.Data);
        ctx.Entities.CopyTo(frame.Entities);
        var commands = instance.InvokeSystem(id, n, HostMod.Call(ctx));
        ctx.Commands.Append(commands);
        var back = instance.Frame(id, n).Data;
        foreach (var (first, count) in layout.Write)
            back.Slice(first * n, count * n).CopyTo(data.Slice(first * n, count * n));
    }
}
