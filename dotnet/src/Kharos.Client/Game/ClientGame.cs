using System.Diagnostics;
using Kharos.Audio;
using Kharos.Audio.Graph;
using Kharos.Client.Looks;
using Kharos.Client.Net;
using Kharos.Client.Ui;
using Kharos.Content;
using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;
using Kharos.Gpu;
using Kharos.Mod.Abi;
using Kharos.Plugins.Game;
using Kharos.Protocol;
using Gui = Kharos.Client.Ui.Gui;
using RenderFrame = Kharos.Gpu.Frame;

namespace Kharos.Client.Game;

/// <summary>Клиент не собрался или потерял партию: нет хоста, моды разошлись, облик не разобран.</summary>
public sealed class ClientException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Настройки клиента партии.</summary>
public sealed class ClientGameOptions
{
    /// <summary>Шрифты интерфейса; null — Ark Pixel из ресурсов (клиент сам их закроет).</summary>
    public Fonts? Fonts { get; init; }

    /// <summary>Звук; null — без звука (тесты, нет устройства): команды модов разбираются и пропадают.</summary>
    public AudioEngine? Audio { get; init; }

    /// <summary>Общий хозяин модов; null — свой, с <see cref="ModuleOptions"/>.</summary>
    public GameModuleHost? Modules { get; init; }

    /// <summary>Пределы клиентских модов (те же, что на хосте).</summary>
    public GameModuleOptions ModuleOptions { get; init; } = new();

    /// <summary>Журнал: сбои модов и разбора.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Сколько ждать приветствия и снимка.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Хранилище между партиями (настройки модов, ABI 2); null — в памяти. TODO(Д4): файловое у оболочки.</summary>
    public IStorage? Storage { get; init; }

    /// <summary>
    /// Как исполняется код клиентских модов (docs/dotnet-n6.md, Н6-6): client.wasm (игра) или сборка .NET в процессе
    /// (тесты, обвязка Viewer: <c>ModModes.FromEnvironment()</c>, KHAROS_MODS=wasm|clr, по умолчанию CLR).
    /// </summary>
    public ModMode ModMode { get; init; } = ModMode.Wasm;

    /// <summary>Режим CLR: сборка клиентской части по имени мода; кого нет — ищется рядом с mod.json (проект *.Client).</summary>
    public IReadOnlyDictionary<string, string>? ClrAssemblies { get; init; }
}

/// <summary>Сколько стоил кадр клиента без видеокарты, мс: приём, моды, сборка кадра.</summary>
public readonly record struct ClientFrameStats(double Total, double Pump, double Mods, double Build, int Sprites);

/// <summary>
/// Партия на клиенте (Р10, Р11; перенос apps/client/src/game.rs): реплика мира с хоста, моды клиента (подача), атлас
/// обликов, шрифты и интерфейс, звук. Кадр (<see cref="Step"/>): приём сообщений хоста (места до тика запоминаются для
/// сглаживания) → вход кадра каждому моду → kh_frame → интерфейс, рисунки, звук, приказы, камера, атмосфера → частицы
/// и следы → раскладка интерфейса → кадр рендера: спрайты по слоям с палитрой сторон и тенями, огни и лучи, следы,
/// туман, атмосфера. Вызовы — из одного потока.
/// </summary>
public sealed partial class ClientGame : IDisposable
{
    private readonly ITransport _transport;
    private readonly ClientGameOptions _options;
    private readonly bool _ownsFonts, _ownsModules;
    private readonly GameModuleHost _modules;
    private readonly List<ClientModule> _mods = [];
    private readonly List<Look> _looks = [];
    // Облики по номеру: сначала виды контента (номер облика = номер вида), за ними свободные облики модов — значки,
    // эффекты, покрытие, которые не вид контента (имя → номер в _lookIds).
    private readonly List<Look?> _lookById;
    private readonly Dictionary<string, int> _lookIds = new(StringComparer.Ordinal);
    private readonly AbiWriter _input = new();
    private readonly FrameInput _frameInput = new();
    private readonly List<ReplicaSignal> _signals = [];
    private double? _last;
    private double _tickTime;
    private Renderer? _uploaded;
    private ViewBox? _viewSent;
    private double _viewSentAt = double.NegativeInfinity;
    private readonly List<ChatLine> _chat = [];
    private readonly List<(byte Action, string Arg)> _shell = [];
    private uint _rosterRevision;
    private bool _disposed;

