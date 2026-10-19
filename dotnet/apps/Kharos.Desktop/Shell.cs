using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kharos.Audio;
using Kharos.Client.Game;
using Kharos.Client.Net;
using Kharos.Client.Shell;
using Kharos.Client.Ui;
using Kharos.Content;
using Kharos.Gpu;
using Kharos.Host;
using Kharos.I18n;
using Kharos.Protocol;
using Silk.NET.Input;
using Silk.NET.Input.Glfw;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;
using HostGame = Kharos.Host.Host;

namespace Kharos.Desktop;

/// <summary>
/// Оболочка десктопа (Р10, Н6.2): окно Silk.NET без графического API, поверхность WebGPU, звук, ввод — и
/// <see cref="ShellApp"/> из Kharos.Client: главное меню, партии (одиночная — хост в своём потоке и клиент через
/// транспорт в памяти, или сервер), окна поверх партии. Кадр: ввод → <see cref="ShellApp.Step"/> → Upload → Render.
/// Масштаб: физический размер — кадровый буфер, логический — он, делённый на масштаб экрана (DPI или настройку
/// игрока), он же идёт в <c>View.Scale</c>. Хранилище игрока — файлы в профиле (настройки, слоты, громкости, настройки
/// модов). KHAROS_SERVER и KHAROS_GAME начинают партию сразу, мимо меню. KHAROS_SHOT вместо игры снимает кадр в PNG с
/// невидимым окном и выходит: без KHAROS_SHOT_SCENE — партия, KHAROS_SHOT_SCENE=menu (menu:new, menu:saves,
/// menu:network, menu:settings, menu:about) — экран меню; имя сценки витрины (mining, construction, defense, battle, armor,
/// infantry, air) — эта сценка за главным меню; editor — редактор нового мира (слот только в памяти).
/// KHAROS_SHOT_HOUR — час начала (настройка партии weather.startHour, сутки стоят).
/// </summary>
public static class Shell
{
    private const double ShotFrameSeconds = 1.0 / 60;

    /// <summary>Проверка тестов: вызывается каждый кадр записи после шага оболочки.</summary>
    internal static Action<ShellApp>? Probe { get; set; }

    /// <summary>Сколько ждать партию снимка, прежде чем сдаться.</summary>
    private static readonly TimeSpan ShotTimeout = TimeSpan.FromMinutes(3);

    public static int Run(DesktopOptions options)
    {
        // Платформы окон и ввода регистрируются явно: поиск по сборкам через отражение не годится для NativeAOT.
        GlfwWindowing.RegisterPlatform();
        GlfwInput.RegisterPlatform();

        var shot = options.Shot;
        // Снимок — без следов в профиле игрока: хранилище в памяти.
        IStorage storage = shot is null ? new FileStorage(Path.Combine(FileStorage.ProfileDirectory, "storage")) : new MemoryStorage();
        var prefs = new ShellPrefs(storage);
        var windowOptions = WindowOptions.Default with
        {
            Title = "Kharos",
            Size = shot is null ? new Vector2D<int>(1280, 800) : new Vector2D<int>(shot.Size.Width, shot.Size.Height),
            API = GraphicsAPI.None,
            VSync = shot is null && prefs.VSync,
            IsVisible = shot is null,
            WindowBorder = WindowBorder.Resizable,
        };
        using var window = Window.Create(windowOptions);
        window.Initialize();
        var input = new InputMap();
        using var context = window.CreateInput();
        input.Attach(context);
        window.FocusChanged += focused =>
        {
            input.Focused = focused;
            if (!focused)
                input.ReleaseAll();
        };

        using var gpu = GpuContext.CreateForWindow(window, vsync: shot is null && prefs.VSync);
        DesktopLog.Write($"видеокарта: {gpu.AdapterName}, {gpu.Backend}");
        using var renderer = new Renderer(gpu);

        AudioEngine? audio = null;
        OpenAlOutput? output = null;
        if (options.Record is not null)
            audio = options.Audio ? RecordSession.CreateAudio() : null; // запись: звук без устройства, его рендерит запись
        else if (options.Audio)
        {
            audio = new AudioEngine();
            output = OpenAlOutput.TryOpen(audio, out var audioError);
            if (output is null)
            {
                DesktopLog.Write($"звука не будет: {audioError}");
                audio = null;
            }
        }

        using var fonts = Fonts.Load();
        var launcher = new DesktopLauncher(options, fonts, audio, storage);
        var app = new ShellApp(new ShellAppOptions
        {
            Fonts = fonts, Storage = storage, Launcher = launcher, Audio = audio, Log = DesktopLog.Write,
            SystemLang = CultureInfo.CurrentUICulture.Name is { Length: > 0 } culture ? culture : Environment.GetEnvironmentVariable("LANG"),
        });
        launcher.Lang = () => app.Lang;
        if (input.Clipboard is { } clipboard)
            app.Clipboard = clipboard;
        app.OnFullscreen = on => window.WindowState = on ? WindowState.Fullscreen : WindowState.Normal;
        app.Menu.OpenUrl = OpenUrl;
        app.Menu.ExportFile = ExportFile;
        app.Background = ShowcaseFor(options, launcher);
        app.Recording = options.Record is not null;
        if (shot is null && prefs.Fullscreen)
            window.WindowState = WindowState.Fullscreen;
        try
        {
            if (!Begin(options, app))
                return 1;
            using var recorder = options.Record is { } record ? new RecordSession(record, audio) : null;
            return Loop(options, window, input, renderer, app, recorder);
        }
        finally
        {
            // Партия закрывается первой (с сохранением слота), потом звук.
            app.Dispose();
            output?.Dispose();
        }
    }

