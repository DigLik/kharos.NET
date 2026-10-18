using System.Reflection;
using Kharos.Engine.World;
using MemoryPack;

namespace Kharos.Base.Scenario;

// Приказы игроков и запросы мода: типизированные payload из Base.Shared (имя приказа — константа Name класса).
public sealed partial class Sim
{
    /// <summary>Имя мода base для <see cref="Query{T}"/>.</summary>
    public const string ModName = "base";

    /// <summary>
    /// Приказ игрока (<c>sim.send</c> в TS): имя берётся из константы <c>Name</c> класса payload
    /// (<c>sim.Send(1, new MoveOrder { … })</c>). Выполнится в начале следующего тика. rights — кто отдал: по умолчанию
    /// «изнутри» (как тест TS); <c>OrderRights.Remote</c> — как клиент по сети (цель по номеру отклонят).
    /// </summary>
    public void Send<T>(uint player, T payload, OrderRights rights = OrderRights.None) where T : class
    {
        Send(player, NameOf(typeof(T)), MemoryPackSerializer.Serialize(payload), rights);
    }

    /// <summary>Приказ с готовым payload (MemoryPack): для приказов без класса и ошибочных payload в тестах.</summary>
    public void Send(uint player, string order, byte[] payload, OrderRights rights = OrderRights.None)
    {
        Host.Send(player, order, payload, rights);
        _pending++;
    }

    /// <summary>Приказ без полей (например, <c>edit.addPlayer</c>) — пустой payload.</summary>
    public void Send(uint player, string order, OrderRights rights = OrderRights.None) => Send(player, order, [], rights);

    /// <summary>
    /// Запрос мода (<c>Mod.Query</c>) сразу, между тиками: только чтение. Аргументы — объект MemoryPack или null;
    /// ответ разбирается в <typeparamref name="T"/>. Мод или запроса нет — <see cref="InvalidOperationException"/>.
    /// </summary>
    public T Query<T>(string name, object? args = null, string mod = ModName)
    {
        var bytes = QueryBytes(name, args, mod);
        return MemoryPackSerializer.Deserialize<T>(bytes) ?? throw new InvalidOperationException($"запрос {name}: пустой ответ");
    }

    /// <summary>Запрос мода, ответ — сырые байты.</summary>
    public byte[] QueryBytes(string name, object? args = null, string mod = ModName)
    {
        var payload = args is null ? [] : MemoryPackSerializer.Serialize(args.GetType(), args);
        var reply = Host.Query(mod, name, payload) ?? throw new InvalidOperationException($"нет запроса «{name}» у мода «{mod}»");
        Throw();
        return reply;
    }

    /// <summary>Есть ли у мода обработчик приказа с таким именем.</summary>
    public bool HasOrder(string name) => Host.HasOrder(name);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, string> OrderNameCache = new();

    /// <summary>Имя приказа класса payload — константа <c>Name</c>; считается один раз на тип.</summary>
    private static string NameOf(Type type) => OrderNameCache.GetOrAdd(type, static t =>
        t.GetField("Name", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() as string
        ?? throw new InvalidOperationException($"{t.Name}: нет константы Name — это не приказ из Base.Shared"));
}
