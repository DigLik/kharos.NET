using Kharos.Protocol;

namespace Kharos.Host;

/// <summary>Подключение к хосту: игрок, транспорт, его фильтр и счёт отправленного.</summary>
public sealed class Connection
{
    internal Connection(uint player, ITransport transport, IViewFilter filter)
    {
        Player = player;
        Transport = transport;
        Filter = filter;
    }

    public uint Player { get; }
    public ITransport Transport { get; }
    public IViewFilter Filter { get; }

    /// <summary>Байт отправлено всего и последней дельтой.</summary>
    public long BytesSent { get; internal set; }
    public int LastDeltaBytes { get; internal set; }
    public long Messages { get; internal set; }

    /// <summary>Отброшенные приказы (не разобраны или не по пределам).</summary>
    public int BadOrders { get; internal set; }
}

/// <summary>
/// Хост и его клиенты: вход (приветствие и снимок), приказы, рассылка дельт после тика. Одна и та же для одиночной игры
/// (<see cref="LocalGame"/>, транспорт в памяти) и сервера (Н4). Вызовы — из потока хоста.
/// </summary>
public sealed class HostSession(Host host)
{
    /// <summary>Пределы приказа: имя и payload.</summary>
    public const int MaxOrderName = 64, MaxOrderPayload = 64 * 1024;

    private readonly List<Connection> _connections = [];
    private readonly WireWriter _scratch = new(64 * 1024);

    public Host Host { get; } = host;
    public IReadOnlyList<Connection> Connections => _connections;

    /// <summary>Фильтр зрителя по игроку: сейчас всё видно (Н4 — по обзору).</summary>
    public Func<uint, IViewFilter> Filter { get; set; } = player => new FullView(player);

    /// <summary>Последняя разосланная дельта (для замеров и тестов).</summary>
    public WorldDelta? LastDelta { get; private set; }

    /// <summary>
    /// Подключение игрока: накопленное уходит прежним клиентам, игрок входит (если ещё не входил), его изменения — тоже
    /// прежним, затем новому — приветствие и снимок. Дальше он получает дельты с прочими.
    /// </summary>
    public Connection Connect(ITransport transport, uint player)
    {
        Broadcast();
        if (Host.Join(player))
            Broadcast();
        var connection = new Connection(player, transport, Filter(player));
        _connections.Add(connection);
        Send(connection, Host.Welcome(player).Encode());
        Send(connection, DeltaCodec.Snapshot(Host.Tick, Host.World, Host.Layers, connection.Filter));
        return connection;
    }

    public void Disconnect(Connection connection)
    {
        if (_connections.Remove(connection))
            connection.Transport.Dispose();
    }

    /// <summary>Тик: приказы клиентов, шаг хоста, рассылка.</summary>
    public void Step()
    {
        PumpOrders();
        Host.Step();
        Broadcast();
    }

    /// <summary>Приказы клиентов — хосту (выполнятся в начале тика); закрытые транспорты — прочь.</summary>
    public void PumpOrders()
    {
        for (var at = _connections.Count - 1; at >= 0; at--)
        {
            var connection = _connections[at];
            while (connection.Transport.TryReceive(out var message))
            {
                try
                {
                    var (name, payload) = DeltaCodec.ReadOrder(message);
                    if (name.Length == 0 || name.Length > MaxOrderName || payload.Length > MaxOrderPayload)
                        throw new FormatException($"приказ {name}: не по пределам");
                    Host.Send(connection.Player, name, payload);
                }
                catch (FormatException error)
                {
                    connection.BadOrders++;
                    Host.Report(null, $"игрок {connection.Player}", error.Message, error);
                }
            }
            if (connection.Transport.Closed)
                _connections.RemoveAt(at);
        }
    }

    /// <summary>Изменения с прошлой рассылки — каждому клиенту через его фильтр.</summary>
    public void Broadcast()
    {
        var delta = Host.TakeDelta();
        LastDelta = delta;
        if (delta.IsEmpty && _connections.Count == 0)
            return;
        foreach (var connection in _connections)
        {
            var bytes = DeltaCodec.Encode(delta, Host.World, Host.Layers, connection.Filter, _scratch);
            connection.LastDeltaBytes = bytes.Length;
            Send(connection, bytes);
        }
    }

    private static void Send(Connection connection, byte[] message)
    {
        connection.Transport.Send(message);
        connection.BytesSent += message.Length;
        connection.Messages++;
    }
}