    /// <summary>Что открыть первым: партию мимо меню (сервер, KHAROS_GAME, снимок партии) или меню.</summary>
    private static bool Begin(DesktopOptions options, ShellApp app)
    {
        var shot = options.Shot;
        if (options.Server is not null)
        {
            app.Start(new ServerLaunch(options.Server, options.Name ?? "", options.Password));
            return true;
        }
        if (shot?.Scene is { } scene && MenuScene(scene) is { } screen)
        {
            app.Menu.Open(screen);
            DesktopLog.Write($"снимок: меню, экран {screen}");
            return true;
        }
        if (shot?.Scene is { } other && ShowcaseBackground.IsScene(other))
        {
            DesktopLog.Write($"снимок: сценка витрины «{other}» за главным меню");
            return true;
        }
        if (shot?.Scene is { } edit && string.Equals(edit, "editor", StringComparison.OrdinalIgnoreCase))
        {
            // Снимок редактора: мир нового слота, который живёт только в памяти (на диск слот не пишется).
            app.Start(new EditorLaunch(new SaveSlot("shot-editor", "editor", 0, 0, 0, 128, null, 1)));
            DesktopLog.Write("снимок: редактор нового мира");
            return true;
        }
        if (shot?.Scene is { } unknown)
            DesktopLog.Write($"снимок: нет сценки «{unknown}» — снимается партия");
        if (options.Game is not null || shot is not null)
        {
            var settings = shot?.Hour is { } hour
                ? $"{{\"weather\":{{\"startHour\":{hour.ToString(CultureInfo.InvariantCulture)},\"dayLength\":0}}}}"
                : null;
            if (shot?.Hour is { } h)
                DesktopLog.Write($"снимок: час {h.ToString(CultureInfo.InvariantCulture)} (настройка партии weather.startHour, сутки стоят)");
            app.Start(new GameLaunch(options.Game, settings));
        }
        return true;
    }

    /// <summary>Фон меню — витрина; в снимке — только когда названа её сценка (KHAROS_SHOT_SCENE), в записи его нет.</summary>
    private static IMenuBackground? ShowcaseFor(DesktopOptions options, IGameLauncher launcher) =>
        options.Record is not null ? null
        : options.Shot is null ? new ShowcaseBackground(launcher)
        : options.Shot.Scene is { } scene && ShowcaseBackground.IsScene(scene) ? new ShowcaseBackground(launcher) { Forced = scene.ToLowerInvariant(), Hour = options.Shot.Hour }
        : null;

