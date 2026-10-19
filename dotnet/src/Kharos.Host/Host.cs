using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kharos.Content;
using Kharos.Engine.Layers;
using Kharos.Engine.Recall;
using Kharos.Engine.Terrain;
using Kharos.Engine.Traces;
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

    /// <summary>
    /// Настройки партии — объект JSON поверх записи games.json (size, fog, land — имя из lands.json или параметры
    /// генератора объектом; height, rules, weather, dev и прочее — модам в LoadContext.GameData). Хранятся в сохранении;
    /// при загрузке эти, если заданы, сильнее сохранённых.
    /// </summary>
    public JsonElement? Settings { get; init; }

    /// <summary>Как исполняется код модов: host.wasm (игра) или сборка .NET в процессе (тесты, <see cref="ModMode.Clr"/>).</summary>
    public ModMode ModMode { get; init; } = ModMode.Wasm;

    /// <summary>
    /// Режим CLR: сборка мода по имени мода; кого нет — ищется рядом с mod.json (проект *.Host, bin/&lt;конфигурация&gt;/net10.0,
    /// <see cref="ClrModule.Find"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string>? ClrAssemblies { get; init; }

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

    /// <summary>Точность прочих чисел в сети, долей единицы (<see cref="NetFormat"/>); 0 — точно.</summary>
    public double NumberScale { get; init; } = NetFormat.NumberScale;

    /// <summary>Точность отдельных полей: «Компонент.поле» → масштаб (0 — точно), сильнее умолчаний.</summary>
    public IReadOnlyDictionary<string, double>? NetScales { get; init; }

    /// <summary>
    /// Компоненты, по которым зритель помнит ушедшее из виду (здания, месторождения): сущность остаётся у клиента
    /// такой, какой её видели. null — список <c>remember</c> партии (data/games.json), нет и его — ничего.
    /// </summary>
    public IReadOnlyList<string>? Remember { get; init; }
}

/// <summary>Ошибка на хосте: тик, мод (если он виноват), где и что.</summary>
public sealed record HostError(ulong Tick, string? Mod, string Source, string Message, Exception? Exception = null);

/// <summary>Хост не собрался: моды, схема, порядок систем.</summary>
public sealed class HostException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Хост (docs/dotnet.md §3, crates/world/src/host.rs): мир по схеме модов, порядок систем, моды хоста в wasm,
/// инструменты движка, обзор и слои. Тик: приказы игроков → системы модов и движка → сигналы. События движка:
/// <c>start</c> (payload — имя партии, UTF-8), <c>player_joined</c> (игрок впервые, u32), <c>player_online</c> и
/// <c>player_left</c> (игрок вошёл в сеть и вышел из неё, u32), <c>player_respawn</c> (игрок просит начать заново:
/// u32 и u8 — 1, если велел администратор), <c>spawned</c> (entity:u32, proto:f64). Партию можно сохранить
/// (<see cref="Save"/>) и продолжить (<see cref="Load"/>). Вызовы — из одного потока.
/// </summary>
public sealed class Host : IDisposable
{
    /// <summary>Событие начала партии: payload — имя партии (UTF-8).</summary>
    public const string StartEvent = "start";

    /// <summary>Событие первого входа игрока: payload — номер игрока, u32.</summary>
    public const string JoinEvent = "player_joined";

    /// <summary>Игрок в сети (первое подключение): payload — u32.</summary>
    public const string OnlineEvent = "player_online";

    /// <summary>Игрок вышел из сети (закрылось последнее подключение): payload — u32.</summary>
    public const string LeftEvent = "player_left";

    /// <summary>Игрок просит начать заново: payload — u32 игрок и u8 (1 — велел администратор).</summary>
    public const string RespawnEvent = "player_respawn";

    /// <summary>
    /// Сигнал мода игроку, которого он начал заново в ответ на <see cref="RespawnEvent"/> (адресно, SignalTo): хост
    /// закрывает ему туман и забывает его память о зданиях (restart в host.ts: vision.forget, forgetBuildings).
    /// </summary>
    public const string RespawnedSignal = "player_respawned";

    /// <summary>Система обзора в порядке тика: на неё можно ссылаться в after/before (все системы движка — EngineSystems).</summary>
    public const string VisionSystem = "engine.vision";