    /// <summary>Партия со своими модами: ждёт приветствие и снимок; моды хоста должны совпасть со своими.</summary>
    public ClientGame(GameContent content, ITransport transport, ClientGameOptions? options = null)
        : this(content, transport, new Replica(), options, connected: false) { }

    /// <summary>Партия с модами, которые собрал вход (<see cref="GameConnector"/>: свои, из кэша или от хоста).</summary>
    public ClientGame(GameConnector connector, ClientGameOptions? options = null)
        : this(connector.Content ?? throw new ClientException("вход не закончен: модов хоста ещё нет"), connector.Transport, connector.Replica, options, connected: true) { }

    private ClientGame(GameContent content, ITransport transport, Replica replica, ClientGameOptions? options, bool connected)
    {
        _options = options ?? new ClientGameOptions();
        Content = content;
        _transport = transport;
        Replica = replica;
        if (!connected || !Replica.Ready)
            Connect(_options.ConnectTimeout);
        TakeInbox();
        var welcome = Replica.Welcome!;
        if (!welcome.Checksum.AsSpan().SequenceEqual(content.Checksum))
            throw new ClientException("моды хоста и клиента разошлись: суммы не совпали");

        _ownsFonts = _options.Fonts is null;
        Fonts = _options.Fonts ?? Fonts.Load();
        Gui = new Gui(Fonts);
        Audio = _options.Audio;
        Scene = new ClientScene(Gui) { LookOf = LookOf };
        Scene.SetPalette(ClientScene.DefaultTeams);
        Land = TerrainOf(Replica);
        Tools = new ClientTools(Replica, content, () => Land, Poses, Fonts)
        {
            Texts = BuildTexts(content),
            Log = Report,
            Storage = _options.Storage ?? new MemoryStorage(),
            Players = () => Players,
            Admin = () => Admin,
        };
        Tools.LookId = LookId;
        _ownsModules = _options.Modules is null;
        _modules = _options.Modules ?? new GameModuleHost(_options.ModuleOptions);
        _lookById = [.. new Look?[content.Protos.Count]];
        try
        {
            LoadMods(welcome);
        }
        catch
        {
            Dispose();
            throw;
        }
        Atlas = LookAtlas.Pack(_looks);
        Minimap = BuildMinimap();
    }

    public GameContent Content { get; }
    public Replica Replica { get; }
    public Fonts Fonts { get; }
    public Gui Gui { get; }
    public AudioEngine? Audio { get; }
    public ClientScene Scene { get; }
    public ClientTools Tools { get; }
    public Poses Poses { get; } = new();

    /// <summary>Местность партии (клиент строит её по генератору из приветствия); null — нет.</summary>
    public Land? Land { get; }

    /// <summary>Облики видов и их общий атлас.</summary>
    public IReadOnlyList<Look> Looks => _looks;
    public LookAtlas Atlas { get; }

    /// <summary>Мини-карта: пиксель на тайл, RGBA; null — у партии нет местности.</summary>
    public (byte[] Pixels, int Width, int Height)? Minimap { get; }

    public IReadOnlyList<ClientModule> Mods => _mods;

    /// <summary>Кадр рендера (переиспользуется: верен до следующего <see cref="Step"/>).</summary>
    public RenderFrame Frame { get; } = new();

    /// <summary>Камера последнего кадра.</summary>
    public View View => Frame.View;

    public ClientFrameStats Stats { get; private set; }

    /// <summary>Сбои модов и разбора (последние 1000).</summary>
    public List<string> Errors { get; } = [];

    /// <summary>Строки чата по порядку прихода (последние 200).</summary>
    public IReadOnlyList<ChatLine> Chat => _chat;

    /// <summary>Игроки партии, как их прислал хост.</summary>
    public IReadOnlyList<PlayerInfo> Players { get; private set; } = [];

    /// <summary>Команды чата, доступные этому подключению, и в режиме ли администратора.</summary>
    public IReadOnlyList<CommandInfo> Commands { get; private set; } = [];
    public bool Admin { get; private set; }

