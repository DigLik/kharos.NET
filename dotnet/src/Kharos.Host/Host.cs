using System.Buffers.Binary;
using System.Text;
using Kharos.Content;
using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;
using Kharos.Mod.Abi;
using Kharos.Plugins.Game;
using Kharos.Protocol;

namespace Kharos.Host;

/// <summary>Настройки хоста.</summary>
public sealed class HostOptions
{
    /// <summary>Партия (запись data/games.json); null — по умолчанию из mod.json.</summary>
    public string? Game { get; init; }

    /// <summary>Зерно мира: случайный ряд систем и модов.</summary>
    public ulong Seed { get; init; } = 1;

    /// <summary>Длина тика, секунды (20 Гц).</summary>
    public double Dt { get; init; } = 0.05;

    /// <summary>Пределы модов (память, время, сбои) для своего <see cref="GameModuleHost"/>.</summary>
    public GameModuleOptions Modules { get; init; } = new();

    /// <summary>Общий хозяин модов (сервер с несколькими партиями); null — хост заводит свой и сам его закрывает.</summary>
    public GameModuleHost? ModuleHost { get; init; }

    /// <summary>Журнал хоста: ошибки систем и модов, выключение модов.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>Ошибка на хосте: тик, мод (если он виноват), где и что.</summary>
public sealed record HostError(ulong Tick, string? Mod, string Source, string Message, Exception? Exception = null);

/// <summary>Хост не собрался: моды, схема, порядок систем.</summary>
public sealed class HostException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Хост (docs/dotnet.md §3, crates/world/src/host.rs): мир по схеме модов, порядок систем, моды хоста в wasm,
/// инструменты движка, обзор и слои. Тик: приказы игроков → системы модов и движка → сигналы. События движка:
/// <c>start</c> (payload — имя партии, UTF-8), <c>player_joined</c> (payload — игрок, u32), <c>spawned</c> (entity:u32,
/// proto:f64). Вызовы — из одного потока.
/// </summary>
public sealed class Host : IDisposable
{
    /// <summary>Событие начала партии: payload — имя партии (UTF-8).</summary>
    public const string StartEvent = "start";

    /// <summary>Событие входа игрока: payload — номер игрока, u32.</summary>
    public const string JoinEvent = "player_joined";

    /// <summary>Система обзора в порядке тика: на неё можно ссылаться в after/before.</summary>
    public const string VisionSystem = "engine.vision";

    private const int MaxErrors = 1000;

    private readonly List<HostMod> _mods = [];
    private readonly HashSet<HostMod> _disabledReported = [];
    private readonly Dictionary<string, HostMod> _systemOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HostMod> _byName = new(StringComparer.Ordinal);
    private readonly SortedSet<uint> _players = [];
    private readonly List<Signal> _signals = [];
    private readonly WorldChanges _changes = new();
    private readonly GameModuleHost _moduleHost;
    private readonly bool _ownsModuleHost;
    private readonly Action<string>? _log;
    private int _signalsTaken;
    private int _stringsSent;
    private bool _disposed;