    private const int MaxErrors = 1000;

    private readonly List<HostMod> _mods = [];
    private readonly HashSet<HostMod> _disabledReported = [];
    private readonly Dictionary<string, HostMod> _systemOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HostMod> _byName = new(StringComparer.Ordinal);
    private readonly SortedSet<uint> _players = [];
    private readonly HashSet<uint> _online = [];
    private readonly int[] _remember;
    private List<HostPack>? _packs;
    private bool _landLoaded, _memoryLoaded;
    private readonly List<Signal> _signals = [];
    private readonly WorldChanges _changes = new();
    private GameModuleHost? _moduleHost;
    private readonly bool _ownsModuleHost;
    private readonly Action<string>? _log;
    private int _signalsTaken;
    private int _stringsSent;
    private bool _disposed;

    public Host(GameContent content, HostOptions? options = null) : this(content, options ?? new HostOptions(), null) { }

    /// <summary>
    /// Хост из сохранения (<see cref="Save"/>): тот же мир, тик, случайный ряд, игроки и память модов; партия, зерно и
    /// длина тика — из сохранения. Другая версия файла — <see cref="SaveVersionException"/>, мусор или моды, с которыми
    /// мир не собирается, — <see cref="HostException"/>.
    /// </summary>
    public static Host Load(GameContent content, ReadOnlySpan<byte> save, HostOptions? options = null)
    {
        HostSave decoded;
        try
        {
            decoded = HostSave.Decode(save);
        }
        catch (SaveVersionException)
        {
            throw;
        }
        catch (FormatException error)
        {
            throw new HostException($"сохранение не читается: {error.Message}", error);
        }
        return new Host(content, options ?? new HostOptions(), decoded);
    }

