using System.Reflection;
using Kharos.Client.Game;
using Kharos.Client.Ui;
using Kharos.Engine.World;
using Kharos.Gpu;
using Kharos.Host;
using Kharos.Plugins.Game;
using Kharos.Protocol;
using MemoryPack;
using RenderFrame = Kharos.Gpu.Frame;

namespace Kharos.Base.Scenario;

/// <summary>Параметры зрителя: экран, режим клиентского мода, предел вызова в wasm.</summary>
public sealed class ViewerOptions
{
    /// <summary>Экран в логических пикселях.</summary>
    public float Width { get; init; } = 1280;
    public float Height { get; init; } = 720;

    /// <summary>Режим кода клиентского мода; null — <c>KHAROS_MODS</c> (wasm | clr), не задана — CLR.</summary>
    public ModMode? Mode { get; init; }

    /// <summary>Шаг часов клиента на кадр, секунды (60 кадров в секунду).</summary>
    public double FrameStep { get; init; } = 1 / 60.0;

    /// <summary>
    /// Звук офлайн: у клиента есть движок звука без устройства (<see cref="Viewer.Engine"/>), моды звучат (Audio.Available), а
    /// каждый кадр отрисовывается столько звука, сколько в нём времени (<see cref="Viewer.Output"/>). Ничего не играет вслух.
    /// </summary>
    public bool Audio { get; init; }
}

/// <summary>
/// Клиент игрока поверх сценария (docs/dotnet-n6.md, Н6-6, Н6.3): настоящий <see cref="ClientGame"/> с модом base в
/// режиме CLR (по умолчанию; <c>KHAROS_MODS=wasm</c> — client.wasm) на реплике хоста <see cref="Sim"/>. Хост шлёт ему
/// приветствие, снимок и дельты настоящим путём протокола (HostSession, VisionView, транспорт в памяти); приказы
/// клиента уходят хосту перед следующим тиком. Окна, звука и видеокарты нет (Audio = null); кадр картинкой — по
/// желанию, <see cref="Render"/> на <c>GpuContext.TryCreateHeadless</c>.
/// <code>
/// using var sim = Sim.Create(new SimOptions { Game = "skirmish" });
/// var viewer = sim.Viewer(1);
/// viewer.Run(1);                              // тики хоста и кадры клиента: 20 и 60 в секунду
/// viewer.Click("cell:1");                     // кнопка интерфейса по id
/// Assert.Contains("Развернуть", viewer.Ui.Texts());
/// Assert.Contains("deploy", viewer.Sent.Names);
/// Assert.True(viewer.Sounds.Played("click"));
/// </code>
/// Часы клиента — свои (<see cref="Time"/>, шаг <see cref="ViewerOptions.FrameStep"/>), не настенные: кадры повторяемы.
/// </summary>
public sealed class Viewer : IDisposable
{
    private readonly Sim _sim;
    private readonly Connection _connection;
    private readonly ViewerOptions _options;
    private readonly List<FrameRecord> _records = [];
    private Renderer? _renderer;
    private GpuContext? _rendererGpu;
    private bool _disposed;

    internal Viewer(Sim sim, uint player, ViewerOptions options)
    {
        var mode = options.Mode ?? Sim.DefaultMode;
        if (Unavailable(mode) is { } reason)
            throw new SimUnavailableException(reason);
        _sim = sim;
        _options = options;
        Engine = options.Audio ? new Kharos.Audio.AudioEngine(new Kharos.Audio.AudioEngineOptions { Seed = 3 }) : null;
        Player = player;
        Mode = mode;
        var (hostEnd, clientEnd) = MemoryTransport.Pair();
        var transport = new SentTransport(clientEnd, Sent);
        _connection = sim.Connect(hostEnd, player);
        try
        {
            Game = new ClientGame(sim.Host.Content, transport, new ClientGameOptions
            {
                Audio = Engine,
                ModMode = mode,
                ModuleOptions = new GameModuleOptions { CallTimeoutMs = sim.Options.CallTimeoutMs, Log = entry => ModLog.Add($"{entry.ModId}: {entry.Message}") },
                Log = Log.Add,
                ConnectTimeout = TimeSpan.FromSeconds(30),
            });
        }
        catch
        {
            sim.Disconnect(this, _connection);
            throw;
        }
        Game.Scene.Recorder = _records.Add;
        Game.AfterMods = Direct;
        Input = new ClientInput { Width = options.Width, Height = options.Height };
        Ui = new ViewerUi(Game.Gui);
    }

