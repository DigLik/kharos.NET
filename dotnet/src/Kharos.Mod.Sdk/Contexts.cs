using System.Diagnostics.CodeAnalysis;
using Kharos.Mod.Abi;
using Kharos.Mod.Sdk.Runtime;
using MemoryPack;

namespace Kharos.Mod.Sdk;

/// <summary>Загрузка мода: сторона, зерно, схема мира, слои, партия и данные мода (data/*.json после патчей).</summary>
public sealed class LoadContext
{
    private readonly ModLoadInfo _info;
    private readonly WorldSchema _schema;

    internal LoadContext(ModLoadInfo info, WorldSchema schema)
    {
        _info = info;
        _schema = schema;
    }

    /// <summary>"host" или "client".</summary>
    public string Side => _info.Side;

    /// <summary>Зерно мира.</summary>
    public ulong Seed => _info.Seed;

    /// <summary>Идущая партия (запись data/games.json); пусто — нет.</summary>
    public string Game => _info.Game;

    /// <summary>
    /// Запись партии (UTF-8 JSON) с настройками хоста поверх (HostOptions.Settings: size, height, land, fog, rules,
    /// weather, dev…); пусто — партии нет.
    /// </summary>
    public ReadOnlySpan<byte> GameData => _info.GameData;

    /// <summary>Компонент схемы по имени — для команд.</summary>
    public ComponentRef Component(string name) => _schema.Get(name);

    /// <summary>Компонент схемы по имени; нет — null.</summary>
    public ComponentRef? FindComponent(string name) => _schema.Find(name);

    /// <summary>Слой мира по имени; нет — null.</summary>
    public LayerRef? Layer(string name) => _schema.Layer(name);

    /// <summary>Байты файла данных (имя без .json); нет — false.</summary>
    public bool TryData(string name, out ReadOnlySpan<byte> bytes)
    {
        foreach (var file in _info.Data)
            if (file.Name == name)
            {
                bytes = file.Bytes;
                return true;
            }
        bytes = default;
        return false;
    }
}

/// <summary>Права приказа или запроса: кто его отдал (как OrderRights движка).</summary>
[Flags]
public enum OrderRights : uint
{
    /// <summary>Изнутри: движок, тесты, консоль сервера.</summary>
    None = 0,
    /// <summary>От клиента по сети: цель по номеру сущности клиент не называет (fromClient в TS).</summary>
    Remote = ModAbi.RightsRemote,
    /// <summary>Отдал администратор.</summary>
    Admin = ModAbi.RightsAdmin,
    /// <summary>Отдал редактор карты.</summary>
    Editor = ModAbi.RightsEditor,
}

/// <summary>Обработчик события.</summary>
public delegate void EventHandler(EventContext ctx);

/// <summary>Обработчик приказа игрока.</summary>
public delegate void OrderHandler(OrderContext ctx);

/// <summary>Обработчик запроса (только чтение мира): ответ — <see cref="QueryContext.Reply(ReadOnlySpan{byte})"/>.</summary>
public delegate void QueryHandler(QueryContext ctx);

/// <summary>Что видит обработчик события.</summary>
public readonly ref struct EventContext
{
    internal EventContext(string name, ReadOnlySpan<byte> payload, ulong tick, double dt, CommandWriter commands)
    {
        Name = name;
        Payload = payload;
        Tick = tick;
        Dt = dt;
        Commands = commands;
    }

    public string Name { get; }

    /// <summary>Байты события: формат знает тот, кто его послал.</summary>
    public ReadOnlySpan<byte> Payload { get; }

    public ulong Tick { get; }
    public double Dt { get; }
    public CommandWriter Commands { get; }

    /// <summary>Payload, разобранный MemoryPack.</summary>
    public T? Read<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>() => MemoryPackSerializer.Deserialize<T>(Payload);

    public double Random() => Host.Random();

    /// <summary>Сцена вызова: мир с записями этого вызова поверх (<see cref="Sdk.Stage"/>).</summary>
    public Stage Stage() => Commands.Stage ??= new Stage(Commands, null, null);
}