    private Host(GameContent content, HostOptions options, HostSave? save)
    {
        Content = content;
        Seed = save?.Seed ?? options.Seed;
        Dt = save?.Dt ?? options.Dt;
        _log = options.Log;
        try
        {
            Game = content.Game(save?.Game ?? options.Game);
            var settings = options.Settings ?? (save?.Settings is { Length: > 0 } saved ? JsonDocument.Parse(saved).RootElement.Clone() : null);
            if (settings is { } overrides)
            {
                Game = WithSettings(content, Game, overrides);
                Settings = System.Text.Encoding.UTF8.GetBytes(overrides.GetRawText());
            }
        }
        catch (Exception error) when (error is ContentException or JsonException)
        {
            throw new HostException(error.Message, error);
        }
        ModMode = options.ModMode;

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
        // Память мода — только хосту: в ней чужие тайны (грузы, учёт труб) и меняется она почти каждый тик.
        schema.Attributes(ModAbi.StorageComponent, NetMode.None);
        StorageComponent = schema.Id(ModAbi.StorageComponent);
        StorageData = schema[StorageComponent].FieldIndex("data");
        try
        {
            World = save is null ? new GameWorld(schema) : WorldSave.Load(schema, save.World);
        }
        catch (FormatException error)
        {
            throw new HostException($"сохранение: мир не собирается: {error.Message}", error);
        }
        World.TrackChanges = true;
        Format = NetFormat.Of(schema, options.NumberScale, options.NetScales);
        _remember = [.. RememberNames(options).Select(name => schema.Find(name) ?? -1).Where(id => id >= 0)];

        var layers = new List<LayerSpec>(content.Layers);
        if (Game.Bounds is { } bounds)
        {
            Map = new MapBounds(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            if (Game.Fog)
                foreach (var spec in Fog.Layers)
                    if (!layers.Any(layer => layer.Name == spec.Name))
                        layers.Add(spec);
            if (Game.Land is { } config)
            {
                if (save?.Land is { } packed)
                {
                    try
                    {
                        Land = Land.Unpack(config, packed);
                    }
                    catch (FormatException error)
                    {
                        throw new HostException($"сохранение: карта не читается: {error.Message}", error);
                    }
                    _landLoaded = true;
                }
                else
                {
                    Land = Generate(new Land(config, LandArea.Of(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)));
                }
            }
        }
        Layers = new LayerSet(layers);
        if (Map is { } map && Game.Fog)
            Vision = new Vision(map, Layers);
        // Следы и память игроков (Д3): правила слоёв и журналы правок включаются до первой команды мода.
        Traces = TraceLayer.Create(Layers, () => Scheduler.Tick, Flies(content));
        // Предметные слои по обзору (Д6): месторождения помнят, следы видят только в обзоре.
        if (Vision is not null)
            ItemMemory = ItemMemory.Create(Layers);
        if (Land is not null)
            Land.TrackEdits = true;
        if (Vision is not null && (Land is not null || Layers.Find(PavingMemory.LayerName) is not null))
        {
            Recall = new PlayerMemory(Land, Layers);
            if (Recall.Land is null && Recall.Paving is null)
                Recall = null;
        }
        if (Recall is not null && save?.Recall is { Length: > 0 } memory)
        {
            try
            {
                Recall.Decode(memory);
                _memoryLoaded = true;
            }
            catch (FormatException error)
            {
                throw new HostException($"сохранение: память игроков не читается: {error.Message}", error);
            }
        }
        if (save is not null)
        {
            try
            {
                foreach (var change in LayerCodec.Decode(save.Layers))
                    if (Layers.Find(change.Layer) is not null)
                        Layers.Apply(change);
                foreach (var player in save.ReadPlayers(Roster))
                {
                    _players.Add(player);
                    Layers.AddPlayer(player);
                    Vision?.Restore(player);
                }
                // Память о чужих зданиях (Д4) и видимое сразу: после загрузки видно то, что видят юниты.
                if (save.Buildings is { Length: > 0 } buildings)
                    Buildings = BuildMemory.Load(World, buildings);
                Vision?.Update(World, save.Tick);
            }
            catch (FormatException error)
            {
                throw new HostException($"сохранение не читается: {error.Message}", error);
            }
            if (ItemMemory is { } items && Vision is { } vision)
            {
                try
                {
                    // Память предметов (раздел IMEM); в прежних сохранениях её нет — игрок знает то, что разведал.
                    if (save.Items is { Length: > 0 } known)
                        items.Decode(known);
                    else
                        items.KnowExplored(vision.Players, vision.Explored);
                }
                catch (FormatException error)
                {
                    throw new HostException($"сохранение: память предметов не читается: {error.Message}", error);
                }
            }
        }
        Tools = new EngineTools(this);

        _moduleHost = options.ModuleHost;
        _ownsModuleHost = options.ModuleHost is null;
        var specs = new List<SystemSpec>();
        try
        {
            var load = LoadInfo();
            foreach (var package in content.Mods)
            {
                if (Module(package, options, load) is not { } module)
                    continue;
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
            specs.AddRange(EngineSystems.Create(this));
            Scheduler = new Scheduler(World, specs, Seed) { Layers = Layers, Land = Land };
            if (save is not null)
            {
                Scheduler.Tick = save.Tick;
                Scheduler.RandomState = save.Random;
                // Память хода (Д1): без неё первый тик после загрузки соседи казались бы стоящими.
                if (save.Motion is { Length: > 0 } motion && Tools.Navigation is { } navigation)
                {
                    try
                    {
                        navigation.LoadMotion(motion);
                    }
                    catch (FormatException error)
                    {
                        throw new HostException($"сохранение: память хода не читается: {error.Message}", error);
                    }
                }
            }
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
            // Память мода — сущность с Storage { mod, data }: сохраняется, как весь мир (Р3), но по сети не ходит (@net none).
            var entity = save is null ? 0 : FindStorage(mod.Name);
            if (entity == 0)
            {
                entity = World.Spawn();
                World.Add(entity, StorageComponent);
                World.Set(entity, StorageComponent, "mod", mod.Name);
            }
            mod.StorageEntity = entity;
        }
        if (save is not null)
        {
            Started = true;
            // Загрузка — не изменения: клиенты получат мир снимком.
            World.TakeChanges(_changes);
            Layers.TakeChanges();
            _stringsSent = World.Strings.Count;
        }
    }

    public GameContent Content { get; }

    /// <summary>Партия: запись games.json с настройками поверх (<see cref="HostOptions.Settings"/>).</summary>
    public GameInfo Game { get; }

    /// <summary>Настройки партии (UTF-8 JSON) — как их задали; null — нет. Идут в сохранение.</summary>
    public byte[]? Settings { get; }

    /// <summary>Как исполняется код модов.</summary>
    public ModMode ModMode { get; }
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

    /// <summary>Какими игроки помнят чужие здания (engine.memory); в сохранении.</summary>
    public BuildMemory Buildings { get; } = new();

    /// <summary>Компоненты памяти зрителя (<see cref="HostOptions.Remember"/>) номерами.</summary>
    internal IReadOnlyList<int> RememberComponents => _remember;

    /// <summary>Следы на земле (engine.traces); null — у партии нет слоя «traces».</summary>
    public TraceLayer? Traces { get; }

    /// <summary>Память игроков о земле и покрытии (engine.recallLand, engine.recallPaving); null — нет тумана, земли и покрытия.</summary>
    public PlayerMemory? Recall { get; }

    /// <summary>
    /// Версии предметных слоёв у игроков (месторождения, следы; engine.memory): слой с remember или sight уходит игроку
    /// его версией. null — нет тумана или таких слоёв.
    /// </summary>
    public ItemMemory? ItemMemory { get; }

    public EngineTools Tools { get; }

    /// <summary>Точность чисел в сети.</summary>
    public NetFormat Format { get; }

    /// <summary>Игроки: номера, токены, ники (в сохранении).</summary>
    public PlayerRoster Roster { get; } = new();

    /// <summary>Карта правлена (или пришла правленой из сохранения): клиентам и в сохранение — байтами.</summary>
    public bool LandEdited => _landLoaded || (Land?.Revision ?? 0) > 0;

    /// <summary>Кто сейчас в сети (<see cref="Online"/>).</summary>
    public IReadOnlyCollection<uint> OnlinePlayers => _online;

    /// <summary>Моды с кодом хоста, в порядке загрузки.</summary>
    public IReadOnlyList<HostMod> Mods => _mods;

    /// <summary>Игроки, хоть раз входившие в партию (в сохранении).</summary>
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
        Vision?.Update(World, Tick);
        Settle();
        return true;
    }

    /// <summary>Игрок в сети: событие <see cref="OnlineEvent"/>. Уже был — false.</summary>
    public bool Online(uint player)
    {
        if (!_online.Add(player))
            return false;
        Emit(OnlineEvent, player);
        return true;
    }

    /// <summary>Игрок вышел из сети: событие <see cref="LeftEvent"/>. Не был в сети — false.</summary>
    public bool Offline(uint player)
    {
        if (!_online.Remove(player))
            return false;
        Emit(LeftEvent, player);
        return true;
    }

    /// <summary>Игрок просит начать заново: событие <see cref="RespawnEvent"/>; что делать, решают моды.</summary>
    public void Respawn(uint player, bool forced = false)
    {
        var payload = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, player);
        payload[4] = forced ? (byte)1 : (byte)0;
        var from = Scheduler.Signals.Count;
        Scheduler.Emit(RespawnEvent, payload);
        Settle();
        // Мод начал игрока заново (решает он: проигравшего или по велению администратора) — тот начинает как в новом мире.
        var signals = Scheduler.Signals;
        for (var at = from; at < signals.Count; at++)
            if (signals[at].Name == RespawnedSignal && signals[at].To is { } to && Array.IndexOf(to, player) >= 0)
            {
                Vision?.Forget(player);
                Buildings.Forget(player);
                ItemMemory?.ForgetPlayer(player);
                Vision?.Update(World, Tick);
                break;
            }
    }

