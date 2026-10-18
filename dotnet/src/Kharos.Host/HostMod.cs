using Kharos.Content;
using Kharos.Engine.World;
using Kharos.Mod.Abi;
using Kharos.Plugins.Game;

namespace Kharos.Host;

/// <summary>
/// Мод на хосте: пакет, модуль (wasm или сборка .NET — <see cref="HostOptions.ModMode"/>) и один его экземпляр
/// (docs/dotnet.md, Р8: пул — только для параллельных систем). Экземпляр после ловушки выбрасывается и создаётся заново
/// при следующем вызове: состояния у мода нет (Р3). Сбои считает модуль (<see cref="IModModule.Strikes"/>): перерасход
/// времени, ловушка, неверный буфер команд; после трёх мод выключен — его системы и обработчики молчат.
/// </summary>
public sealed class HostMod : IDisposable
{
    private readonly Host _host;
    private IModInstance? _instance;

    internal HostMod(Host host, ModPackage package, IModModule module)
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
    public IModModule Module { get; }

    /// <summary>Как исполняется код мода.</summary>
    public ModMode Mode => Module is ClrModule ? ModMode.Clr : ModMode.Wasm;

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

    /// <summary>Линейная память экземпляра wasm, байт; в режиме CLR — null.</summary>
    public long? MemoryBytes => (_instance as GameModuleInstance)?.MemoryBytes;

    /// <summary>Запуск экземпляра (рантайм мода и kh_load), мс.</summary>
    public double StartupMs => _instance?.StartupMs ?? 0;

    /// <summary>Годный экземпляр; мод выключен или не поднимается — null.</summary>
    internal IModInstance? Acquire()
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

    private IModInstance Create()
    {
        var instance = Module.CreateInstance();
        instance.RandomSource = _host.NextRandom;
        instance.Tools = (id, input, output) => _host.Tools.Call(this, id, input, output);
        return instance;
    }

    /// <summary>Тик, длина тика и следующий номер сущности — для общего блока мода.</summary>
    internal static ModCall Call(Context ctx) => new(ctx.Tick, ctx.Dt, ctx.World.NextEntity);

    /// <summary>То же с правами приказа.</summary>
    internal static ModCall Call(OrderContext ctx) => new(ctx.Tick, ctx.Dt, ctx.World.NextEntity, (uint)ctx.Rights);

    public void Dispose()
    {
        _instance?.Dispose();
        _instance = null;
        Module.Dispose();
    }

    public override string ToString() => Name;
}

/// <summary>
/// Система мода в порядке тика: столбцы запроса и побочных таблиц (QueryColumns.Data) — в кадр мода одним копированием
/// на часть, kh_system, столбцы из Write и пишущих таблиц — обратно, буфер команд — в команды движка (проверит и
/// применит Scheduler).
/// </summary>
internal sealed class ModSystem(HostMod mod, int id) : ISystem
{
    public void Run(SystemContext ctx)
    {
        if (mod.Acquire() is not { } instance)
            return;
        ctx.Commands.Append(instance.RunSystem(id, new Frame(ctx), HostMod.Call(ctx)));
    }

    /// <summary>Кадр движка: часть 0 — запрос системы, t + 1 — таблица t.</summary>
    private sealed class Frame(SystemContext ctx) : IModFrame
    {
        private QueryColumns Part(int part) => part == 0 ? ctx.Columns : ctx.Tables[part - 1];

        public int Count(int part) => Part(part).Count;
        public ReadOnlySpan<uint> Entities(int part) => Part(part).Entities;
        public Span<double> Data(int part) => Part(part).Data;
    }
}
