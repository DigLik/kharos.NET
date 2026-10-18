using System.Buffers.Binary;
using System.Text;
using Kharos.Content;
using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Host.Tests;

/// <summary>
/// Партия для тестов обзора и видимости (Д4): мод без кода с компонентами как в base (схема по разделам, атрибуты
/// @net и @private), поле 2·size по стороне, туман и память о зданиях (remember: Building). Сущности ставятся прямо в
/// мир хоста, как в тестах TS; клиенты — реплики через <see cref="HostSession"/> без предела байт.
/// </summary>
internal sealed class Arena : IDisposable
{
    private static readonly (string Name, string Json)[] Schema =
    [
        ("kernel", """
            {
              "Unit": {}, "Building": { "level": 1 },
              "Health": { "value": 1, "max": 1, "hit": -1 },
              "Producer": { "progress": 0, "queue": null, "@net": "owner" },
              "Converting": { "left": 0 },
              "Deploys": { "x": 0, "y": 0, "@net": "owner" }
            }
            """),
        ("logistics", """{ "Stock": { "capacity": 0, "metal": 0, "@net": "owner" } }"""),
        ("construction", """{ "Repair": { "radius": 5, "target": 0, "@net": "owner" } }"""),
        ("mining", """{ "Drop": {} }"""),
        ("combat", """{ "Armed": { "target": 0, "chase": false, "@private": ["target"] }, "Burst": { "radius": 0, "@net": "none" } }"""),
        ("players", """{ "Player": { "id": 0, "credits": 0, "@net": "owner" } }"""),
        ("pipes", """{ "Batch": { "amount": 0 } }"""),
        ("missiles", """
            {
              "Mark": { "kind": "ping", "player": 0 },
              "Scan": { "x": 0, "y": 0, "tick": -1, "wait": 0, "@net": "owner" },
              "Silo": { "start": -1, "toX": 0, "@private": ["toX"] }
            }
            """),
    ];

    private readonly List<Peer> _peers = [];

    public Arena(bool fog = true, int size = 128) : this(Content(fog, size)) { }

    private Arena(GameContent content, Host? host = null)
    {
        GameContent = content;
        Host = host ?? new Host(content, new HostOptions { Seed = 1 });
        if (host is null)
            Host.Start();
        Session = new HostSession(Host, new HostSessionOptions { SendRate = 0, Local = true });
    }

    public GameContent GameContent { get; }
    public Host Host { get; }
    public HostSession Session { get; }
    public GameWorld World => Host.World;

    /// <summary>Содержимое: мод «arena» без кода, партия duel.</summary>
    public static GameContent Content(bool fog = true, int size = 128)
    {
        var manifest = Encoding.UTF8.GetBytes("""{ "name": "arena", "game": "duel" }""");
        var games = Encoding.UTF8.GetBytes($$"""{ "duel": { "size": {{size}}, "fog": {{(fog ? "true" : "false")}}, "remember": ["Building"] } }""");
        return GameContent.Load([new ModPackage("arena", manifest, new Dictionary<string, byte[]> { ["games"] = games },
            schema: Schema.ToDictionary(file => file.Name, file => Encoding.UTF8.GetBytes(file.Json)))]);
    }

    /// <summary>Та же партия из сохранения — новым хостом (перезапуск сервера).</summary>
    public Arena Reload() => new(GameContent, Host.Load(GameContent, Host.Save()));

    public int Id(string component) => World.Schema.Id(component);

    public uint Spawn(uint player, double x, double y, params string[] tags)
    {
        var entity = World.Spawn();
        if (player != 0)
            Set(entity, "Owner", "player", player);
        Set(entity, "Position", "x", x);
        Set(entity, "Position", "y", y);
        foreach (var tag in tags)
            Add(entity, tag);
        return entity;
    }

    /// <summary>Юнит с глазом радиуса sight (0 — без глаза).</summary>
    public uint Unit(uint player, double x, double y, double sight = 7)
    {
        var unit = Spawn(player, x, y, "Unit");
        if (sight > 0)
            Set(unit, "Sight", "radius", sight);
        return unit;
    }

    /// <summary>Здание w × h с левым верхним тайлом (x, y); sight 0 — без глаза.</summary>
    public uint Building(uint player, int x, int y, int w = 2, int h = 2, double sight = 0)
    {
        var building = Spawn(player, x, y, "Building");
        Set(building, "Footprint", "w", w);
        Set(building, "Footprint", "h", h);
        if (sight > 0)
            Set(building, "Sight", "radius", sight);
        return building;
    }

