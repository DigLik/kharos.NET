using Kharos.Audio;
using Kharos.Client.Game;
using Kharos.Client.Ui;
using Kharos.Gpu;
using Kharos.I18n;
using Kharos.Mod.Abi;
using RenderFrame = Kharos.Gpu.Frame;

namespace Kharos.Client.Shell;

/// <summary>Где партия: подключается, ждёт пароль, идёт или не состоялась.</summary>
public enum SessionState : byte { Connecting, Password, Ready, Failed }

/// <summary>
/// Партия, которую завела оболочка (play.ts): локальная (хост в процессе) или на сервере. Подключение идёт само
/// (<see cref="Poll"/> каждый кадр), игра появляется в <see cref="Game"/>. Уход (<see cref="Leave"/>) сохраняет мир слота.
/// </summary>
public interface IGameSession : IDisposable
{
    Launch Launch { get; }
    SessionState State { get; }

    /// <summary>Что сейчас: адрес сервера, загрузка модов.</summary>
    string? Status { get; }

    /// <summary>Почему не состоялась или остановилась (на языке игрока или как есть).</summary>
    string? Error { get; }

    /// <summary>Пароль спрошен после неверного.</summary>
    bool WrongPassword { get; }

    /// <summary>Игра; null — ещё нет.</summary>
    ClientGame? Game { get; }

    /// <summary>Продвинуть подключение; у идущей игры — проверить, жив ли хост и соединение.</summary>
    void Poll();

    /// <summary>Подключиться снова с паролем.</summary>
    void Retry(string password);

    /// <summary>Выйти: локальная партия слота сохраняется в слот.</summary>
    void Leave();
}

/// <summary>Заводит партии по запускам: десктоп — хост в процессе и сервер по сети, тесты — подделки.</summary>
public interface IGameLauncher
{
    IGameSession Start(Launch launch);
}

/// <summary>
/// Фон меню вместо ровного цвета: витрина (пакет В, Н6-4) — партия showcase без тумана со своей камерой и музыкой.
/// Оболочка зовёт <see cref="Step"/> каждый кадр меню и рисует меню поверх кадра фона.
/// </summary>
public interface IMenuBackground : IDisposable
{
    /// <summary>Кадр фона; null — фона пока нет (ровный цвет).</summary>
    RenderFrame? Step(in ClientInput input);

    /// <summary>Ресурсы фона рендеру (как <see cref="ClientGame.Upload"/>).</summary>
    void Upload(Renderer renderer);
}

/// <summary>Что нужно оболочке.</summary>
public sealed class ShellAppOptions
{
    /// <summary>Шрифты: общие у меню и партий (у рендера один атлас шрифтов).</summary>
    public required Fonts Fonts { get; init; }

    /// <summary>Хранилище игрока: настройки, слоты, громкости, настройки модов.</summary>
    public required IStorage Storage { get; init; }

    public required IGameLauncher Launcher { get; init; }

    /// <summary>Звук; null — без звука.</summary>
    public AudioEngine? Audio { get; init; }

    /// <summary>Язык системы (тег вроде «de-AT»): берётся, пока игрок язык не выбрал.</summary>
    public string? SystemLang { get; init; }

    public Action<string>? Log { get; init; }
}

/// <summary>
/// Оболочка клиента (Н6.2): главное меню, партия и окна поверх неё — меню паузы с настройками, чат, подключение,
/// пароль, ошибка. Кадр (<see cref="Step"/>): горячие клавиши оболочки (если поле ввода или модальное окно их не
/// держит), затем меню или партия; окна поверх партии описываются в её интерфейсе после модов
/// (<see cref="ClientGame.AfterMods"/>), так что модальное меню паузы забирает ввод у модов. Просьбы модов
/// (запись Shell: меню, чат с текстом, настройки) забираются каждый кадр. Окна и звука здесь нет — их даёт десктоп.
/// </summary>
public sealed class ShellApp : IDisposable
{
    private readonly ShellAppOptions _options;
    private readonly Gui _gui;
    private readonly Forms _forms;
    private readonly Words _words;
    private readonly RenderFrame _frame = new();
    private readonly Texts _shellTexts = Texts.Shell();
    private Words? _gameWords;
    private Forms? _gameForms;
    private ClientGame? _hooked;
    private string _password = "";
    private double _slideAt = double.NegativeInfinity;
    private int _previewUploaded = -1;
    private Renderer? _menuRenderer;
    private bool _cameraRestored;
    private IReadOnlyList<string> _hits = [];