    /// <summary>Хост отказал или выгнал: причина; null — нет.</summary>
    public string? Refused { get; private set; }

    /// <summary>Ревизия ростера (вход кадра, ABI 2): растёт, когда хост прислал игроков.</summary>
    public uint RosterRevision => _rosterRevision;

    /// <summary>
    /// Просьбы модов к оболочке (запись Shell, ABI 2): действие ClientAbi.ShellAction* и текст — копятся, пока их не
    /// заберут. TODO(Д4): оболочка забирает их каждый кадр (меню, чат с текстом, настройки).
    /// </summary>
    public List<(byte Action, string Arg)> ShellRequests => _shell;

    /// <summary>
    /// После модов кадра, до сборки кадра рендера: обвязка тестов (Viewer, сверка кадров) ставит камеру и атмосферу
    /// сцены поверх того, что решили моды.
    /// </summary>
    public Action<ClientScene>? AfterMods { get; set; }

    /// <summary>Строка чата или команда («/…») хосту.</summary>
    public void Say(string text) => _transport.Send(Protocol.Messages.Say(text));

    /// <summary>Начать заново (что делать, решают моды хоста).</summary>
    public void Respawn() => _transport.Send(Protocol.Messages.Respawn());

    /// <summary>Облик по номеру (номер вида или свободного облика, <see cref="LookId"/>); нет — null.</summary>
    public Look? LookOf(int look) => (uint)look < (uint)_lookById.Count ? _lookById[look] : null;

    /// <summary>
    /// Номер облика «раздел/имя»: у вида контента — номер вида, у свободного облика мода — номер после всех видов
    /// (в порядке объявления, по всем модам); нет такого облика — −1. Инструмент клиента Look (ClientAbi, 52).
    /// </summary>
    public int LookId(string name) =>
        _lookIds.TryGetValue(name, out var id) ? id : Content.FindProto(name) is { } proto && _lookById[proto.Id] is not null ? proto.Id : -1;

    /// <summary>Облик «раздел/имя» (вид или свободный); нет — null.</summary>
    public Look? LookOf(string name) => LookOf(LookId(name));

    /// <summary>
    /// Ресурсы рендеру: атлас обликов, скин, мини-карта и земля — один раз на рендер, шрифтовой атлас — когда менялся,
    /// туман — если кадр с <see cref="RenderFrame.FogUpdate"/> до этого рендера не дошёл (кадры пропускали или рендер
    /// новый). Звать перед каждым <see cref="Renderer.Render"/> с кадром последнего <see cref="Step"/>.
    /// </summary>
    public void Upload(Renderer renderer)
    {
        if (_uploaded != renderer)
        {
            renderer.SetAtlas(Atlas.Pixels, Atlas.Width, Atlas.Height);
            renderer.SetSkin(Skins.Pixels(), Skins.Width, Skins.Height);
            if (Minimap is { } minimap)
                renderer.SetMinimap(minimap.Pixels, minimap.Width, minimap.Height);
            if (Land is { } land)
                renderer.SetLand((x, y) => land.GetChunk(x, y), (x, y) => land.ChunkRevision(x, y));
            else
                renderer.ClearLand();
            _uploaded = renderer;
            _fogDelivered = -1;
        }
        if (_fogDelivered != _fogImageRevision && _fogImage is { } fog)
        {
            if (Frame.FogUpdate is null)
                renderer.SetFog(fog.Alpha, fog.Width, fog.Height);
            _fogDelivered = _fogImageRevision;
        }
        renderer.SetFontAtlas(Fonts.Atlas.Pixels, (ulong)Fonts.Atlas.Generation);
    }