    /// <summary>Экран меню снимка: «menu», «menu:new» и т. д.; не меню — null.</summary>
    internal static MenuScreen? MenuScene(string scene) => scene.ToLowerInvariant() switch
    {
        "menu" or "menu:main" => MenuScreen.Main,
        "menu:new" => MenuScreen.New,
        "menu:saves" => MenuScreen.Saves,
        "menu:network" => MenuScreen.Network,
        "menu:settings" => MenuScreen.Settings,
        "menu:about" => MenuScreen.About,
        _ => null,
    };

    private static int Loop(DesktopOptions options, IWindow window, InputMap input, Renderer renderer, ShellApp app, RecordSession? recorder)
    {
        var clock = Stopwatch.StartNew();
        var shot = options.Shot;
        var frames = 0;
        var last = 0.0;
        while (!window.IsClosing)
        {
            window.DoEvents();
            var buffer = window.FramebufferSize;
            var size = window.Size;
            if (buffer.X <= 0 || buffer.Y <= 0 || size.X <= 0 || size.Y <= 0)
            {
                // Окно свёрнуто: рисовать некуда.
                Thread.Sleep(50);
                continue;
            }
            // Окно → физические: кадровый буфер (на Retina он вдвое больше окна). Физические → логические: масштаб.
            var toPhysical = (float)buffer.X / size.X;
            var scale = options.Scale ?? (shot is not null ? 1 : app.Prefs.Scale > 0 ? app.Prefs.Scale : Math.Max(toPhysical > 1.001f ? toPhysical : Dpi.Of(window), 1));
            var toLogical = toPhysical / scale;
            if (shot?.Pointer is { } at)
                input.PlacePointer(at.X / toLogical, at.Y / toLogical);

            // Снимок: партия считается от готовности (хост поднимается в своём потоке), меню — сразу.
            var waiting = app.Session is { State: SessionState.Connecting } || (shot is not null && app.Background is { Ready: false });
            renderer.Resize(buffer.X, buffer.Y);
            if (recorder is not null && !waiting)
                recorder.Before(app, frames);
            var time = shot is { FixedClock: true } ? frames * (options.Record is { } rec ? 1.0 / rec.Fps : ShotFrameSeconds) : clock.Elapsed.TotalSeconds;
            var frame = app.Step(input.Take(time, buffer.X / scale, buffer.Y / scale, scale, toLogical));
            app.Upload(renderer);
            renderer.Render(frame);
            Probe?.Invoke(app);
            if (recorder is not null && !waiting && recorder.After(renderer, frames))
            {
                recorder.Finish();
                return 0;
            }
            if (!waiting)
                frames++;

            if (shot is not null)
            {
                if (app.Session is { State: SessionState.Failed or SessionState.Password } failed)
                {
                    DesktopLog.Write($"снимок: партия не состоялась: {failed.Error}");
                    return 1;
                }
                if (recorder is null && frames >= shot.Frames)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(shot.Path) ?? ".");
                    renderer.SavePng(shot.Path);
                    ReportShot(app, frames, buffer);
                    return 0;
                }
                if (clock.Elapsed > ShotTimeout && (recorder is null || waiting))
                {
                    DesktopLog.Write("снимок: партия не поднялась за отведённое время");
                    return 1;
                }
                // Хост идёт по часам, а не по кадрам: без паузы 60 кадров пролетели бы за миг игры.
                var wait = frames * (recorder is { } paced ? 1.0 / options.Record!.Fps : ShotFrameSeconds) - clock.Elapsed.TotalSeconds;
                // Запись партии в памяти идёт ручными тиками и не ждёт часов; сетевая — по часам сервера.
                if (recorder is not null && app.Session is not NetSession)
                    wait = 0;
                if (wait > 0 && !waiting)
                    Thread.Sleep(TimeSpan.FromSeconds(wait));
                else if (waiting)
                    Thread.Sleep(5);
                continue;
            }
            // Предел кадров из настроек: остаток кадра — сон.
            if (app.Prefs.FpsLimit > 0)
            {
                var period = 1.0 / app.Prefs.FpsLimit;
                var rest = last + period - clock.Elapsed.TotalSeconds;
                if (rest > 0.001)
                    Thread.Sleep(TimeSpan.FromSeconds(rest));
            }
            last = clock.Elapsed.TotalSeconds;
        }
        return 0;
    }

    private static void ReportShot(ShellApp app, int frames, Vector2D<int> buffer)
    {
        if (app.Session?.Game is not { } game)
        {
            DesktopLog.Write($"снимок: {buffer.X}x{buffer.Y}, кадров {frames}, меню: {app.Menu.Screen}");
            return;
        }
        var ticks = app.Session is LocalSession local ? local.Ticks : 0;
        DesktopLog.Write($"снимок: {buffer.X}x{buffer.Y}, кадров {frames}, тиков {ticks}, спрайтов {game.Stats.Sprites}, ошибок клиента {game.Errors.Count}");
        foreach (var error in game.Errors.Take(5))
            DesktopLog.Write($"ошибка клиента: {error}");
    }

    /// <summary>Адрес — в системный браузер (по щелчку игрока по ссылке меню).</summary>
    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            DesktopLog.Write($"ссылка не открылась: {error.Message}");
        }
    }

    /// <summary>Файл переноса сохранения — в «Документы/Kharos»; путь записанного или null.</summary>
    private static string? ExportFile(string name, byte[] bytes)
    {
        try
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var folder = Path.Combine(documents.Length > 0 ? documents : FileStorage.ProfileDirectory, "Kharos");
            var clean = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            var path = Path.Combine(folder, clean.Length > 0 ? clean : "save.kharos");
            SaveFile.WriteAtomic(path, bytes);
            DesktopLog.Write($"файл сохранения: {path}");
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DesktopLog.Write($"файл сохранения не записан: {error.Message}");
            return null;
        }
    }
}

