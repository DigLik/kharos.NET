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
}

/// <summary>Клиентский мод в партии: модуль, экземпляр (после ловушки — новый) и его голоса.</summary>
public sealed class ClientModule : IDisposable
{
    private readonly Action<GameModuleInstance> _prepare;
    private GameModuleInstance? _instance;

    internal ClientModule(ModPackage package, GameModule module, Action<GameModuleInstance> prepare, AudioEngine? audio)
    {
        Package = package;
        Module = module;
        _prepare = prepare;
        Sounds = new ModSounds(audio);
        _instance = module.CreateInstance();
        prepare(_instance);
        Manifest = _instance.Manifest;
    }

    public string Name => Package.Name;
    public ModPackage Package { get; }
    public GameModule Module { get; }

    /// <summary>Что мод объявил в kh_load: облики, звуки, голоса.</summary>
    public ModManifest Manifest { get; }

    public ModSounds Sounds { get; }

    /// <summary>Сколько раз экземпляр пересоздан после ловушки.</summary>
    public int Restarts { get; private set; }

    /// <summary>Время kh_frame с разбором выхода прошлого кадра, мс.</summary>
    public double LastFrameMs { get; internal set; }

    /// <summary>Выключен после трёх сбоев.</summary>
    public bool Disabled => Module.Disabled;

    /// <summary>Экземпляр для вызовов (null — выключен).</summary>
    internal GameModuleInstance? Acquire()
    {
        if (Module.Disabled)
            return null;
        if (_instance is { Faulted: false })
            return _instance;
        _instance?.Dispose();
        _instance = null;
        Sounds.ReleaseAll();
        _instance = Module.CreateInstance();
        _prepare(_instance);
        Restarts++;
        return _instance;
    }

    public void Dispose()
    {
        Sounds.ReleaseAll();
        _instance?.Dispose();
        _instance = null;
        Module.Dispose();
    }
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
public sealed class ClientGame : IDisposable
{
    /// <summary>Мини-карта по умолчанию — PALETTE из minimap.ts: биом за биомом, внутри — вид местности.</summary>
    public static readonly uint[] DefaultMinimap =
    [
        0xC29945, 0x63450F, 0x36331A, 0x8C6828,
        0xD1C9B3, 0x5E5C54, 0x4D8585, 0x828078,
        0xB35C29, 0x542414, 0x452112, 0x804024,
        0x857A38, 0x454021, 0x1A3621, 0x645F34,
    ];

    /// <summary>С какой доли срока следы растворяются.</summary>
    private const float FadeFrom = 0.25f;

    /// <summary>Туман над тайлом, байты: не разведан, разведан, виден (fogPass.ts).</summary>
    private const byte FogHidden = 240, FogRemembered = 128, FogVisible = 0;

    private readonly ITransport _transport;
    private readonly ClientGameOptions _options;
    private readonly bool _ownsFonts, _ownsModules;
    private readonly GameModuleHost _modules;
    private readonly List<ClientModule> _mods = [];
    private readonly List<Look> _looks = [];
    private readonly Look?[] _lookByProto;
    private readonly AbiWriter _input = new();
    private readonly FrameInput _frameInput = new();
    private readonly List<ReplicaSignal> _signals = [];
    private readonly Dictionary<uint, (float X, float Y, float Distance, double Seen)> _walked = [];
    private readonly List<(float Bottom, int At)> _buildings = [];
    private readonly List<float> _buildingSprites = [];
    private readonly List<uint> _forget = [];
    private readonly int _attached = -1, _attachedParent = -1;
    private double? _last;
    private double _tickTime;
    private int _fogRevision = -1;
    private uint[] _minimapPalette = DefaultMinimap;
    private (float X, float Y) _drift;
    private Renderer? _uploaded;
    private (byte[] Alpha, int Width, int Height)? _fogImage;
    private int _fogImageRevision, _fogDelivered;
    private ViewBox? _viewSent;
    private double _viewSentAt = double.NegativeInfinity;
    private readonly List<ChatLine> _chat = [];
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
        Scene = new ClientScene(Gui) { LookOf = proto => (uint)proto < (uint)_lookByProto!.Length ? _lookByProto[proto] : null };
        Scene.SetPalette(ClientScene.DefaultTeams);
        if (welcome.Land is { } config && welcome.Map is { } map)
            Land = Replica.Land is { } packed ? UnpackLand(config, packed) : new Land(config, LandArea.Of(map.Left, map.Top, map.Right, map.Bottom));
        Tools = new ClientTools(Replica, content, () => Land, Poses, Fonts) { Log = Report };
        if (Replica.Schema!.Find("Attached") is { } attached && Replica.Schema[attached].Field("parent") is { } parent)
            (_attached, _attachedParent) = (attached, parent.Index);