    /// <summary>Кадр: приём, моды клиента, сборка кадра рендера.</summary>
    public RenderFrame Step(in ClientInput input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var started = Stopwatch.GetTimestamp();
        var time = input.Time;
        var dt = _last is { } last ? Math.Clamp(time - last, 0, 0.25) : 1 / 60.0;
        _last = time;
        Pump(time);
        var pumped = Stopwatch.GetTimestamp();
        var welcome = Replica.Welcome!;
        Poses.Alpha = Math.Clamp((time - _tickTime) / welcome.Dt, 0, 1);
        _signals.Clear();
        Replica.TakeSignals(_signals);

        var frame = Frame;
        frame.Clear();
        Scene.World = frame.World;
        Scene.BeginFrame();
        Scene.Time = time;
        BeginUi(input);
        var clock = new FrameClock(time, dt, Replica.Tick, Poses.Alpha, welcome.Dt, Replica.Player);
        foreach (var mod in _mods)
            RunMod(mod, clock, input);
        AfterMods?.Invoke(Scene);
        foreach (var error in Scene.Errors)
            Report(error);
        var modded = Stopwatch.GetTimestamp();

        StepParticles((float)dt);
        FinishUi(frame);
        foreach (var (name, payload) in Scene.Orders)
            _transport.Send(DeltaCodec.Order(name, payload));

        var camera = Scene.Camera;
        frame.View = new View((float)camera.X, (float)camera.Y, (float)camera.Zoom, input.Width, input.Height, input.Scale);
        frame.Time = (float)time;
        Scene.Palette.CopyTo(frame.Palette, 0);
        Atmosphere(frame, (float)dt);
        Decals((float)dt, frame);
        SendView(frame.View, time);
        UpdateFog(frame);
        SceneLights(frame);
        var sprites = Sprites(frame, time) + SceneSprites(frame);
        var done = Stopwatch.GetTimestamp();
        Stats = new ClientFrameStats(
            Stopwatch.GetElapsedTime(started, done).TotalMilliseconds,
            Stopwatch.GetElapsedTime(started, pumped).TotalMilliseconds,
            Stopwatch.GetElapsedTime(pumped, modded).TotalMilliseconds,
            Stopwatch.GetElapsedTime(modded, done).TotalMilliseconds,
            sprites);
        return frame;
    }