    /// <summary>Почему клиента в этом режиме не поднять (нет Base.Client.dll или client.wasm); null — можно.</summary>
    public static string? Unavailable(ModMode mode) => Sim.Unavailable(mode) ?? mode switch
    {
        ModMode.Wasm when !File.Exists(Path.Combine(Sim.ModDir, "client.wasm")) =>
            "mods/base/client.wasm не собран: pwsh dotnet/tools/build-mods.ps1 -Mod base",
        ModMode.Clr when ClrModule.FindClient(Sim.ModDir) is null => "Base.Client.dll не собран: dotnet build dotnet/Kharos.slnx",
        _ => null,
    };

    /// <summary>Партия, на которую смотрит зритель.</summary>
    public Sim Sim => _sim;

    /// <summary>Игрок клиента.</summary>
    public uint Player { get; }

    /// <summary>Движок звука без устройства (<see cref="ViewerOptions.Audio"/>); null — звука нет.</summary>
    public Kharos.Audio.AudioEngine? Engine { get; }

    /// <summary>Левый канал всего, что отрендерил движок по кадрам зрителя (с <see cref="ViewerOptions.Audio"/>).</summary>
    public List<float> Output { get; } = [];

    /// <summary>Как исполняется код клиентского мода.</summary>
    public ModMode Mode { get; }

    /// <summary>Клиент партии: реплика, моды, интерфейс, кадр рендера.</summary>
    public ClientGame Game { get; }

    /// <summary>Реплика мира у клиента (то, что ему прислал хост).</summary>
    public GameWorld World => Game.Replica.World!;

    /// <summary>Ввод по умолчанию для <see cref="Frame"/>: экран, указатель, клавиши.</summary>
    public ClientInput Input { get; set; }

    /// <summary>Часы клиента, секунды: растут на <see cref="ViewerOptions.FrameStep"/> за кадр.</summary>
    public double Time { get; private set; }

    /// <summary>Где мод в последнем кадре просил поставить чат оболочки (ShellAction.ChatAt: «left bottom»); не просил — null.</summary>
    public string? ChatAt { get; private set; }

    /// <summary>Кадров сделано.</summary>
    public int Frames { get; private set; }

    /// <summary>Камера поверх модов (сверка кадров, съёмка): ставится каждый кадр после модов; null — как решили моды.</summary>
    public CameraState? Camera { get; set; }

    /// <summary>Атмосфера поверх модов (свет, ветер, осадки); null — как решили моды.</summary>
    public AtmosphereState? Atmosphere { get; set; }

    /// <summary>Сбои клиента: моды и разбор (то же, что <c>Game.Errors</c>, без предела).</summary>
    public List<string> Log { get; } = [];

    /// <summary>Журнал клиентских модов (Host.Log мода): «мод: текст».</summary>
    public List<string> ModLog { get; } = [];

    /// <summary>Звуковые команды модов (Play, SoundAt, Loop, Voice) по порядку, со всех кадров.</summary>
    public ViewerSounds Sounds { get; } = new();

    /// <summary>Рисунки модов последнего кадра: записи 0x20–0x26.</summary>
    public ViewerDraws Draws { get; private set; } = new();

    /// <summary>Интерфейс последнего кадра: тексты, кнопки, узлы по id.</summary>
    public ViewerUi Ui { get; }

    /// <summary>Приказы, что клиент послал хосту (от модов и оболочки), по порядку.</summary>
    public ViewerSent Sent { get; } = new();

    /// <summary>Кадр последнего <see cref="Frame"/>.</summary>
    public RenderFrame Last => Game.Frame;

    // ===== Кадры =====

    /// <summary>Кадр клиента: часы вперёд на шаг, приём от хоста, моды, сборка кадра. Время хоста не идёт.</summary>
    public RenderFrame Frame(ClientInput? input = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return FrameAt(Time + _options.FrameStep, input);
    }