        _ownsModules = _options.Modules is null;
        _modules = _options.Modules ?? new GameModuleHost(_options.ModuleOptions);
        _lookByProto = new Look?[content.Protos.Count];
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

    /// <summary>Строка чата или команда («/…») хосту.</summary>
    public void Say(string text) => _transport.Send(Protocol.Messages.Say(text));

    /// <summary>Начать заново (что делать, решают моды хоста).</summary>
    public void Respawn() => _transport.Send(Protocol.Messages.Respawn());

    /// <summary>Облик вида; нет — null.</summary>
    public Look? LookOf(int proto) => (uint)proto < (uint)_lookByProto.Length ? _lookByProto[proto] : null;

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
        Scene.Orders.Clear();
        Scene.Errors.Clear();
        Scene.Time = time;
        Gui.Begin(input.ToUi());
        var clock = new FrameClock(time, dt, Replica.Tick, Poses.Alpha, welcome.Dt, Replica.Player);
        foreach (var mod in _mods)
            RunMod(mod, clock, input);
        foreach (var error in Scene.Errors)
            Report(error);
        var modded = Stopwatch.GetTimestamp();

        StepParticles((float)dt);
        Gui.Finish(frame.Interface);
        foreach (var (name, payload) in Scene.Orders)
            _transport.Send(DeltaCodec.Order(name, payload));

        var camera = Scene.Camera;
        frame.View = new View((float)camera.X, (float)camera.Y, (float)camera.Zoom, input.Width, input.Height, input.Scale);
        frame.Time = (float)time;
        Scene.Palette.CopyTo(frame.Palette, 0);
        var atmosphere = Scene.Atmosphere;
        frame.Light = Math.Clamp(atmosphere.Light, 0, 1);
        frame.Precipitation = Math.Clamp(atmosphere.Precipitation, 0, 1);
        frame.Wind = (atmosphere.WindX, atmosphere.WindY);
        // Снос копится, а не считается как ветер × время: смена ветра не дёргает пелену (precipitationPass.ts).
        _drift = (Euclid(_drift.X + atmosphere.WindX * (float)dt, Renderer.Window), Euclid(_drift.Y + atmosphere.WindY * (float)dt, Renderer.Window));
        frame.Drift = _drift;
        Decals((float)dt, frame);
        SendView(frame.View, time);
        UpdateFog(frame);
        var sprites = Sprites(frame, time);
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
                    throw new ClientException($"хост не пустил: {Protocol.Messages.ReadRefused(message).Reason}");
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

    /// <summary>Карта, правленная на хосте, — байтами.</summary>
    private static Land UnpackLand(GeneratorConfig config, byte[] packed)
    {
        try
        {
            return Land.Unpack(config, packed);
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
                        break;
                    case MessageType.Commands:
                        (var commands, Admin) = Protocol.Messages.ReadCommands(message);
                        Commands = commands;
                        break;
                    case MessageType.Refused:
                        Refused = Protocol.Messages.ReadRefused(message).Reason;
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
            Abi = ModAbi.Version,
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
        };
        var minimap = DefaultMinimap;
        foreach (var package in Content.Mods)
        {
            if (package.ClientWasm is not { } wasm)
                continue;
            var module = _modules.Load(package.Name, wasm, load);
            ClientModule mod;
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
                if (Content.FindProto(path) is not { } proto)
                {
                    Report($"мод {package.Name}: облик {path} — нет такого вида");
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
                var look = Look.Build(proto.Id, spec);
                if (_lookByProto[proto.Id] is { } previous)
                    _looks.Remove(previous);
                _lookByProto[proto.Id] = look;
                _looks.Add(look);
            }
            if (Audio is { } audio)
            {
                for (var entry = 0; entry < manifest.Bank.Length; entry++)
                {
                    var info = manifest.Bank[entry];
                    var variants = new SampleBuffer[Math.Max(info.Variants, 1)];
                    for (var variant = 0; variant < variants.Length; variant++)
                        variants[variant] = ReadSample(instance.Bank(entry, variant, AudioFormat.SampleRate), $"мод {package.Name}, звук {info.Name}");
                    audio.Bank.Add(info.Name, Buses.Parse(info.Bus) ?? Bus.World, variants);
                }
                foreach (var voice in manifest.Voices)
                {
                    try
                    {
                        audio.RegisterVoice(voice.Name, VoiceGraph.Decode(voice.Graph));
                    }
                    catch (FormatException error)
                    {
                        Report($"мод {package.Name}, голос {voice.Name}: {error.Message}");
                    }
                }
            }
            if (manifest.Minimap.Length == 16)
                minimap = manifest.Minimap;
        }
        _minimapPalette = minimap;
    }