/// <summary>Что видит обработчик приказа. Проверять аргументы и права — дело обработчика: приходит что угодно.</summary>
public readonly ref struct OrderContext
{
    internal OrderContext(string name, uint player, OrderRights rights, ReadOnlySpan<byte> payload, ulong tick, double dt, CommandWriter commands)
    {
        Name = name;
        Player = player;
        Rights = rights;
        Payload = payload;
        Tick = tick;
        Dt = dt;
        Commands = commands;
    }

    public string Name { get; }

    /// <summary>Игрок, отдавший приказ.</summary>
    public uint Player { get; }

    /// <summary>Кто отдал: клиент по сети (<see cref="OrderRights.Remote"/>), администратор, редактор.</summary>
    public OrderRights Rights { get; }

    /// <summary>Пришёл от клиента по сети: приказы с целью по номеру сущности так не принимаются.</summary>
    public bool Remote => (Rights & OrderRights.Remote) != 0;

    public ReadOnlySpan<byte> Payload { get; }
    public ulong Tick { get; }
    public double Dt { get; }
    public CommandWriter Commands { get; }

    /// <summary>Payload, разобранный MemoryPack.</summary>
    public T? Read<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>() => MemoryPackSerializer.Deserialize<T>(Payload);

    public double Random() => Host.Random();

    /// <summary>Сцена вызова: мир с записями этого вызова поверх (<see cref="Sdk.Stage"/>).</summary>
    public Stage Stage() => Commands.Stage ??= new Stage(Commands, null, null);
}

/// <summary>
/// Что видит обработчик запроса (Host.Query: тесты, администратор, редактор): payload, права, тик. Мир — только на
/// чтение (инструменты), команд нет; ответ — байтами.
/// </summary>
public readonly ref struct QueryContext
{
    private readonly ReplyBuffer _reply;

    internal QueryContext(string name, OrderRights rights, ReadOnlySpan<byte> payload, ulong tick, double dt, ReplyBuffer reply)
    {
        Name = name;
        Rights = rights;
        Payload = payload;
        Tick = tick;
        Dt = dt;
        _reply = reply;
    }

    public string Name { get; }
    public OrderRights Rights { get; }
    public ReadOnlySpan<byte> Payload { get; }
    public ulong Tick { get; }
    public double Dt { get; }

    /// <summary>Payload, разобранный MemoryPack.</summary>
    public T? Read<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>() => MemoryPackSerializer.Deserialize<T>(Payload);

    /// <summary>Ответ байтами (заменяет прежний).</summary>
    public void Reply(ReadOnlySpan<byte> bytes) => _reply.Set(bytes);

    /// <summary>Ответ, упакованный MemoryPack.</summary>
    public void Reply<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(T value) => _reply.Set(MemoryPackSerializer.Serialize(value));
}

/// <summary>Объявления мода: системы, обработчики событий, приказы, запросы. Даётся в <see cref="Mod.Setup"/>.</summary>
public sealed class ModBuilder
{
    internal List<SystemDeclaration> Systems { get; } = [];
    internal List<(string Name, EventHandler Handler)> Events { get; } = [];
    internal List<(string Name, OrderHandler Handler)> Orders { get; } = [];
    internal List<(string Name, QueryHandler Handler)> Queries { get; } = [];

    /// <summary>Система под именем name: дальше цепочкой — Query, Write, Where, Table, After…</summary>
    public SystemDeclaration System(string name, ISystem system)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(system);
        var declaration = new SystemDeclaration(name, system);
        Systems.Add(declaration);
        return declaration;
    }

    /// <summary>Обработчик события name.</summary>
    public void On(string name, EventHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Events.Add((name, handler));
    }

    /// <summary>Приказ игроков name.</summary>
    public void Order(string name, OrderHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Orders.Add((name, handler));
    }

    /// <summary>Запрос name (Host.Query): только чтение мира, ответ байтами — для тестов, администратора, редактора.</summary>
    public void Query(string name, QueryHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Queries.Add((name, handler));
    }

    internal ModManifest ToManifest() => new()
    {
        Abi = ModAbi.Version,
        Systems = [.. Systems.Select(s => s.ToInfo())],
        Events = [.. Events.Select(e => e.Name)],
        Orders = [.. Orders.Select(o => o.Name)],
        Queries = [.. Queries.Select(q => q.Name)],
    };
}

/// <summary>
/// Мод на хосте. Наследник объявляет системы и обработчики в <see cref="Setup"/>, берёт данные и компоненты в
/// <see cref="Load"/>. Класс мода называется в проекте свойством <c>KharosModClass</c>: экспорты ABI SDK пишет сам.
/// </summary>
public abstract class Mod
{
    /// <summary>Объявления: системы, события, приказы, запросы. Не зависит от данных.</summary>
    public abstract void Setup(ModBuilder mod);

    /// <summary>Данные мода и схема мира — раз на экземпляр, после <see cref="Setup"/>.</summary>
    public virtual void Load(LoadContext ctx)
    {
    }
}
