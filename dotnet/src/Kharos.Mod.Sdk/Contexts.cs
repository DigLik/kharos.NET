using System.Diagnostics.CodeAnalysis;
using Kharos.Mod.Abi;
using MemoryPack;

namespace Kharos.Mod.Sdk;

/// <summary>Загрузка мода: сторона, зерно, схема мира и данные мода (data/*.json после патчей).</summary>
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

    /// <summary>Компонент схемы по имени — для команд.</summary>
    public ComponentRef Component(string name) => _schema.Get(name);

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

/// <summary>Обработчик события.</summary>
public delegate void EventHandler(EventContext ctx);

/// <summary>Обработчик приказа игрока.</summary>
public delegate void OrderHandler(OrderContext ctx);

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
}

/// <summary>Что видит обработчик приказа. Проверять аргументы и права — дело обработчика: приходит что угодно.</summary>
public readonly ref struct OrderContext
{
    internal OrderContext(string name, uint player, ReadOnlySpan<byte> payload, ulong tick, double dt, CommandWriter commands)
    {
        Name = name;
        Player = player;
        Payload = payload;
        Tick = tick;
        Dt = dt;
        Commands = commands;
    }

    public string Name { get; }

    /// <summary>Игрок, отдавший приказ.</summary>
    public uint Player { get; }

    public ReadOnlySpan<byte> Payload { get; }
    public ulong Tick { get; }
    public double Dt { get; }
    public CommandWriter Commands { get; }

    /// <summary>Payload, разобранный MemoryPack.</summary>
    public T? Read<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>() => MemoryPackSerializer.Deserialize<T>(Payload);

    public double Random() => Host.Random();
}

/// <summary>Объявления мода: системы, обработчики событий, приказы. Даётся в <see cref="Mod.Setup"/>.</summary>
public sealed class ModBuilder
{
    internal List<SystemDeclaration> Systems { get; } = [];
    internal List<(string Name, EventHandler Handler)> Events { get; } = [];
    internal List<(string Name, OrderHandler Handler)> Orders { get; } = [];

    /// <summary>Система под именем name: дальше цепочкой — Query, Write, Where, After…</summary>
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

    internal ModManifest ToManifest() => new()
    {
        Abi = ModAbi.Version,
        Systems = [.. Systems.Select(s => s.ToInfo())],
        Events = [.. Events.Select(e => e.Name)],
        Orders = [.. Orders.Select(o => o.Name)],
    };
}

/// <summary>
/// Мод на хосте. Наследник объявляет системы и обработчики в <see cref="Setup"/>, берёт данные и компоненты в
/// <see cref="Load"/>. Класс мода называется в проекте свойством <c>KharosModClass</c>: экспорты ABI SDK пишет сам.
/// </summary>
public abstract class Mod
{
    /// <summary>Объявления: системы, события, приказы. Не зависит от данных.</summary>
    public abstract void Setup(ModBuilder mod);

    /// <summary>Данные мода и схема мира — раз на экземпляр, после <see cref="Setup"/>.</summary>
    public virtual void Load(LoadContext ctx)
    {
    }
}