/// <summary>Партии десктопа: локальная (хост в своём потоке) и на сервере.</summary>
internal sealed class DesktopLauncher(DesktopOptions options, Fonts fonts, AudioEngine? audio, IStorage storage) : IGameLauncher
{
    private GameContent? _content;
    private static readonly Texts ShellTexts = Texts.Shell();

    /// <summary>Язык игрока для ошибок подключения.</summary>
    public Func<string> Lang { get; set; } = () => Langs.Default;

    public string T(string key, params ReadOnlySpan<(string Name, object Value)> args) => ShellTexts.T(Lang(), key, args);

    public ClientGameOptions ClientOptions => new() { Fonts = fonts, Audio = audio, Log = DesktopLog.Write, Storage = storage };

    /// <summary>Свои моды — один раз на процесс.</summary>
    public GameContent Content => _content ??= GameContent.Load([.. options.ModRoots.SelectMany(ModPackage.Discover)]);

    public DesktopOptions Options => options;

    /// <summary>
    /// Партия, когда её не назвали (KHAROS_GAME, слот): партия мода base (skirmish), как «Новая игра» в TS; без base —
    /// game последнего мода. Пример hello, что лежит рядом в dotnet/mods, так не перехватывает меню.
    /// </summary>
    public string? DefaultGame => options.Game ?? Content.Mods.LastOrDefault(mod => mod.Name == BaseMod && mod.Game is not null)?.Game ?? Content.DefaultGame;

    private const string BaseMod = "base";

    public IGameSession Start(Launch launch) => launch switch
    {
        ServerLaunch server => new NetSession(this, server),
        SlotLaunch or GameLaunch => new LocalSession(this, launch, new SaveSlots(storage)),
        EditorLaunch editor => new EditorSession(this, editor, new SaveSlots(storage)),
        _ => throw new ArgumentException($"не знаю, как начать {launch}"),
    };
}