    /// <summary>Приказ хосту от оболочки (не от мода).</summary>
    public void Send(string order, ReadOnlySpan<byte> payload) => _transport.Send(DeltaCodec.Order(order, payload));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var mod in _mods)
            mod.Dispose();
        _mods.Clear();
        if (_ownsModules)
            _modules.Dispose();
        if (_ownsFonts)
            Fonts?.Dispose();
    }

    // ------------------------------------------------------------------ загрузка

    private void Connect(TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!Replica.Ready)
        {
            if (_transport.TryReceive(out var message))
            {
                if (Replica.Apply(message) == MessageType.Refused)
                    throw new ClientException($"хост не пустил: {RefusedText(Protocol.Messages.ReadRefused(message))}");
                continue;
            }
            if (_transport.Closed)
                throw new ClientException(ClosedReason(_transport, "хост закрыл соединение до снимка мира"));
            if (watch.Elapsed > timeout)
                throw new ClientException($"нет приветствия и снимка за {timeout.TotalSeconds:0.#} с");
            Thread.Sleep(1);
        }
        _tickTime = 0;
    }

    /// <summary>Карта клиента: из генератора или присланная хостом байтами, с правками хоста поверх (их применяет сама реплика).</summary>
    private static Land? TerrainOf(Replica replica)
    {
        try
        {
            return replica.Terrain;
        }
        catch (FormatException error)
        {
            throw new ClientException($"карта хоста не читается: {error.Message}", error);
        }
    }

    /// <summary>Почему закрылся транспорт: причина из кадра закрытия WebSocket, иначе fallback.</summary>
    internal static string ClosedReason(ITransport transport, string fallback) =>
        transport is WebSocketTransport { CloseReason: { Length: > 0 } reason } ? $"сервер закрыл соединение: {reason}" : fallback;

    /// <summary>Сообщения вне мира: чат, игроки, команды, отказ.</summary>
    private void TakeInbox()
    {
        foreach (var message in Replica.Inbox)
        {
            try
            {
                switch ((MessageType)message[0])
                {
                    case MessageType.Chat:
                        _chat.AddRange(Protocol.Messages.ReadChat(message));
                        if (_chat.Count > 200)
                            _chat.RemoveRange(0, _chat.Count - 200);
                        break;
                    case MessageType.Players:
                        Players = Protocol.Messages.ReadPlayers(message);
                        _rosterRevision++;
                        break;
                    case MessageType.Commands:
                        (var commands, Admin) = Protocol.Messages.ReadCommands(message);
                        Commands = commands;
                        break;
                    case MessageType.Refused:
                        Refused = RefusedText(Protocol.Messages.ReadRefused(message));
                        Report($"хост: {Refused}");
                        break;
                }
            }
            catch (FormatException error)
            {
                Report($"сообщение хоста: {error.Message}");
            }
        }
        Replica.Inbox.Clear();
    }

    /// <summary>Экран — хосту, когда сдвинулся на тайл или раз в секунду: по нему хост решает, что слать первым.</summary>
    private void SendView(View view, double time)
    {
        var (left, top, right, bottom) = view.Bounds(0);
        var box = new ViewBox(left, top, right, bottom);
        if (_viewSent is { } sent && time - _viewSentAt < 1 && Math.Abs(sent.Left - box.Left) < 1 && Math.Abs(sent.Top - box.Top) < 1 && Math.Abs(sent.Right - box.Right) < 1 && Math.Abs(sent.Bottom - box.Bottom) < 1)
            return;
        if (!float.IsFinite(left) || !float.IsFinite(top) || !float.IsFinite(right) || !float.IsFinite(bottom) || right < left || bottom < top)
            return;
        _viewSent = box;
        _viewSentAt = time;
        _transport.Send(Protocol.Messages.View(box));
    }

    private void LoadMods(Welcome welcome)
    {
        var schema = Replica.Schema!;
        var load = new ModLoadInfo
        {
            Abi = ClientAbi.Version,
            Side = ClientAbi.Side,
            Seed = welcome.Seed,
            Game = welcome.Game,
            Components = [.. schema.Components.Select(c => new ModComponentInfo
            {
                Name = c.Name,
                Id = c.Id,
                Fields = [.. c.Fields.Select(f => new ModFieldInfo { Name = f.Name, Kind = (byte)f.Kind, Index = f.Index, Slot = f.Slot })],
            })],
            Data = [.. Content.DataNames.Select(name => new ModDataFile { Name = name, Bytes = Content.DataBytes(name)! })],
            Layers = [.. Replica.Layers!.Specs.Select((spec, id) => new ModLayerInfo
            {
                Name = spec.Name,
                Id = id,
                PerPlayer = spec.Scope == LayerScope.Player,
                Cell = spec.Cell,
                Channels = spec.Channels.Count,
                Fields = [.. spec.Fields.Select(field => field.Name)],
            })],
        };
        var minimap = DefaultMinimap;
        foreach (var package in Content.Mods)
        {
            if (ClientModule.Load(package, _options.ModMode, load, () => _modules, _options.ModuleOptions, _options.ClrAssemblies) is not { } module)
                continue;
            ClientModule mod;
            Tools.Mod = package.Name;
            try
            {
                mod = new ClientModule(package, module, Prepare, Audio);
            }
            catch
            {
                module.Dispose();
                throw;
            }
            _mods.Add(mod);
            var instance = mod.Acquire() ?? throw new ClientException($"мод {package.Name}: выключен при загрузке");
            var manifest = mod.Manifest;
            for (var index = 0; index < manifest.Looks.Length; index++)
            {
                var path = manifest.Looks[index];
                if (path.Length == 0)
                {
                    Report($"мод {package.Name}: облик №{index} без имени");
                    continue;
                }
                LookSpec spec;
                try
                {
                    spec = LookStream.Decode(instance.Look(index));
                }
                catch (FormatException error)
                {
                    throw new ClientException($"мод {package.Name}, облик {path}: {error.Message}", error);
                }
                // Облик вида — под номером вида; облик, что не вид контента (значок, эффект, покрытие), — свободный:
                // номер после всех видов. Позднее объявление того же имени (мод поверх мода) заменяет прежнее.
                int id;
                if (Content.FindProto(path) is { } proto)
                    id = proto.Id;
                else if (!_lookIds.TryGetValue(path, out id))
                {
                    id = _lookById.Count;
                    _lookIds[path] = id;
                    _lookById.Add(null);
                }
                var look = Look.Build(id, spec, path);
                if (_lookById[id] is { } previous)
                    _looks.Remove(previous);
                _lookById[id] = look;
                _looks.Add(look);
            }
            LoadSounds(package, instance, manifest);
            if (manifest.Minimap.Length == 16)
                minimap = manifest.Minimap;
            // Мод, собранный до ItemLooks, их не присылает.
            foreach (var item in manifest.ItemLooks ?? [])
                ResolveItemLook(package.Name, item);
            foreach (var cell in manifest.CellLooks ?? [])
                ResolveCellLook(package.Name, cell);
        }
        _minimapPalette = minimap;
    }

    private void Prepare(IClientModInstance instance)
    {
        instance.Tools = Tools.Call;
        instance.RandomSource = null;
    }

    // ------------------------------------------------------------------ кадр

    /// <summary>Сообщения хоста; сдвинутые дельтой сущности кадр ведёт от прежнего места к новому (за span тиков).</summary>
    private void Pump(double time)
    {
        var dt = Replica.Welcome?.Dt ?? 0.05;
        Poses.Time = time;
        while (_transport.TryReceive(out var message))
        {
            var version = Replica.WorldVersion;
            try
            {
                var type = Replica.Apply(message);
                if (type == MessageType.Delta)
                {
                    Poses.Moved(Replica.Moves, time, dt);
                    _tickTime = time;
                }
            }
            catch (FormatException error)
            {
                Report($"сообщение хоста: {error.Message}");
            }
            if (Replica.WorldVersion != version)
            {
                Poses.Forget();
                _tickTime = time;
            }
        }
        if (Replica.Inbox.Count > 0)
            TakeInbox();
    }

    private void RunMod(ClientModule mod, in FrameClock clock, in ClientInput input)
    {
        var started = Stopwatch.GetTimestamp();
        Tools.Mod = mod.Name;
        Tools.Lang = input.Lang;
        IClientModInstance? instance;
        try
        {
            instance = mod.Acquire();
        }
        catch (ModException error)
        {
            mod.Module.Strike();
            Report($"мод {mod.Name}: {error.Message}");
            return;
        }
        if (instance is null)
            return;
        var camera = Scene.Camera;
        _frameInput.Write(_input, clock, input, camera, Gui, Scene.Atmosphere, _signals, Audio?.Now, mod.Sounds.TakeEnded(), _rosterRevision);
        try
        {
            var output = instance.Frame(_input.Written);
            ClientStream.Apply(output, Scene, mod.Sounds, new Listener(camera.X, camera.Y, camera.Zoom, input.Width, input.Height));
        }
        catch (ModTrapException error)
        {
            if (!error.Timeout)
                mod.Module.Strike();
            Report($"мод {mod.Name}: {error.Message}");
        }
        catch (ModException error)
        {
            Report($"мод {mod.Name}: {error.Message}");
        }
        catch (FormatException error)
        {
            mod.Module.Strike();
            Report($"мод {mod.Name}: выход кадра: {error.Message}");
        }
        // Настройки мода — под его ключом; просьбы к оболочке — копятся до оболочки.
        foreach (var (key, value) in Scene.Prefs)
            Tools.Storage.Set(ClientTools.PrefsKey(mod.Name, key), value);
        Scene.Prefs.Clear();
        _shell.AddRange(Scene.Shell);
        if (_shell.Count > 100)
            _shell.RemoveRange(0, _shell.Count - 100);
        Scene.Shell.Clear();
        mod.LastFrameMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private void Report(string message)
    {
        if (Errors.Count >= 1000)
            Errors.RemoveAt(0);
        Errors.Add(message);
        _options.Log?.Invoke(message);
    }

    /// <summary>Цвет мода (0xRRGGBB или 0xAARRGGBB) с множителем альфы — в rgba вершины.</summary>
    public static uint Rgba(uint value, float alpha = 1)
    {
        var a = (value > 0xffffff ? (value >> 24) / 255f : 1f) * alpha;
        return (value >> 16 & 0xff) | (value >> 8 & 0xff) << 8 | (value & 0xff) << 16 | (uint)Gui.Byte(Math.Clamp(a, 0, 1) * 255) << 24;
    }

    /// <summary>Числа в список без промежуточного массива.</summary>
    private static void Push(List<float> list, ReadOnlySpan<float> values) => list.AddRange(values);

    private static float Euclid(float value, float modulus)
    {
        var rest = value % modulus;
        return rest < 0 ? rest + modulus : rest;
    }
}