    /// <summary>Помнит ли зритель сущность, ушедшую из виду (у неё компонент из <see cref="HostOptions.Remember"/>).</summary>
    public bool Remembered(uint entity)
    {
        foreach (var component in _remember)
            if (World.Has(entity, component))
                return true;
        return false;
    }

    /// <summary>Есть ли у какого-нибудь мода приказ с таким именем.</summary>
    public bool HasOrder(string name) => _mods.Any(mod => !mod.Disabled && mod.Manifest.Orders.Contains(name));

    /// <summary>Сохранение партии (на потоке хоста): мир, слои, игроки, тик и случайный ряд.</summary>
    public byte[] Save() => HostSave.Capture(this).Encode();

    /// <summary>Пакеты модов для клиентов (без кода хоста) — в порядке загрузки; собираются при первом обращении.</summary>
    public IReadOnlyList<HostPack> Packs => _packs ??= [.. Content.Mods.Select(HostPack.Of)];

    /// <summary>Приказ игрока: выполнится в начале следующего тика; rights — кто отдал (клиент по сети, админ, редактор).</summary>
    public void Send(uint player, string order, byte[] payload, OrderRights rights = OrderRights.None) => Scheduler.Send(player, order, payload, rights);

    /// <summary>
    /// Запрос мода (Mod.Query: тесты, администратор, редактор) — сразу, между тиками: только чтение мира, ответ байтами.
    /// Нет мода, запроса или мод выключен — null; ошибка мода — <see cref="ModException"/> (и запись в <see cref="Errors"/>).
    /// </summary>
    public byte[]? Query(string mod, string name, ReadOnlySpan<byte> payload, OrderRights rights = OrderRights.Admin)
    {
        if (Mod(mod) is not { } owner || Array.IndexOf(owner.Manifest.Queries, name) is var id && id < 0 || owner.Acquire() is not { } instance)
            return null;
        try
        {
            return instance.RunQuery(id, payload, new ModCall(Tick, Dt, World.NextEntity, (uint)rights)).ToArray();
        }
        catch (ModException error)
        {
            if (error is ModTrapException { Timeout: false })
                owner.Module.Strike();
            Report(owner, $"запрос {name}", error.Message, error);
            throw;
        }
    }