    private void Prepare(GameModuleInstance instance)
    {
        instance.Tools = Tools.Call;
        instance.RandomSource = null;
    }

    /// <summary>Запись банка из kh_bank: channels:u8 frames:u32 и f32 канал за каналом.</summary>
    private static SampleBuffer ReadSample(ReadOnlySpan<byte> bytes, string where)
    {
        var r = new AbiReader(bytes);
        int channels = r.U8(), frames = checked((int)r.U32());
        if (channels is 0 or 3 or > 4 || frames > AudioFormat.SampleRate * 60)
            throw new ClientException($"{where}: {channels} каналов, {frames} кадров");
        var data = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            data[c] = new float[frames];
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(r.Take(frames * 4)).CopyTo(data[c]);
        }
        return new SampleBuffer(data);
    }

    /// <summary>Мини-карта: тайл — пиксель цветом своей местности и биома (minimap вида lands, PALETTE в minimap.ts).</summary>
    private (byte[], int, int)? BuildMinimap()
    {
        if (Land is not { } land || Replica.Welcome?.Map is not { } map)
            return null;
        var (width, height) = (map.Width, map.Height);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var (tx, ty) = (map.Left + x, map.Top + y);
                var terrain = (int)land.TerrainAt(tx, ty) & 3;
                var biome = (int)land.BiomeAt(tx + 0.5, ty + 0.5) & 3;
                var color = _minimapPalette[biome * 4 + terrain];
                var at = (y * width + x) * 4;
                (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = ((byte)(color >> 16), (byte)(color >> 8), (byte)color, 255);
            }
        return (pixels, width, height);
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
        GameModuleInstance? instance;
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
        _frameInput.Write(_input, clock, input, camera, Gui, Scene.Atmosphere, _signals, Audio?.Now, mod.Sounds.TakeEnded());
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
        mod.LastFrameMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>Частицы: ход, снос ветром, отмирание; рисуются в слой поверх мира квадратами по текселю.</summary>
    private void StepParticles(float dt)
    {
        var (wx, wy) = (Scene.Atmosphere.WindX, Scene.Atmosphere.WindY);
        var particles = Scene.Particles;
        var kept = 0;
        for (var i = 0; i < particles.Count; i++)
        {
            var p = particles[i];
            p.Age += dt;
            var drift = p.Drift ? 1f : 0f;
            p.X += (p.Vx + wx * drift) * dt;
            p.Y += (p.Vy + wy * drift) * dt;
            if (p.Age >= p.Life)
                continue;
            particles[kept++] = p;
            var fade = MathF.Min(1 - p.Age / p.Life, 1);
            var size = p.Size / RenderFrame.Texels;
            var x = MathF.Floor(p.X * RenderFrame.Texels) / RenderFrame.Texels;
            var y = MathF.Floor(p.Y * RenderFrame.Texels) / RenderFrame.Texels;
            Scene.World.Rect(x, y, x + size, y + size, Rgba(p.Color, fade));
        }
        particles.RemoveRange(kept, particles.Count - kept);
    }

    /// <summary>
    /// Следы на земле: стареют, с <see cref="FadeFrom"/> срока растворяются (плавная ступень), вне обзора забываются;
    /// что в кадре — в frame.Decals (decalsPass.ts).
    /// </summary>
    private void Decals(float dt, RenderFrame frame)
    {
        var (left, top, right, bottom) = frame.View.Bounds(2);
        for (var node = Scene.Decals.First; node is not null;)
        {
            var next = node.Next;
            ref var decal = ref node.ValueRef;
            decal.Age += dt;
            if (decal.Age >= decal.Life || !Replica.Sees((int)MathF.Floor(decal.X2), (int)MathF.Floor(decal.Y2)))
            {
                Scene.Decals.Remove(node);
                node = next;
                continue;
            }
            if (decal.X2 >= left && decal.X2 <= right && decal.Y2 >= top && decal.Y2 <= bottom)
            {
                var share = MathF.Max((decal.Age / decal.Life - FadeFrom) / (1 - FadeFrom), 0);
                var remaining = 1 - share * share * (3 - 2 * share);
                Push(frame.Decals, [decal.X1, decal.Y1, decal.X2, decal.Y2, decal.Width, 0, 0, 0, 0, decal.R, decal.G, decal.B, decal.Opacity, remaining, decal.Density, decal.Seed]);
            }
            node = next;
        }
    }

    /// <summary>Туман войны: альфа на тайл с рамкой в тайл (неразведанной) — когда обзор поменялся.</summary>
    private void UpdateFog(RenderFrame frame)
    {
        if (Replica.Welcome is not { Fog: true, Map: { } map } welcome || Replica.Layers is not { } layers)
            return;
        frame.Fog = (map.Left - 1, map.Top - 1);
        if (_fogRevision == Replica.FogRevision)
            return;
        _fogRevision = Replica.FogRevision;
        int width = map.Width + 2, height = map.Height + 2;
        var alpha = new byte[width * height];
        alpha.AsSpan().Fill(FogHidden);
        var owner = LayerOwner.Player(welcome.Player);
        var sight = layers.Find(Fog.Sight);
        var explored = layers.Find(Fog.Explored);
        int fromX = map.Left >> 5, toX = (map.Right - 1) >> 5, fromY = map.Top >> 5, toY = (map.Bottom - 1) >> 5;
        for (var cy = fromY; cy <= toY; cy++)
            for (var cx = fromX; cx <= toX; cx++)
            {
                var seen = sight is { } s ? layers.ChunkBytes(s, owner, cx, cy) : [];
                var known = explored is { } e ? layers.ChunkBytes(e, owner, cx, cy) : [];
                if (seen.IsEmpty && known.IsEmpty)
                    continue;
                for (var ty = 0; ty < LayerSet.Chunk; ty++)
                {
                    var y = (cy << 5) + ty;
                    if (y < map.Top || y >= map.Bottom)
                        continue;
                    for (var tx = 0; tx < LayerSet.Chunk; tx++)
                    {
                        var x = (cx << 5) + tx;
                        if (x < map.Left || x >= map.Right)
                            continue;
                        var tile = ty * LayerSet.Chunk + tx;
                        var value = !seen.IsEmpty && seen[tile] != 0 ? FogVisible : !known.IsEmpty && known[tile] != 0 ? FogRemembered : FogHidden;
                        alpha[(y - map.Top + 1) * width + (x - map.Left + 1)] = value;
                    }
                }
            }
        frame.FogUpdate = (alpha, width, height);
        _fogImage = frame.FogUpdate;
        _fogImageRevision++;
    }

    /// <summary>
    /// Спрайты видимых сущностей по слоям (build в game.rs): облик по виду, кадр по направлению и фазе (у ходовых — по
    /// пройденному пути), сторона по владельцу, тень, огни и лучи; здания — по нижнему краю. Возвращает, сколько спрайтов.
    /// </summary>
    private int Sprites(RenderFrame frame, double time)
    {
        if (Replica.World is not { } world)
            return 0;
        var view = frame.View;
        var (left, top, right, bottom) = view.Bounds(7);
        var player = Replica.Player;
        var fog = Replica.Welcome is { Fog: true };
        static float Snap(float value) => MathF.Round(value * RenderFrame.Texels, MidpointRounding.AwayFromZero) / RenderFrame.Texels;
        _buildings.Clear();
        _buildingSprites.Clear();
        Span<float> sprite = stackalloc float[RenderFrame.Sprite];
        var count = 0;
        var proto = world.Store(Builtin.Proto);
        var protoIds = proto.Column(0);
        foreach (var entity in world.Store(Builtin.Position).Entities)
        {
            var row = proto.RowOf(entity);
            if (row < 0 || LookOf((int)protoIds[row]) is not { } look)
                continue;
            if (!Poses.TryGet(world, entity, out var px, out var py, out var angle))
                continue;
            var (x, y) = ((float)px, (float)py);
            if (x < left || x > right || y < top || y > bottom)
                continue;
            var owner = world.TryNumber(entity, Builtin.Owner, 0, out var number) ? (uint)number : 0;
            // Чужое вне обзора не видно (руда — где разведано).
            if (owner != player && fog && !(look.Spec.Memory ? Replica.Explored((int)MathF.Floor(x), (int)MathF.Floor(y)) : Replica.Sees((int)MathF.Floor(x), (int)MathF.Floor(y))))
                continue;
            // Турель — слоем выше своего носителя.
            var layer = (int)look.Spec.Layer;
            if (_attached >= 0 && world.TryNumber(entity, _attached, _attachedParent, out var parent) && parent >= 1)
                layer = (world.TryNumber((uint)parent, Builtin.Proto, 0, out var parentProto) && LookOf((int)parentProto) is { } carrier ? (int)carrier.Spec.Layer : 0) + 1;
            var phase = 0;
            if (look.Phases > 1)
            {
                if (look.Spec.Step is { } step)
                {
                    (float X, float Y, float Distance, double Seen) walk = _walked.TryGetValue(entity, out var known) ? known : (x, y, 0f, time);
                    var moved = MathF.Sqrt((x - walk.X) * (x - walk.X) + (y - walk.Y) * (y - walk.Y));
                    var distance = walk.Distance + (moved < 1 ? moved : 0);
                    _walked[entity] = (x, y, distance, time);
                    phase = (int)(distance / step) % look.Phases;
                }
                else
                    phase = (int)(time * look.Spec.Fps) % look.Phases;
            }
            var (team, grey) = owner == 0 ? (3f, 1f) : owner == player ? (0f, 0f) : (1f, 0f);
            var cell = look.Cell(angle, phase);
            var uv = look.Cells[cell];
            var (w, h) = look.Tiles;
            var (spriteLeft, spriteTop) = (x + look.Offset.X - view.X, y + look.Offset.Y - view.Y);
            sprite[0] = spriteLeft;
            sprite[1] = spriteTop;
            sprite[2] = w;
            sprite[3] = h;
            sprite[4] = uv.U0;
            sprite[5] = uv.V0;
            sprite[6] = uv.U1;
            sprite[7] = uv.V1;
            sprite[8] = team;
            sprite[9] = look.Shadow.Offset;
            sprite[10] = look.Shadow.Opacity;
            sprite[11] = grey;
            if (layer == (int)LookLayer.Building)
            {
                _buildings.Add((y + look.Offset.Y + h, _buildingSprites.Count));
                foreach (var value in sprite) _buildingSprites.Add(value);
            }
            else
                foreach (var value in sprite) frame.SpriteLayers[Math.Min(layer, RenderFrame.Layers - 1)].Add(value);
            count++;

            var (cos, sin) = ((float)Math.Cos(angle), (float)Math.Sin(angle));
            var (forwardX, forwardY) = (cos / RenderFrame.Texels, sin / RenderFrame.Texels);
            foreach (var lamp in look.Spec.Lamps)
            {
                var (lx, ly) = (Snap(x + lamp.Along * forwardX - lamp.Across * forwardY), Snap(y + lamp.Along * forwardY + lamp.Across * forwardX));
                Push(frame.Spots, [lx, ly, lamp.Radius * 2, 1]);
                Push(frame.Blooms, [lx, ly, lamp.Radius, 1]);
            }
            if (look.Spec.Beam is { } beam)
                Push(frame.Beams, [Snap(x + beam.Along * forwardX), Snap(y + beam.Along * forwardY), beam.Length, beam.Level, cos, sin, beam.Near, beam.Spread]);
            // Огни кадра (здания): пятно в 16 радиусов лампы, ореол в 3 + 2 (lights.add в buildingsPass.ts).
            foreach (var light in look.FrameLights[cell])
            {
                if (light.Level <= Renderer.MinLevel)
                    continue;
                Push(frame.Spots, [x + light.X, y + light.Y, light.Size * 16, light.Level]);
                Push(frame.Blooms, [x + light.X, y + light.Y, light.Size * 3 + 2, light.Level]);
            }
        }
        if (_buildings.Count > 0)
        {
            _buildings.Sort((a, b) => a.Bottom.CompareTo(b.Bottom));
            var layer = frame.SpriteLayers[(int)LookLayer.Building];
            foreach (var (_, at) in _buildings)
                for (var i = 0; i < RenderFrame.Sprite; i++)
                    layer.Add(_buildingSprites[at + i]);
        }
        // Путь забывается через 2 с без показа.
        _forget.Clear();
        foreach (var (entity, walk) in _walked)
            if (time - walk.Seen >= 2)
                _forget.Add(entity);
        foreach (var entity in _forget)
            _walked.Remove(entity);
        return count;
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