    public Host(GameContent content, HostOptions? options = null)
    {
        options ??= new HostOptions();
        Content = content;
        Seed = options.Seed;
        Dt = options.Dt;
        _log = options.Log;
        try
        {
            Game = content.Game(options.Game);
        }
        catch (ContentException error)
        {
            throw new HostException(error.Message, error);
        }

        var schema = new Schema();
        try
        {
            content.DeclareComponents(schema);
        }
        catch (ContentException error)
        {
            throw new HostException(error.Message, error);
        }
        schema.DeclareString(ModAbi.StorageComponent, "mod", "");
        schema.DeclareBytes(ModAbi.StorageComponent, "data");
        StorageComponent = schema.Id(ModAbi.StorageComponent);
        StorageData = schema[StorageComponent].FieldIndex("data");
        World = new GameWorld(schema) { TrackChanges = true };

        var layers = new List<LayerSpec>(content.Layers);
        if (Game.Bounds is { } bounds)
        {
            Map = new MapBounds(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            if (Game.Fog)
                foreach (var spec in Fog.Layers)
                    if (!layers.Any(layer => layer.Name == spec.Name))
                        layers.Add(spec);
            if (Game.Land is { } config)
                Land = Generate(new Land(config, LandArea.Of(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)));
        }
        Layers = new LayerSet(layers);
        if (Map is { } map && Game.Fog)
            Vision = new Vision(map, Layers);
        Tools = new EngineTools(this);

        _moduleHost = options.ModuleHost ?? new GameModuleHost(options.Modules);
        _ownsModuleHost = options.ModuleHost is null;
        var specs = new List<SystemSpec>();
        try
        {
            var load = LoadInfo();
            foreach (var package in content.Mods)
            {
                if (package.HostWasm is not { } wasm)
                    continue;
                var module = _moduleHost.Load(package.Name, wasm, load);
                HostMod mod;
                try
                {
                    mod = new HostMod(this, package, module);
                }
                catch
                {
                    module.Dispose();
                    throw;
                }
                _mods.Add(mod);
                _byName[mod.Name] = mod;
                specs.AddRange(Systems(mod));
            }
            if (Vision is { } vision)
                specs.Add(new SystemSpec { Name = VisionSystem, Key = "vision", System = new DelegateSystem(ctx => { if (ctx.Tick % Kharos.Host.Vision.Ticks == 1) vision.Update(ctx.World); }) });
            Scheduler = new Scheduler(World, specs, Seed);
        }
        catch (Exception error) when (error is ModException or ScheduleException or KeyNotFoundException)
        {
            Dispose();
            throw new HostException(error.Message, error);
        }

        foreach (var mod in _mods)
        {
            for (var id = 0; id < mod.Manifest.Events.Length; id++)
            {
                var handler = id;
                Scheduler.On(mod.Manifest.Events[id], ctx =>
                {
                    if (mod.Acquire() is { } instance)
                        ctx.Commands.Append(instance.RaiseEvent(handler, ctx.Payload, HostMod.Call(ctx)));
                }, mod.Name);
            }
            for (var id = 0; id < mod.Manifest.Orders.Length; id++)
            {
                var order = id;
                Scheduler.OnOrder(mod.Manifest.Orders[id], ctx =>
                {
                    if (mod.Acquire() is { } instance)
                        ctx.Commands.Append(instance.RunOrder(order, ctx.Player, ctx.Payload, HostMod.Call(ctx)));
                }, mod.Name);
            }
            // Память мода — сущность с Storage { mod, data }: сохраняется и ходит по сети, как весь мир (Р3).
            var entity = World.Spawn();
            World.Add(entity, StorageComponent);
            World.Set(entity, StorageComponent, "mod", mod.Name);
            mod.StorageEntity = entity;
        }
    }

    public GameContent Content { get; }
    public GameInfo Game { get; }
    public ulong Seed { get; }
    public double Dt { get; }
    public GameWorld World { get; }
    public Scheduler Scheduler { get; } = null!;
    public LayerSet Layers { get; }

    /// <summary>Карта партии; null — нет.</summary>
    public MapBounds? Map { get; }

    /// <summary>Местность партии; null — нет.</summary>
    public Land? Land { get; }

    /// <summary>Обзор; null — у партии нет карты или тумана.</summary>
    public Vision? Vision { get; }

    public EngineTools Tools { get; }

    /// <summary>Моды с кодом хоста, в порядке загрузки.</summary>
    public IReadOnlyList<HostMod> Mods => _mods;

    /// <summary>Вошедшие игроки.</summary>
    public IReadOnlyCollection<uint> Players => _players;

    public ulong Tick => Scheduler.Tick;
    public bool Started { get; private set; }

    /// <summary>Ошибки систем, обработчиков и инструментов (последние <see cref="MaxErrors"/>).</summary>
    public List<HostError> Errors { get; } = [];

    /// <summary>Номер компонента Storage и поля data.</summary>
    public int StorageComponent { get; }
    public int StorageData { get; }

    public HostMod? Mod(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Память мода (Storage.data).</summary>
    public ReadOnlySpan<byte> Storage(HostMod mod) => mod.StorageEntity == 0 ? [] : World.GetBytes(mod.StorageEntity, StorageComponent, StorageData);

    /// <summary>Начало партии: событие <see cref="StartEvent"/>.</summary>
    public void Start()
    {
        if (Started)
            throw new InvalidOperationException("партия уже идёт");
        Started = true;
        Scheduler.Emit(StartEvent, Encoding.UTF8.GetBytes(Game.Name));
        Settle();
    }

    /// <summary>Игрок вошёл: слои и обзор, событие <see cref="JoinEvent"/>, обзор сразу. Уже вошедший — false.</summary>
    public bool Join(uint player)
    {
        if (player == 0)
            throw new ArgumentOutOfRangeException(nameof(player), "игрок 0 — «ничей»");
        if (!_players.Add(player))
            return false;
        Layers.AddPlayer(player);
        Vision?.AddPlayer(player);
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, player);
        Scheduler.Emit(JoinEvent, payload);
        Vision?.Update(World);
        Settle();
        return true;
    }

    /// <summary>Приказ игрока: выполнится в начале следующего тика.</summary>
    public void Send(uint player, string order, byte[] payload) => Scheduler.Send(player, order, payload);

    /// <summary>Один тик.</summary>
    public void Step()
    {
        CollectSignals();
        Scheduler.Step(Dt);
        _signalsTaken = 0;
        Settle();
    }

    /// <summary>Изменения с прошлого раза (мир, слои, сигналы) — для рассылки. Брать после каждого тика.</summary>
    public WorldDelta TakeDelta()
    {
        CollectSignals();
        World.TakeChanges(_changes);
        var delta = WorldDelta.Build(World, _changes, _stringsSent, Layers.TakeChanges(), _signals, Tick);
        _stringsSent = World.Strings.Count;
        _signals.Clear();
        return delta;
    }

    /// <summary>
    /// Собирает все чанки карты заранее: пределы вызовов мода идут по часам, и холодная генерация внутри инструмента
    /// (поиск пути, проходимость) засчиталась бы моду.
    /// </summary>
    private static Land Generate(Land land)
    {
        if (land.Area is { } area)
            for (var y = 0; y < area.Height; y++)
                for (var x = 0; x < area.Width; x++)
                    land.GetChunk(area.Left + x, area.Top + y);
        return land;
    }

    /// <summary>Приветствие игроку.</summary>
    public Welcome Welcome(uint player) => new()
    {
        Player = player,
        Game = Game.Name,
        Seed = Seed,
        Dt = Dt,
        Tick = Tick,
        Checksum = Content.Checksum,
        Map = Map,
        Fog = Vision is not null,
        Land = Land?.Config,
        Components = Protocol.Welcome.Describe(World.Schema),
        Layers = [.. Layers.Specs],
    };

    /// <summary>Следующее случайное мира (kh.random модов).</summary>
    internal double NextRandom() => Scheduler?.NextRandom() ?? 0;

    /// <summary>Ошибка мода или движка — в журнал.</summary>
    internal void Report(HostMod? mod, string source, string message, Exception? exception)
    {
        if (Errors.Count >= MaxErrors)
            Errors.RemoveAt(0);
        Errors.Add(new HostError(Scheduler?.Tick ?? 0, mod?.Name, source, message, exception));
        _log?.Invoke(mod is null ? $"{source}: {message}" : $"мод {mod.Name}, {source}: {message}");
    }

    /// <summary>Сигналы, пришедшие с прошлого раза, — в очередь рассылки.</summary>
    private void CollectSignals()
    {
        var signals = Scheduler.Signals;
        for (var at = _signalsTaken; at < signals.Count; at++)
            _signals.Add(signals[at]);
        _signalsTaken = signals.Count;
    }

    /// <summary>Ошибки планировщика — в журнал; неверный буфер команд и ловушка — сбой мода.</summary>
    private void Settle()
    {
        CollectSignals();
        foreach (var error in Scheduler.Errors)
        {
            var mod = Owner(error.Source);
            if (mod is not null && error.Exception is CommandException or ModTrapException { Timeout: false })
                mod.Module.Strike();
            Report(mod, error.Source, error.Message, error.Exception);
        }
        Scheduler.Errors.Clear();
        foreach (var mod in _mods)
            if (mod.Disabled && !_disabledReported.Contains(mod))
            {
                _disabledReported.Add(mod);
                Report(mod, "сбои", $"выключен после {mod.Module.Strikes} сбоев", null);
            }
    }

    /// <summary>Мод по источнику ошибки: имя системы или «мод: событие …».</summary>
    private HostMod? Owner(string source)
    {
        if (_systemOwners.TryGetValue(source, out var mod))
            return mod;
        var colon = source.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0 ? _byName.GetValueOrDefault(source[..colon]) : null;
    }

    /// <summary>Системы мода по манифесту: раскладка кадра мода должна совпасть с запросом движка.</summary>
    private IEnumerable<SystemSpec> Systems(HostMod mod)
    {
        var schema = World.Schema;
        for (var id = 0; id < mod.Manifest.Systems.Length; id++)
        {
            var info = mod.Manifest.Systems[id];
            var width = info.Query.Sum(name => schema.Find(name) is { } component ? schema[component].Columns : throw new HostException($"{mod.Name}, {info.Name}: нет компонента {name}"));
            if (width != mod.Layouts[id].Width)
                throw new HostException($"{mod.Name}, {info.Name}: ширина кадра мода не совпала со схемой");
            if (!_systemOwners.TryAdd(info.Name, mod))
                throw new HostException($"{mod.Name}: система {info.Name} уже есть у {_systemOwners[info.Name].Name}");
            yield return new SystemSpec
            {
                Name = info.Name,
                Key = info.Key ?? info.Name[(info.Name.LastIndexOf('.') + 1)..],
                Query = info.Query,
                Where = [.. info.Where.Select(where => (where.Component, where.Field, where.Kind switch
                {
                    ModAbi.KindFlag => FieldValue.Of(where.Number != 0),
                    ModAbi.KindString => FieldValue.Of(where.Text ?? ""),
                    _ => FieldValue.Of(where.Number),
                }))],
                Write = info.Write,
                Every = info.Every,
                After = info.After,
                Before = info.Before,
                Instead = info.Instead,
                System = new ModSystem(mod, id),
            };
        }
    }

    /// <summary>Что моды получают в kh_load: схема мира, все данные после патчей, партия.</summary>
    private ModLoadInfo LoadInfo() => new()
    {
        Abi = ModAbi.Version,
        Side = "host",
        Seed = Seed,
        Game = Game.Name,
        Components = [.. World.Schema.Components.Select(c => new ModComponentInfo
        {
            Name = c.Name,
            Id = c.Id,
            Fields = [.. c.Fields.Select(f => new ModFieldInfo { Name = f.Name, Kind = (byte)f.Kind, Index = f.Index, Slot = f.Slot })],
        })],
        Data = [.. Content.DataNames.Select(name => new ModDataFile { Name = name, Bytes = Content.DataBytes(name)! })],
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var mod in _mods)
            mod.Dispose();
        _mods.Clear();
        if (_ownsModuleHost)
            _moduleHost.Dispose();
    }
}