    /// <summary>
    /// Кадр клиента с часами ровно time (сверка кадров: замороженные экранные часы сцены, как аргумент time проходов TS);
    /// дальше <see cref="Frame"/> идёт от них.
    /// </summary>
    public RenderFrame FrameAt(double time, ClientInput? input = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Time = time;
        _records.Clear();
        ChatAt = null;
        var frame = Game.Step((input ?? Input) with { Time = Time });
        Frames++;
        // Зритель — оболочка тестов: просьбу мода начать заново исполняет, как ShellApp (остальные просьбы — тестам).
        // Место чата мод просит каждый кадр: последнее — в ChatAt, а в списке оно копилось бы.
        for (var i = Game.ShellRequests.Count - 1; i >= 0; i--)
            if (Game.ShellRequests[i].Action == ClientGame.ShellChatAt)
            {
                ChatAt ??= Game.ShellRequests[i].Arg;
                Game.ShellRequests.RemoveAt(i);
            }
        if (Game.ShellRequests.RemoveAll(request => request.Action == ClientGame.ShellRespawn) is var respawns and > 0)
            for (var i = 0; i < respawns; i++)
                Game.Respawn();
        Draws = ViewerDraws.Of(_records, Game.Scene);
        Sounds.Take(_records, Frames);
        if (Engine is not null)
            Output.AddRange(Engine.Render((int)Math.Round(_options.FrameStep * Kharos.Audio.AudioFormat.SampleRate)).Left);
        return frame;
    }

    /// <summary>Несколько кадров с одним вводом, время хоста не идёт.</summary>
    public RenderFrame Step(int frames = 1, ClientInput? input = null)
    {
        var frame = Game.Frame;
        for (var i = 0; i < frames; i++)
            frame = Frame(input);
        return frame;
    }

    /// <summary>
    /// Партия и клиент вместе: на каждый тик хоста — столько кадров, сколько их помещается в 0,05 с (при 60 к/с — три).
    /// </summary>
    public RenderFrame Run(double seconds, ClientInput? input = null)
    {
        var frame = Game.Frame;
        var perTick = Math.Max(1, (int)Math.Round(Sim.Dt / _options.FrameStep));
        for (var tick = 0; tick < Sim.TicksIn(seconds); tick++)
        {
            _sim.Advance();
            for (var i = 0; i < perTick; i++)
                frame = Frame(input);
        }
        return frame;
    }

    /// <summary>Как <see cref="Run"/>, пока условие не выполнится (проверка перед каждым тиком); false — не дождались.</summary>
    public bool Until(Func<bool> done, double limit = 60, ClientInput? input = null)
    {
        for (var tick = 0; tick < Sim.TicksIn(limit); tick++)
        {
            if (done())
                return true;
            Run(Sim.Dt, input);
        }
        return done();
    }

    // ===== Ввод =====

    /// <summary>Щелчок по узлу интерфейса id (середина его места в прошлом кадре): наведение, нажатие, отпускание.</summary>
    public void Click(string id, PointerButtons button = PointerButtons.Left, bool shift = false)
    {
        if (Ui.Center(id) is not { } at)
            throw new InvalidOperationException($"в интерфейсе нет узла {id}; есть: {string.Join(", ", Ui.Ids().Take(40))}");
        ClickAt(at, button, shift);
    }

    /// <summary>Щелчок в точке экрана (логические пиксели): четыре кадра с одним указателем.</summary>
    public void ClickAt((float X, float Y) at, PointerButtons button = PointerButtons.Left, bool shift = false)
    {
        var input = Input with { Pointer = at, Shift = shift };
        Frame(input);
        Frame(input with { Pressed = button, Down = button });
        Frame(input with { Released = button });
        Frame(input);
    }

    /// <summary>Щелчок по точке мира (тайлы) через камеру прошлого кадра.</summary>
    public void ClickWorld(double x, double y, PointerButtons button = PointerButtons.Left, bool shift = false) =>
        ClickAt(Game.Frame.View.ToScreen((float)x, (float)y), button, shift);