/// <summary>
/// Одиночная партия: хост поднимается в своём потоке (моды хоста грузятся не в кадре), клиент — когда он готов.
/// Партия слота продолжает сохранённый мир, а новый заводит по настройкам слота; уход сохраняет мир в слот.
/// </summary>
internal sealed class LocalSession : IGameSession
{
    private readonly DesktopLauncher _launcher;
    private readonly SaveSlots _slots;
    private readonly Task<LocalGame> _start;
    private LocalGame? _local;

    public LocalSession(DesktopLauncher launcher, Launch launch, SaveSlots slots)
    {
        (_launcher, _slots, Launch) = (launcher, slots, launch);
        var (game, settings, save, seed) = launch switch
        {
            SlotLaunch slot => (slot.Slot.Game, slot.Slot.Settings, slots.Load(slot.Slot.Id), (ulong)Math.Abs(slot.Slot.Seed) + 1),
            GameLaunch direct => (direct.Game, direct.Settings, null, 1UL),
            _ => (null, null, null, 1UL),
        };
        Status = launch is SlotLaunch named ? named.Slot.Name : game;
        _start = Task.Run(() =>
        {
            var content = launcher.Content;
            // Сохранённый мир помнит свои настройки; новый — по настройкам слота.
            var hostOptions = new HostOptions
            {
                Game = game ?? launcher.DefaultGame,
                Settings = save is null && settings is not null ? JsonDocument.Parse(settings).RootElement.Clone() : null,
                Seed = seed,
                Log = DesktopLog.Write,
            };
            return LocalGame.Start(() => save is null ? new HostGame(content, hostOptions) : HostGame.Load(content, save, hostOptions), manual: launcher.Options.Record is not null);
        });
    }

    public Launch Launch { get; }
    public SessionState State { get; private set; } = SessionState.Connecting;
    public string? Status { get; }
    public string? Error { get; private set; }
    public bool WrongPassword => false;
    public ClientGame? Game { get; private set; }
    public long Ticks => _local?.Ticks ?? 0;

    /// <summary>Длина тика хоста, мс (запись роликов).</summary>
    public double StepMs => _local?.Step.TotalMilliseconds ?? 50;

    /// <summary>Запись роликов: довести хост до тика (ручной ход).</summary>
    public void Advance(long ticks) => _local?.Advance(ticks);

    public void Poll()
    {
        if (State == SessionState.Failed)
            return;
        if (Game is null)
        {
            if (!_start.IsCompleted)
                return;
            if (_start.Exception is { } failure)
            {
                Fail($"хост не запустился: {failure.GetBaseException().Message}");
                return;
            }
            _local = _start.Result;
            try
            {
                Game = new ClientGame(_launcher.Content, _local.Client, _launcher.ClientOptions);
                State = SessionState.Ready;
            }
            catch (ClientException error)
            {
                Fail(error.Message);
            }
            return;
        }
        if (_local?.Failure is { } stopped)
            Fail($"хост остановился: {stopped.Message}");
    }

    private void Fail(string error)
    {
        DesktopLog.Write(error);
        (Error, State) = (error, SessionState.Failed);
    }

    public void Retry(string password) { }

    public void Leave()
    {
        if (Launch is not SlotLaunch slot || _local is not { } local || Game is null)
            return;
        local.Stop();
        if (local.Host is not { } host || local.Failure is not null)
            return;
        var save = host.Save();
        if (_slots.Store(slot.Slot.Id, save))
            DesktopLog.Write($"мир сохранён в слот «{slot.Slot.Name}»: {save.Length / 1024} КБ, тик {host.Tick}");
    }

    public void Dispose()
    {
        // Клиент закрывается первым, потом хост: клиент ещё читает транспорт.
        Game?.Dispose();
        Game = null;
        if (_local is not null)
            _local.Dispose();
        else
            _start.ContinueWith(task => task.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
    }
}

/// <summary>
/// Партия на сервере: рукопожатие, моды сервера (из кэша или скачать) и снимок — в фоне; отказ с паролем — спросить
/// пароль и подключиться снова. Токен из приветствия запоминается (сервер узнает игрока в следующий раз).
/// </summary>
internal sealed class NetSession : IGameSession
{
    private readonly DesktopLauncher _launcher;
    private readonly ServerLaunch _server;
    private Task<GameConnector?>? _connect;
    private GameConnector? _connector;
    private string? _password;
    private bool _disposed;