    /// <summary>
    /// Приказы из очереди сразу, без тика (правки редактора, сцены тестов): тик и время не идут, системы не работают,
    /// обработчики видят dt = 0. Сигналы уходят с ближайшей дельтой.
    /// </summary>
    public void Apply()
    {
        Scheduler.ApplyOrders();
        Settle();
    }

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
        var layers = Layers.TakeChanges();
        // Покрытие при тумане уходит игроку таким, каким он его знает, а не слоем мира.
        if (Recall?.Paving is not null || ItemMemory is not null)
            layers.RemoveAll(change => Fogged(change.Layer));
        var delta = WorldDelta.Build(World, _changes, _stringsSent, layers, _signals, Tick);
        if (Recall is { } memory)
        {
            foreach (var (player, tiles) in memory.TakeLearnedLand())
                delta.LandKnown[player] = tiles;
            foreach (var (player, blocks) in memory.TakeLearnedPaving())
                delta.LayersKnown[player] = blocks;
        }
        if (ItemMemory is { } items)
            foreach (var (player, blocks) in items.TakeLearned())
            {
                if (delta.LayersKnown.TryGetValue(player, out var known))
                    known.AddRange(blocks);
                else
                    delta.LayersKnown[player] = blocks;
            }
        else if (Land is { } land)
        {
            foreach (var edit in land.TakeEdits())
                delta.LandEdits.Add(new LandTile(edit.X, edit.Y, edit.After));
        }
        _stringsSent = World.Strings.Count;
        _signals.Clear();
        return delta;
    }

    /// <summary>
    /// Карта игроку при подключении (<see cref="MessageType.Land"/>): без тумана — сегодняшняя, если её правили; с туманом
    /// — какой её знают все до правок (правки игрока идут следом, <see cref="KnownLand"/>). null — клиент повторит карту
    /// генератором.
    /// </summary>
    public byte[]? LandForJoin()
    {
        if (Land is null)
            return null;
        if (Recall?.Land is not null)
            return Recall.PristineLand() ?? (_landLoaded && !_memoryLoaded ? Land.Pack() : null);
        return LandEdited ? Land.Pack() : null;
    }

    /// <summary>Правки карты, которые игрок уже знает, — поверх <see cref="LandForJoin"/>.</summary>
    public IReadOnlyList<LandTile> KnownLand(uint player) => Recall?.KnownLand(player) ?? [];

    /// <summary>
    /// Слои мира игроку: блоки, что ему уходят, без слоёв, которые при тумане идут иначе (покрытие, предметы по обзору), и
    /// они же, какими игрок их знает. known — блоки игрока из дельты; null — все его знания (подключение).
    /// </summary>
    public List<LayerChange> LayersForPlayer(List<LayerChange> blocks, uint player, IReadOnlyList<LayerChange>? known = null)
    {
        if (Recall?.Paving is null && ItemMemory is null)
            return blocks;
        blocks.RemoveAll(change => Fogged(change.Layer));
        if (known is not null)
        {
            blocks.AddRange(known);
            return blocks;
        }
        if (Recall?.Paving is not null)
            blocks.AddRange(Recall.KnownPaving(player));
        if (ItemMemory is { } items)
            blocks.AddRange(items.Blocks(player));
        return blocks;
    }

    /// <summary>Идёт ли слой игрокам их версиями, а не слоем мира: покрытие при тумане, предметы по обзору.</summary>
    public bool Fogged(string layer) =>
        (Recall?.Paving is not null && layer == PavingMemory.LayerName) || ItemMemory?.Covers(layer) == true;

    /// <summary>Ходовые, которые летают (слой записи moves.json — «air»): колею они не оставляют.</summary>
    private static Func<string, bool> Flies(GameContent content)
    {
        var air = new HashSet<string>(StringComparer.Ordinal);
        if (content.Data("moves") is { ValueKind: JsonValueKind.Object } moves)
            foreach (var chassis in moves.EnumerateObject())
                if (chassis.Value.ValueKind == JsonValueKind.Object && chassis.Value.TryGetProperty("layer", out var layer) && layer.ValueKind == JsonValueKind.String && layer.GetString() == "air")
                    air.Add(chassis.Name);
        return air.Contains;
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
        Token = Roster.Get(player)?.Token ?? "",
        Name = Roster.NameOf(player),
        Mods = [.. Packs.Select(pack => pack.Ref)],
        Game = Game.Name,
        Seed = Seed,
        Dt = Dt,
        Tick = Tick,
        Checksum = Content.Checksum,
        Map = Map,
        Fog = Vision is not null,
        Land = Land?.Config,
        Components = Protocol.Welcome.Describe(World.Schema, Format),
        Layers = [.. Layers.Specs],
    };

    /// <summary>Компоненты памяти зрителя: из настроек или списка remember партии.</summary>
    private IEnumerable<string> RememberNames(HostOptions options)
    {
        if (options.Remember is { } names)
            return names;
        if (Game.Data.ValueKind == JsonValueKind.Object && Game.Data.TryGetProperty("remember", out var list) && list.ValueKind == JsonValueKind.Array)
            return [.. list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)];
        return [];
    }

    /// <summary>Сущность памяти мода в загруженном мире; нет — 0.</summary>
    private uint FindStorage(string mod)
    {
        var field = World.Schema[StorageComponent].FieldIndex("mod");
        foreach (var entity in World.Store(StorageComponent).Entities)
            if (World.Get(entity, StorageComponent, field).Text == mod)
                return entity;
        return 0;
    }

    private void Emit(string name, uint player)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, player);
        Scheduler.Emit(name, payload);
        Settle();
    }

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
            for (var t = 0; t < info.Tables.Length; t++)
                if (info.Tables[t].Query.Sum(name => schema.Find(name) is { } component ? schema[component].Columns : throw new HostException($"{mod.Name}, {info.Name}: нет компонента {name}")) != mod.Layouts[id].Tables[t].Width)
                    throw new HostException($"{mod.Name}, {info.Name}: ширина таблицы {t} не совпала со схемой");
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
                Without = info.Without,
                Tables = [.. info.Tables.Select(table => new TableSpec { Query = table.Query, Without = table.Without, Write = table.Write })],
                Write = info.Write,
                Every = info.Every,
                After = info.After,
                Before = info.Before,
                Instead = info.Instead,
                System = new ModSystem(mod, id),
            };
        }
    }

    /// <summary>Что моды получают в kh_load: схема мира, слои, все данные после патчей, партия с настройками.</summary>
    private ModLoadInfo LoadInfo() => new()
    {
        Abi = ModAbi.Version,
        Side = "host",
        Seed = Seed,
        Game = Game.Name,
        GameData = System.Text.Encoding.UTF8.GetBytes(Game.Data.ValueKind == JsonValueKind.Undefined ? "{}" : Game.Data.GetRawText()),
        Components = [.. World.Schema.Components.Select(c => new ModComponentInfo
        {
            Name = c.Name,
            Id = c.Id,
            Fields = [.. c.Fields.Select(f => new ModFieldInfo
            {
                Name = f.Name,
                Kind = (byte)f.Kind,
                Index = f.Index,
                Slot = f.Slot,
                Default = f.Kind is FieldKind.Number or FieldKind.Flag ? f.Default.Number : 0,
                DefaultText = f.Kind == FieldKind.String ? f.Default.Text : null,
                Private = f.Private,
            })],
            Net = (byte)c.Net,
            Save = c.Save,
        })],
        Layers = [.. Layers.Specs.Select((spec, id) => new ModLayerInfo
        {
            Name = spec.Name,
            Id = id,
            PerPlayer = spec.Scope == LayerScope.Player,
            Cell = spec.Cell,
            Channels = spec.Channels.Count,
            Fields = [.. spec.Fields.Select(field => field.Name)],
        })],
        Data = [.. Content.DataNames.Select(name => new ModDataFile { Name = name, Bytes = Content.DataBytes(name)! })],
    };

    /// <summary>
    /// Модуль мода в нужном режиме; у мода нет кода хоста — null. CLR: сборка из настроек или рядом с mod.json; нет её у
    /// мода с host.wasm — ошибка (мод потерялся бы молча).
    /// </summary>
    private IModModule? Module(ModPackage package, HostOptions options, ModLoadInfo load)
    {
        if (options.ModMode == ModMode.Clr)
        {
            var assembly = options.ClrAssemblies?.GetValueOrDefault(package.Name) ?? ClrModule.Find(package.Directory);
            if (assembly is null)
                return package.HostWasm is null ? null : throw new HostException($"{package.Name}: режим CLR, а сборки мода нет — dotnet build {package.Directory}");
            return new ClrModule(package.Name, assembly, load, options.Modules);
        }
        if (package.HostWasm is not { } wasm)
            return null;
        _moduleHost ??= new GameModuleHost(options.Modules);
        return _moduleHost.Load(package.Name, wasm, load);
    }

    /// <summary>
    /// Запись партии с настройками поверх (JsonPatch): size, fog и land пересчитываются, остальное идёт модам как есть.
    /// land — имя из lands.json или параметры генератора объектом.
    /// </summary>
    private static GameInfo WithSettings(GameContent content, GameInfo game, JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object)
            throw new HostException("настройки партии — объект JSON");
        var record = game.Data.ValueKind == JsonValueKind.Object ? JsonNode.Parse(game.Data.GetRawText())! : new JsonObject();
        var merged = JsonPatch.Merge(record, JsonNode.Parse(settings.GetRawText()))!;
        var data = JsonDocument.Parse(JsonPatch.Write(merged)).RootElement.Clone();
        var size = game.Size;
        var fog = game.Fog;
        var (landName, land) = (game.LandName, game.Land);
        if (settings.TryGetProperty("size", out var sizeValue))
            size = sizeValue.TryGetInt32(out var number) && number > 0 ? number : throw new HostException("настройки: size — целое больше нуля");
        if (settings.TryGetProperty("fog", out var fogValue))
            fog = fogValue.ValueKind is JsonValueKind.True or JsonValueKind.False ? fogValue.GetBoolean() : throw new HostException("настройки: fog — true или false");
        if (settings.TryGetProperty("land", out var landValue))
        {
            if (landValue.ValueKind == JsonValueKind.String)
            {
                landName = landValue.GetString()!;
                land = content.Data(GameContent.LandsFile) is { ValueKind: JsonValueKind.Object } lands && lands.TryGetProperty(landName, out var config)
                    ? GameContent.Generator(config, $"lands.{landName}")
                    : throw new HostException($"настройки: нет местности {landName} в data/{GameContent.LandsFile}.json");
            }
            else
            {
                (landName, land) = (null, GameContent.Generator(landValue, "настройки.land"));
            }
        }
        return new GameInfo(game.Name, size, fog, landName, land, data);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var mod in _mods)
            mod.Dispose();
        _mods.Clear();
        if (_ownsModuleHost)
            _moduleHost?.Dispose();
    }
}