    /// <summary>Сущность счёта: Player, Owner, Team; видна хозяину и союзникам (Sighting allies).</summary>
    public uint Account(uint player, double credits = 0)
    {
        var account = World.Spawn();
        Set(account, "Owner", "player", player);
        Set(account, "Player", "id", player);
        Set(account, "Player", "credits", credits);
        Set(account, "Team", "player", player);
        Set(account, "Team", "head", -1);
        Set(account, "Sighting", "rule", Sightings.Allies);
        return account;
    }

    /// <summary>Союз игроков: главой — первый (Team.head у всех одинаковый).</summary>
    public void Ally(params uint[] players)
    {
        foreach (var player in players)
            Set(AccountOf(player), "Team", "head", players[0]);
    }

    /// <summary>Игрок вне союза.</summary>
    public void Alone(uint player) => Set(AccountOf(player), "Team", "head", -1);

    public uint AccountOf(uint player)
    {
        var store = World.Store(Builtin.Team);
        for (var row = 0; row < store.Count; row++)
            if (store.Column(0)[row] == player)
                return store.Entities[row];
        throw new InvalidOperationException($"нет счёта игрока {player}");
    }

    public void Add(uint entity, string component)
    {
        if (!World.Has(entity, Id(component)))
            World.Add(entity, Id(component));
    }

    public void Set(uint entity, string component, string field, double value)
    {
        Add(entity, component);
        World.SetNumber(entity, Id(component), World.Schema.Get(component).FieldIndex(field), value);
    }

    public void SetBytes(uint entity, string component, string field, byte[] value)
    {
        Add(entity, component);
        World.SetBytes(entity, Id(component), World.Schema.Get(component).FieldIndex(field), value);
    }

    public double Number(uint entity, string component, string field) => World.Number(entity, Id(component), World.Schema.Get(component).FieldIndex(field));

    /// <summary>Игроки, перечисленные байтами u32 подряд (Sighting.viewers).</summary>
    public static byte[] Players(params uint[] players)
    {
        var bytes = new byte[players.Length * 4];
        for (var i = 0; i < players.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), players[i]);
        return bytes;
    }

    /// <summary>Клиент игрока: приветствие, снимок — и дальше дельты на каждом шаге.</summary>
    public Peer Join(uint player)
    {
        var (hostEnd, clientEnd) = MemoryTransport.Pair();
        var peer = new Peer(Session.Connect(hostEnd, player), clientEnd, World.Schema);
        peer.Pump();
        _peers.Add(peer);
        return peer;
    }

    /// <summary>Подключение закрыто (игрок вышел).</summary>
    public void Leave(Peer peer)
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

    /// <summary>Сколько тиков нужно, чтобы обзор и память наверняка пересчитались.</summary>
    public void Settle() => Step(Vision.Ticks + 1);

    public VisionView View(uint player) => new(Host, player);

    public void Dispose() => Host.Dispose();
}

/// <summary>Клиент: реплика и сигналы, что до него дошли.</summary>
internal sealed class Peer(Connection connection, MemoryTransport end, Schema schema)
{
    public Connection Connection { get; } = connection;
    public Replica Replica { get; } = new();
    public List<string> Signals { get; } = [];
    public GameWorld World => Replica.World!;

    public void Pump()
    {
        Replica.Pump(end);
        Replica.Inbox.Clear();
        var signals = new List<ReplicaSignal>();
        Replica.TakeSignals(signals);
        Signals.AddRange(signals.Select(signal => signal.Signal.Name));
    }

    public bool Has(uint entity) => World.Alive(entity);

    public bool Has(uint entity, string component) => World.Alive(entity) && World.Has(entity, schema.Id(component));

    public double Number(uint entity, string component, string field) =>
        World.Number(entity, schema.Id(component), schema.Get(component).FieldIndex(field));

    /// <summary>Сущности реплики с компонентом, по возрастанию.</summary>
    public List<uint> With(string component)
    {
        var list = new List<uint>();
        World.Query([schema.Id(component)], list);
        list.Sort();
        return list;
    }

    /// <summary>Видит ли клиент тайл (слой «sight»).</summary>
    public bool Sees(double x, double y) => Replica.Sees((int)Math.Floor(x), (int)Math.Floor(y));
}