    /// <summary>Рамка левой кнопкой от from до to (экран).</summary>
    public void Drag((float X, float Y) from, (float X, float Y) to)
    {
        Frame(Input with { Pointer = from });
        Frame(Input with { Pointer = from, Pressed = PointerButtons.Left, Down = PointerButtons.Left });
        Frame(Input with { Pointer = to, Down = PointerButtons.Left });
        Frame(Input with { Pointer = to, Down = PointerButtons.Left });
        Frame(Input with { Pointer = to, Released = PointerButtons.Left });
        Frame(Input with { Pointer = to });
    }

    /// <summary>Нажатие клавиши («t», «escape», «x»): кадр с нажатием и кадр после.</summary>
    public void Press(string key)
    {
        Frame(Input with { Hits = [key], Keys = [key] });
        Frame();
    }

    // ===== Картинка =====

    /// <summary>
    /// Кадр картинкой RGBA8 (сверху вниз) размером экрана: последний кадр клиента на видеокарте без окна. Рендер
    /// создаётся на gpu один раз и живёт со зрителем.
    /// </summary>
    public byte[] Render(GpuContext gpu)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_renderer is null || _rendererGpu != gpu)
        {
            _renderer?.Dispose();
            _renderer = new Renderer(gpu);
            _rendererGpu = gpu;
        }
        return Render(_renderer);
    }

    /// <summary>
    /// То же на готовом рендере (его конвейеры собираются долго — на программном адаптере секунды: сцены подряд берут
    /// один рендер). Размер рендера ставится по экрану зрителя.
    /// </summary>
    public byte[] Render(Renderer renderer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (width, height) = ((int)MathF.Round(Input.Width * Input.Scale), (int)MathF.Round(Input.Height * Input.Scale));
        if (renderer.Width != width || renderer.Height != height)
            renderer.Resize(width, height);
        Game.Upload(renderer);
        renderer.Render(Game.Frame);
        return renderer.ReadPixels();
    }

    /// <summary>Видеокарта без окна для <see cref="Render"/>; нет адаптера — null и причина (тест — пропуск).</summary>
    public static GpuContext? TryGpu(out string reason) => GpuContext.TryCreateHeadless(out reason);

    // ===== Внутреннее =====

    /// <summary>После модов: камера и атмосфера сцены поверх их решения.</summary>
    private void Direct(ClientScene scene)
    {
        if (Camera is { } camera)
            scene.Camera = camera;
        if (Atmosphere is { } atmosphere)
            scene.Atmosphere = atmosphere;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _renderer?.Dispose();
        Game.Dispose();
        _sim.Disconnect(this, _connection);
    }

    /// <summary>Транспорт клиента, что помнит его приказы.</summary>
    private sealed class SentTransport(MemoryTransport inner, ViewerSent sent) : ITransport
    {
        public bool Closed => inner.Closed;

        public void Send(byte[] message)
        {
            if (message.Length > 0 && message[0] == (byte)MessageType.Order)
                sent.Add(DeltaCodec.ReadOrder(message));
            inner.Send(message);
        }

        public bool TryReceive([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out byte[]? message) => inner.TryReceive(out message);

        public ValueTask<byte[]?> ReceiveAsync(CancellationToken cancel = default) => inner.ReceiveAsync(cancel);

        public void Dispose() => inner.Dispose();
    }
}

/// <summary>Приказы клиента хосту по порядку: имя и payload MemoryPack.</summary>
public sealed class ViewerSent
{
    private readonly List<(string Name, byte[] Payload)> _orders = [];

    internal void Add((string Name, byte[] Payload) order) => _orders.Add(order);

    /// <summary>Все приказы.</summary>
    public IReadOnlyList<(string Name, byte[] Payload)> All => _orders;

    /// <summary>Имена приказов по порядку.</summary>
    public IReadOnlyList<string> Names => [.. _orders.Select(order => order.Name)];

    public int Count => _orders.Count;

    /// <summary>Приказы класса T (имя — его константа Name), разобранные.</summary>
    public List<T> Of<T>() where T : class
    {
        var name = typeof(T).GetField("Name", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() as string
            ?? throw new InvalidOperationException($"{typeof(T).Name}: нет константы Name — это не приказ из Base.Shared");
        return [.. _orders.Where(order => order.Name == name).Select(order => MemoryPackSerializer.Deserialize<T>(order.Payload)!)];
    }

    /// <summary>Забыть посланное.</summary>
    public void Clear() => _orders.Clear();
}