    public NetSession(DesktopLauncher launcher, ServerLaunch server)
    {
        (_launcher, _server, Launch, _password) = (launcher, server, server, server.Password);
        Connect();
    }

    public Launch Launch { get; }
    public SessionState State { get; private set; } = SessionState.Connecting;
    public string? Status { get; private set; }
    public string? Error { get; private set; }
    public bool WrongPassword { get; private set; }
    public ClientGame? Game { get; private set; }

    private void Connect()
    {
        State = SessionState.Connecting;
        Status = _server.Url;
        var options = _launcher.Options with { Server = _server.Url, Name = _server.Name.Length > 0 ? _server.Name : null, Password = _password };
        var local = NetGame.LocalMods(options);
        _connect = Task.Run(() =>
        {
            if (NetGame.Connect(options) is not { } transport)
                return null;
            var connector = new GameConnector(transport, new ContentCache(NetGame.CacheDirectory), local);
            try
            {
                connector.Wait(NetGame.EnterTimeout);
            }
            catch (ClientException) when (connector.PasswordNeeded || connector.Refused is not null || transport.Closed)
            {
            }
            return connector;
        });
    }

    public void Poll()
    {
        if (State is SessionState.Failed or SessionState.Password)
            return;
        if (Game is not null)
        {
            if (Game.Refused is { } refused)
                Fail(refused);
            else if (_connector?.Transport is WebSocketTransport { Closed: true } transport)
                Fail($"{_launcher.T("errors.lost")}: {NetGame.Describe(transport)}");
            return;
        }
        if (_connect is not { IsCompleted: true } connect)
            return;
        _connect = null;
        if (connect.Exception is { } error || connect.Result is not { } connector)
        {
            Fail(_launcher.T("errors.cannotConnect", ("url", _server.Url)));
            return;
        }
        if (connector.PasswordNeeded)
        {
            WrongPassword = _password is { Length: > 0 };
            connector.Transport.Dispose();
            State = SessionState.Password;
            return;
        }
        if (!connector.Ready)
        {
            var reason = connector.Refused ?? connector.Failure ?? (connector.Transport is WebSocketTransport socket ? NetGame.Describe(socket) : null) ?? _launcher.T("errors.lost");
            connector.Transport.Dispose();
            Fail(reason);
            return;
        }
        try
        {
            if (connector.Replica.Welcome is { Token.Length: > 0 } welcome && Uri.TryCreate(_server.Url, UriKind.Absolute, out var address))
                new PlayerTokens().Remember(address, welcome.Token);
            if (connector.FromCache + connector.Downloaded > 0)
                DesktopLog.Write($"моды сервера: из кэша {connector.FromCache}, скачано {connector.Downloaded} ({connector.Received / 1024} КБ)");
            _connector = connector;
            Game = new ClientGame(connector, _launcher.ClientOptions);
            State = SessionState.Ready;
        }
        catch (ClientException failure)
        {
            connector.Transport.Dispose();
            Fail(failure.Message);
        }
    }

    private void Fail(string error)
    {
        DesktopLog.Write($"сервер {_server.Url}: {error}");
        (Error, State) = (error, SessionState.Failed);
    }

    public void Retry(string password)
    {
        _password = password;
        Connect();
    }

    public void Leave() { }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Game?.Dispose();
        Game = null;
        _connector?.Transport.Dispose();
        // Подключение ещё идёт — его соединение закроется, когда оно кончится.
        _connect?.ContinueWith(task => task.Result?.Transport.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
    }
}

/// <summary>Масштаб экрана окна: на Windows — DPI окна к 96, иначе 1.</summary>
internal static partial class Dpi
{
    public static float Of(IWindow window)
    {
        if (!OperatingSystem.IsWindows() || window.Native?.Win32 is not { } win32)
            return 1;
        var dpi = GetDpiForWindow(win32.Hwnd);
        return dpi > 0 ? dpi / 96f : 1;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}
