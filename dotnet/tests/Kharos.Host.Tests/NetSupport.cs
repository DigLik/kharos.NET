using Kharos.Base.Scenario;
using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Host.Tests;

/// <summary>
/// Клиент на конце транспорта для тестов сети (net.test.ts): реплика, список игроков и всё, что пришло вне мира.
/// Вход — по токену и нику (<see cref="Join"/>) или по номеру игрока (<see cref="Connect"/>).
/// </summary>
internal sealed class NetPeer
{
    private readonly HostSession _session;

    private NetPeer(HostSession session, Func<ITransport, Connection> enter)
    {
        _session = session;
        (var hostEnd, End) = MemoryTransport.Pair();
        Connection = enter(hostEnd);
        Pump();
    }

    public static NetPeer Join(HostSession session, string? token = null, string? name = null) =>
        new(session, transport => session.Join(transport, token, name));

    public static NetPeer Connect(HostSession session, uint player) =>
        new(session, transport => session.Connect(transport, player));

    public MemoryTransport End { get; }
    public Connection Connection { get; }
    public Replica Replica { get; } = new();
    public List<PlayerInfo> Players { get; private set; } = [];
    public List<string> Refused { get; } = [];

    /// <summary>Вызывается после каждого применённого сообщения мира (его сдвиги — в <see cref="Replica.Moves"/>).</summary>
    public Action<Replica, MessageType>? Applied { get; set; }

    public uint Player => Connection.Player;
    public string Token => Replica.Welcome!.Token;
    public GameWorld World => Replica.World!;

    public void Pump()
    {
        while (End.TryReceive(out var message))
        {
            var type = Replica.Apply(message);
            Applied?.Invoke(Replica, type);
        }
        foreach (var message in Replica.Inbox)
            switch (Protocol.Messages.TypeOf(message))
            {
                case MessageType.Players:
                    Players = Protocol.Messages.ReadPlayers(message);
                    break;
                case MessageType.Refused:
                    Refused.Add(Protocol.Messages.ReadRefused(message).Reason);
                    break;
            }
        Replica.Inbox.Clear();
    }

    /// <summary>Приказ по сети: хост принимает его с правами «клиент по сети».</summary>
    public void Order(string name, byte[] payload) => End.Send(DeltaCodec.Order(name, payload));

    public bool Has(uint entity, string component) => World.Alive(entity) && World.Has(entity, Replica.Schema!.Id(component));

    public double Number(uint entity, string component, string field)
    {
        var id = Replica.Schema!.Id(component);
        return World.Number(entity, id, Replica.Schema.Get(component).FieldIndex(field));
    }

    /// <summary>Сущности с компонентом у реплики, по возрастанию.</summary>
    public List<uint> With(string component)
    {
        var list = new List<uint>();
        World.Query([Replica.Schema!.Id(component)], list);
        list.Sort();
        return list;
    }

    /// <summary>Юниты игрока (компонент Unit, Owner.player) в мире реплики, по возрастанию.</summary>
    public List<uint> Units(uint player)
    {
        var list = new List<uint>();
        World.Query([Replica.Schema!.Id("Unit"), Builtin.Owner], list);
        list.RemoveAll(entity => World.Number(entity, Builtin.Owner, 0) != player);
        list.Sort();
        return list;
    }

    /// <summary>Кредиты своего счёта (Player.credits), который хост шлёт хозяину; нет счёта — NaN.</summary>
    public double Credits(uint player)
    {
        foreach (var entity in With("Player"))
            if (Number(entity, "Player", "id") == player)
                return Number(entity, "Player", "credits");
        return double.NaN;
    }
}

/// <summary>Партия base с хостом сессии и подключёнными клиентами: Sim ставит сцену, сессия ведёт игроков и тики.</summary>
internal sealed class BaseNet : IDisposable
{
    private readonly List<NetPeer> _peers = [];

    private BaseNet(Sim sim, HostSessionOptions? options)
    {
        Sim = sim;
        Session = new HostSession(sim.Host, options ?? new HostSessionOptions { SendRate = 0 });
    }

    public Sim Sim { get; }
    public HostSession Session { get; }

    /// <summary>Партия: размер, туман и правила — настройки партии; игроков нет — их вводят подключения.</summary>
    public static BaseNet Create(string game = "skirmish", int? size = 256, bool? fog = null, object? rules = null, HostSessionOptions? options = null)
    {
        Assert.SkipWhen(Sim.Unavailable(Sim.DefaultMode) is { } reason, Sim.Unavailable(Sim.DefaultMode) ?? "");
        return new BaseNet(Sim.Create(new SimOptions { Game = game, Size = size, Land = "default", Rules = rules, Fog = fog, Players = [] }), options);
    }

    public NetPeer Join(string? token = null, string? name = null)
    {
        var peer = NetPeer.Join(Session, token, name);
        _peers.Add(peer);
        return peer;
    }

    public NetPeer Connect(uint player)
    {
        var peer = NetPeer.Connect(Session, player);
        _peers.Add(peer);
        return peer;
    }

    public void Leave(NetPeer peer)
    {
        Session.Disconnect(peer.Connection);
        _peers.Remove(peer);
    }

    /// <summary>Тики хоста; клиенты получают своё.</summary>
    public void Step(int ticks = 1)
    {
        for (var tick = 0; tick < ticks; tick++)
        {
            Session.Step();
            foreach (var peer in _peers)
                peer.Pump();
        }
    }

    /// <summary>Вид юнита или здания по имени записи данных.</summary>
    public string Kind(uint entity) => Sim.Host.Content.Protos[(int)Sim.Num(entity, "Proto.id")].Name;

    /// <summary>Юниты игрока на хосте.</summary>
    public List<uint> HostUnits(uint player) => Sim.Of(player, "Unit");

    public void Dispose() => Sim.Dispose();
}