    public ShellApp(ShellAppOptions options)
    {
        _options = options;
        Storage = options.Storage;
        Prefs = new ShellPrefs(Storage);
        if (!Prefs.LangChosen && Langs.FromTag(options.SystemLang) is { } system)
            Lang = system;
        Audio = new AudioPrefs(options.Audio, Storage.Get, (key, value) => Storage.Set(key, value));
        Slots = new SaveSlots(Storage);
        _gui = new Gui(options.Fonts);
        _forms = new Forms(_gui);
        _words = new Words(_shellTexts, () => Lang);
        Settings = new SettingsWindow(Prefs, Audio, _words);
        Menu = new Menu(_forms, _words, Slots, Prefs, Settings, Start);
        Chat = new ChatBox();
    }

    public IStorage Storage { get; }
    public ShellPrefs Prefs { get; }
    public AudioPrefs Audio { get; }
    public SaveSlots Slots { get; }
    public Menu Menu { get; }
    public ChatBox Chat { get; }
    public SettingsWindow Settings { get; }

    /// <summary>Интерфейс меню (у партии — свой, <see cref="ClientGame.Gui"/>).</summary>
    public Gui Gui => _gui;

    /// <summary>Язык игрока: выбранный в настройках, а до выбора — системы.</summary>
    public string Lang
    {
        get => _lang ?? Prefs.Lang;
        private set => _lang = value;
    }
    private string? _lang;

    /// <summary>Идущая или подключающаяся партия; null — меню.</summary>
    public IGameSession? Session { get; private set; }

    /// <summary>Открыто меню поверх партии (партия за ним идёт дальше, звук тише).</summary>
    public bool Paused { get; private set; }

    /// <summary>В меню паузы открыты настройки.</summary>
    public bool SettingsOpen { get; private set; }

    /// <summary>Идёт запись ролика (пакет З): флаг входа кадра модов.</summary>
    public bool Recording { get; set; }

    /// <summary>Фон меню (витрина, пакет В); null — ровный цвет.</summary>
    public IMenuBackground? Background { get; set; }

    /// <summary>Полный экран: оболочке окна — переключить (горячая клавиша или настройки).</summary>
    public Action<bool>? OnFullscreen { get; set; }

    /// <summary>Буфер обмена полей ввода (меню и партий): десктоп подставляет системный.</summary>
    public IClipboard Clipboard
    {
        get => _gui.Clipboard;
        set
        {
            _gui.Clipboard = value;
            if (Session?.Game is { } game)
                game.Clipboard = value;
        }
    }

    /// <summary>Клавиши держит поле ввода или модальное окно (меню или партии): горячие клавиши оболочки молчат.</summary>
    public bool CapturesKeys => (Session?.Game is { } game ? game.CapturesKeys : _gui.CapturesKeys) || Settings.Binding is not null;

    /// <summary>Флаги оболочки во входе кадра модов (ClientAbi.Shell*): меню поверх партии, редактор, запись.</summary>
    public byte ShellFlags =>
        (byte)((Paused ? ClientAbi.ShellMenu : 0) | (Session?.Game?.Editor == true ? ClientAbi.ShellEditor : 0) | (Recording ? ClientAbi.ShellRecording : 0));

    /// <summary>Кадр, который показан (меню, окно подключения или партия).</summary>
    public RenderFrame? Frame { get; private set; }

    /// <summary>Начать партию (меню, «Продолжить», запуск из окружения).</summary>
    public void Start(Launch launch)
    {
        Leave(save: false);
        Prefs.RememberLast(launch, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        (Paused, SettingsOpen, _password, _cameraRestored) = (false, false, "", false);
        Chat.Close();
        _options.Log?.Invoke($"оболочка: запуск {launch}");
        Session = _options.Launcher.Start(launch);
        if (_options.Audio is { } audio)
            ShellSounds.PlayOpen(audio);
    }

    /// <summary>Выйти из партии в главное меню; save — сохранить мир слота (и место камеры).</summary>
    public void Leave(bool save = true)
    {
        if (Session is not { } session)
            return;
        if (save && session.Game is { } game && session.Launch is SlotLaunch slot)
        {
            var camera = game.Scene.Camera;
            Slots.StoreCamera(slot.Slot.Id, new CameraSave(camera.X, camera.Y, camera.Zoom));
        }
        try
        {
            if (save)
                session.Leave();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _options.Log?.Invoke($"оболочка: выход из партии: {error.Message}");
        }
        session.Dispose();
        Session = null;
        _hooked = null;
        Paused = SettingsOpen = false;
        Audio.Dim = false;
        Menu.Open(MenuScreen.Main);
    }

    /// <summary>Кадр оболочки: горячие клавиши, затем меню, окно подключения или партия.</summary>
    public RenderFrame Step(ClientInput input)
    {
        input = input with { Lang = Lang };
        var hits = input.Hits;
        // Горячие клавиши оболочки — пока клавиши не держит поле ввода или модальное окно.
        if (!CapturesKeys && hits.Contains(Prefs.KeyOf(ShellKey.Fullscreen)))
        {
            Prefs.SetFullscreen(!Prefs.Fullscreen);
            OnFullscreen?.Invoke(Prefs.Fullscreen);
            hits = [.. hits.Where(key => key != Prefs.KeyOf(ShellKey.Fullscreen))];
        }
        _hits = hits;
        input = input with { Hits = hits };
        Session?.Poll();
        Frame = Session switch
        {
            { State: SessionState.Ready, Game: { } game } => PlayFrame(game, input),
            { } session => NoticeFrame(session, input),
            null => MenuFrame(input),
        };
        // Выход, заказанный меню паузы посреди кадра партии, — после кадра; показывается уже меню.
        if (_leaveRequested)
        {
            _leaveRequested = false;
            Leave(save: true);
            Frame = MenuFrame(input);
        }
        return Frame;
    }

    /// <summary>
    /// Ресурсы рендеру перед <see cref="Renderer.Render"/>: у партии — её (атлас, земля, мини-карта), у меню — скин,
    /// шрифты и схема новой игры в растре мини-карты.
    /// </summary>
    public void Upload(Renderer renderer)
    {
        if (Session is { State: SessionState.Ready, Game: { } game })
        {
            game.Upload(renderer);
            _menuRenderer = null;
            return;
        }
        if (Background is { } background && Session is null)
            background.Upload(renderer);
        if (_menuRenderer != renderer)
        {
            renderer.SetAtlas([0, 0, 0, 0], 1, 1);
            renderer.SetSkin(Skins.Pixels(), Skins.Width, Skins.Height);
            renderer.SetFog([0], 1, 1);
            renderer.ClearLand();
            _menuRenderer = renderer;
            _previewUploaded = -1;
        }
        if (_previewUploaded != Menu.Preview.Revision)
        {
            renderer.SetMinimap(Menu.Preview.Pixels, TerrainPreview.Side, TerrainPreview.Side);
            _previewUploaded = Menu.Preview.Revision;
        }
        renderer.SetFontAtlas(_options.Fonts.Atlas.Pixels, (ulong)_options.Fonts.Atlas.Generation);
    }

    // ------------------------------------------------------------------ меню

    private RenderFrame MenuFrame(ClientInput input)
    {
        var frame = Background?.Step(input) ?? Blank(input);
        BeginGui(input);
        if (Background is null || frame == _frame)
            _gui.Box(new Style { Id = "menu:backdrop", Anchor = new Anchor(Left: 0, Right: 0, Top: 0, Bottom: 0), Background = Colors.Backdrop, Passive = true });
        Menu.Draw(input.Hits);
        FinishGui(frame);
        return frame;
    }

    /// <summary>Кадр без мира: только интерфейс.</summary>
    private RenderFrame Blank(in ClientInput input)
    {
        _frame.Clear();
        _frame.View = new View(0, 0, 32, input.Width, input.Height, input.Scale);
        _frame.Time = (float)input.Time;
        _frame.Light = 1;
        return _frame;
    }

    private void BeginGui(in ClientInput input)
    {
        _gui.Begin(input.ToUi());
        _forms.Begin();
    }

    private void FinishGui(RenderFrame frame)
    {
        _gui.Finish(frame.Interface);
        Sounds(_gui, _forms, menu: true);
    }

    /// <summary>Звуки оболочки: щелчок и наведение кнопок меню, тик ползунка.</summary>
    private void Sounds(Gui gui, Forms forms, bool menu)
    {
        if (_options.Audio is not { } audio)
            return;
        if (menu && gui.PressedButton is { } disabled)
            ShellSounds.PlayClick(audio, disabled);
        else if (!menu && forms.Clicked is not null)
            ShellSounds.PlayClick(audio);
        if (menu && gui.HoveredButton)
            ShellSounds.PlayHover(audio);
        if (forms.Slid is { } share && gui.Now - _slideAt >= ShellSounds.SlideGap)
        {
            ShellSounds.PlaySlide(audio, share);
            _slideAt = gui.Now;
        }
    }

    // ------------------------------------------------------------------ подключение, пароль, ошибка

    private RenderFrame NoticeFrame(IGameSession session, ClientInput input)
    {
        var frame = Blank(input);
        BeginGui(input);
        switch (session.State)
        {
            case SessionState.Failed:
                Notice(_words["game.stopped"], () =>
                    _forms.Note(session.Error ?? "", Colors.Soft), () =>
                {
                    if (_forms.Button("play:menu", _words["common.toMenu"]))
                        Leave(save: false);
                    if (_forms.Button("play:reload", session.Launch is ServerLaunch ? _words["game.reconnect"] : _words["game.reload"]))
                        Start(session.Launch);
                });
                break;
            case SessionState.Password:
                var submit = false;
                Notice(session.WrongPassword ? _words["game.wrongPassword"] : _words["game.passwordNeeded"], () =>
                {
                    var field = _forms.TextField("play:password", _password, 240, _words["game.password"], password: true, focus: true);
                    _password = field.Value;
                    if (field.Keys.Contains("enter") && _password.Length > 0)
                        submit = true;
                }, () =>
                {
                    if (_forms.Button("play:cancel", _words["common.cancel"]))
                        Leave(save: false);
                    if (_forms.Button("play:connect", _words["common.connect"], disabled: _password.Length == 0))
                        submit = true;
                });
                if (submit && Session is { } current)
                {
                    current.Retry(_password);
                    _password = "";
                }
                break;
            default:
                Notice(session.Launch is ServerLaunch ? _words["game.connecting"] : "KHAROS", () =>
                    _forms.Note(session.Status ?? (session.Launch as ServerLaunch)?.Url ?? "", Colors.Soft), () =>
                {
                    if (_forms.Button("play:abort", _words["common.cancel"]))
                        Leave(save: false);
                });
                break;
        }
        FinishGui(frame);
        return frame;
    }

    /// <summary>Окно посреди затемнённого экрана: подключение, пароль, ошибка.</summary>
    private void Notice(string title, Action body, Action actions) =>
        _gui.Box(new Style
        {
            Id = "play:notice", Anchor = new Anchor(Left: 0, Right: 0, Top: 0, Bottom: 0), Background = Colors.Shade,
            Direction = Direction.Column, Align = Align.Center, Justify = Align.Center, Gap = 12, Padding = 24,
        }, () =>
        {
            _gui.Text(title, new TextStyle { Color = Colors.Text });
            _gui.Box(new Style { Direction = Direction.Column, Align = Align.Center, MaxWidth = MathF.Min(720, _gui.Width - 48) }, body);
            _gui.Box(new Style { Direction = Direction.Row, Gap = 8 }, actions);
        });

    // ------------------------------------------------------------------ партия

    private RenderFrame PlayFrame(ClientGame game, ClientInput input)
    {
        if (_hooked != game)
        {
            _hooked = game;
            game.Clipboard = _gui.Clipboard;
            game.AfterMods = Overlay;
            _gameWords = new Words(game.Texts, () => Lang);
            _gameForms = new Forms(game.Gui);
        }
        game.Lang = Lang;
        var hits = input.Hits;
        // Меню и чат по клавише — пока клавиши не держит поле ввода или модальное окно; эти нажатия модам не идут.
        if (!game.CapturesKeys && !Paused && !Chat.IsOpen && Settings.Binding is null)
        {
            if (hits.Contains(Prefs.KeyOf(ShellKey.Menu)))
            {
                Pause(true);
                hits = [.. hits.Where(key => key != Prefs.KeyOf(ShellKey.Menu))];
            }
            else if (hits.Contains(Prefs.KeyOf(ShellKey.Chat)) || input.Text.Contains('/'))
            {
                Chat.Open(hits.Contains(Prefs.KeyOf(ShellKey.Chat)) ? "" : "/");
                hits = [.. hits.Where(key => key != Prefs.KeyOf(ShellKey.Chat))];
                input = input with { Text = input.Text.Replace("/", "") };
            }
        }
        input = input with { Hits = hits, Shell = ShellFlags };
        _hits = hits;
        var frame = game.Step(input);
        if (_gameForms is { } forms)
            Sounds(game.Gui, forms, menu: false);
        return frame;
    }

    private void Pause(bool on)
    {
        (Paused, SettingsOpen) = (on, false);
        Audio.Dim = on;
    }

    /// <summary>Окна оболочки поверх партии — в интерфейсе партии, после модов: просьбы модов, чат, меню паузы.</summary>
    private void Overlay(ClientScene scene)
    {
        if (Session?.Game is not { } game || _gameForms is not { } forms || _gameWords is not { } words)
            return;
        forms.Begin();
        if (!_cameraRestored)
        {
            _cameraRestored = true;
            if (Session.Launch is SlotLaunch slot && Slots.LoadCamera(slot.Slot.Id) is { } camera)
                scene.Camera = new CameraState(camera.X, camera.Y, camera.Zoom);
        }
        foreach (var (action, arg) in game.ShellRequests)
        {
            switch (action)
            {
                case ClientAbi.ShellActionMenu:
                    Pause(true);
                    break;
                case ClientAbi.ShellActionChat:
                    if (!Paused)
                        Chat.Open(arg);
                    break;
                case ClientAbi.ShellActionSettings:
                    Pause(true);
                    SettingsOpen = true;
                    break;
            }
        }
        game.ShellRequests.Clear();
        Chat.Take(game.Chat, scene.Gui.Now);
        if (!Paused)
        {
            var names = game.Players.Select(player => player.Name).ToList();
            Chat.Draw(scene.Gui, words, game.Commands, names, game.Admin, game.Replica.Player, game.TextOf, game.Say, 12, 12);
        }
        else
            PauseMenu(scene.Gui, forms, words);
    }

    /// <summary>Меню поверх игры: продолжить, настройки, выйти (в слот — с сохранением).</summary>
    private void PauseMenu(Gui gui, Forms forms, Words words)
    {
        if (SettingsOpen)
        {
            if (Settings.Draw(forms, _hits, modal: true))
                SettingsOpen = false;
            return;
        }
        var launch = Session?.Launch;
        var leave = false;
        var back = forms.Window("pause", words["game.menu"], MathF.Min(300, gui.Width - 32), () =>
            gui.Box(new Style { Direction = Direction.Column, Align = Align.Stretch, Gap = 6 }, () =>
            {
                if (forms.Button("pause:resume", words["common.resume"], primary: true))
                    Pause(false);
                if (forms.Button("pause:settings", words["common.settings"]))
                    SettingsOpen = true;
                // «Сдаться» и «Начать заново» — смысл игры: их кнопки — у мода (панель игроков, К3б).
                if (forms.Button("pause:exit", launch is SlotLaunch ? words["game.saveAndExit"] : words["game.exitToMenu"]))
                    leave = true;
            }), modal: true);
        if (back)
            Pause(false);
        if (leave)
            _leaveRequested = true;
    }

    private bool _leaveRequested;

    public void Dispose()
    {
        Leave(save: true);
        Background?.Dispose();
    }
}
