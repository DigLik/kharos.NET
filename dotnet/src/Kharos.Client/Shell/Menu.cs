using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Kharos.Client.Ui;
using Kharos.Engine.Terrain;
using Kharos.Protocol;

namespace Kharos.Client.Shell;

/// <summary>Экран главного меню.</summary>
public enum MenuScreen : byte { Main, New, Saves, Network, Settings, About }

/// <summary>
/// Главное меню (menu.ts): продолжить последнюю игру, новая игра с предпросмотром местности, сохранения, сетевая игра,
/// настройки, об игре. Окна, поля и кнопки — те же, что у TS. Фон — витрина (пакет В) или ровный цвет; меню только
/// описывает окна. Игру начинает <see cref="Play"/>.
/// </summary>
public sealed class Menu
{
    /// <summary>Стороны карты новой игры, тайлов.</summary>
    public static readonly int[] MapSizes = [512, 1024, 2048];

    /// <summary>Сколько длятся сутки новой игры, секунд; 0 — время стоит.</summary>
    public static readonly int[] DayLengths = [600, 1200, 2400, 0];

    /// <summary>Уже скольких пикселей экран узкий: окна по центру, превью над полями.</summary>
    public const float Narrow = 760;

    public const string Repository = "https://github.com/vicimpa/kharos";
    private static readonly (string Name, string Url)[] Authors = [("vicimpa", "https://github.com/vicimpa")];

    /// <summary>Ползунок местности для человека: доля 0..1 переводится в параметр генератора и обратно.</summary>
    private sealed record Knob(string Key, double From, double To);

    private static readonly Knob[] Knobs =
    [
        // Чем ниже уровень скал, тем их больше: ползунок «больше» вправо.
        new("rockLevel", 0.75, 0.45),
        new("swampLevel", 0.2, 0.5),
        new("zoneScale", 24, 120),
        new("zoneWarp", 0, 80),
        new("biomeScale", 80, 600),
        new("peakChance", 0, 1),
    ];

    /// <summary>Готовые местности: отличия от генератора по умолчанию.</summary>
    private static readonly (string Label, (string Key, double Value)[] Changes)[] Presets =
    [
        ("menu.presets.normal", []),
        ("menu.presets.rocky", [("rockLevel", 0.5), ("swampLevel", 0.3), ("peakChance", 0.5)]),
        ("menu.presets.desert", [("rockLevel", 0.68), ("swampLevel", 0.25), ("zoneScale", 80)]),
        ("menu.presets.swamps", [("rockLevel", 0.62), ("swampLevel", 0.46), ("zoneWarp", 50)]),
        ("menu.presets.islands", [("rockLevel", 0.6), ("swampLevel", 0.5), ("zoneScale", 40), ("zoneWarp", 10)]),
    ];

    /// <summary>Поле тонкой настройки генератора.</summary>
    private sealed record GenField(string Key, string Label, double Min, double Max, double Step);

    private static GenField[] BiomeFields(string prefix) =>
    [
        new($"{prefix}SwampShift", "generator.fields.swampShift", -0.2, 0.2, 0.005),
        new($"{prefix}RockShift", "generator.fields.rockShift", -0.2, 0.2, 0.005),
        new($"{prefix}PeakFactor", "generator.fields.peakFactor", 0, 4, 0.1),
    ];

    private static readonly (string Title, GenField[] Fields)[] GeneratorGroups =
    [
        ("generator.groups.zones",
        [
            new("biomeScale", "generator.fields.biomeScale", 40, 600, 10),
            new("zoneScale", "generator.fields.zoneScale", 16, 160, 1),
            new("zoneWarp", "generator.fields.zoneWarp", 0, 80, 1),
            new("swampLevel", "generator.fields.swampLevel", 0, 1, 0.005),
            new("rockLevel", "generator.fields.rockLevel", 0, 1, 0.005),
        ]),
        ("generator.groups.peaks",
        [
            new("peakChance", "generator.fields.chance", 0, 1, 0.01),
            new("peakMinRadius", "generator.fields.peakMinRadius", 0.5, 2, 0.05),
            new("peakMaxRadius", "generator.fields.peakMaxRadius", 0.5, 2, 0.05),
            new("doubleChance", "generator.fields.doubleChance", 0, 1, 0.01),
            new("tripleChance", "generator.fields.tripleChance", 0, 1, 0.01),
        ]),
        ("generator.groups.deposits",
        [
            new("depositChance", "generator.fields.chance", 0, 1, 0.01),
            new("depositRichness", "generator.fields.depositRichness", 0.1, 5, 0.1),
        ]),
        ("generator.groups.salt", BiomeFields("salt")),
        ("generator.groups.red", BiomeFields("red")),
        ("generator.groups.marsh", BiomeFields("marsh")),
    ];

    /// <summary>Новая игра в окне: название, сторона карты, местность, сутки и погода.</summary>
    public sealed class NewGame
    {
        public string Name = "";
        public int Size = 512;
        public GeneratorConfig Generator = new();
        public int DayLength = 1200;
        public double StartHour = 9;
        public bool WeatherChanges = true;
        public bool Fine;
    }

    private readonly Forms _forms;
    private readonly Words _words;
    private readonly SaveSlots _slots;
    private readonly ShellPrefs _prefs;
    private readonly SettingsWindow _settings;
    private readonly Random _random;
    private List<SaveSlot> _saves = [];
    private string _error = "";
    private string _notice = "";
    private string? _confirming;
    private (string Id, string Name)? _renaming;
    private string? _importPath;
    private (string Url, string Name) _server;

    public Menu(Forms forms, Words words, SaveSlots slots, ShellPrefs prefs, SettingsWindow settings, Action<Launch> play, Random? random = null)
    {
        _forms = forms;
        _words = words;
        _slots = slots;
        _prefs = prefs;
        _settings = settings;
        Play = play;
        _random = random ?? new Random();
        Refresh();
        Game = Fresh();
        _server = prefs.Server();
    }

    /// <summary>Начать игру с этим запуском.</summary>
    public Action<Launch> Play { get; }

    /// <summary>Открыть адрес в браузере (оболочка решает как); null — ссылки не открываются.</summary>
    public Action<string>? OpenUrl { get; set; }

    /// <summary>Записать файл переноса: имя и байты → путь записанного; null — не вышло.</summary>
    public Func<string, byte[], string?>? ExportFile { get; set; }

    /// <summary>Прочитать файл сохранения по пути; null — не вышло.</summary>
    public Func<string, byte[]?> ReadFile { get; set; } = path =>
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    };

    public MenuScreen Screen { get; private set; } = MenuScreen.Main;

    /// <summary>Окно новой игры.</summary>
    public NewGame Game { get; private set; }

    /// <summary>Схема карты окна новой игры: оболочка кладёт её в растр мини-карты рендера.</summary>
    public TerrainPreview Preview { get; } = new();

    /// <summary>Слоты, как их показывает окно сохранений.</summary>
    public IReadOnlyList<SaveSlot> Saves => _saves;

    public void Open(MenuScreen screen)
    {
        Screen = screen;
        if (screen == MenuScreen.New)
            Game = Fresh();
        if (screen == MenuScreen.Saves)
        {
            Refresh();
            (_error, _notice, _confirming, _renaming, _importPath) = ("", "", null, null, null);
        }
        if (screen == MenuScreen.Network)
            _server = _prefs.Server();
    }

    private void Home() => Screen = MenuScreen.Main;

    private void Refresh() => _saves = _slots.List();

    private NewGame Fresh() => new()
    {
        Name = _words.T("menu.gameName", ("number", _saves.Count + 1)),
        Generator = new GeneratorConfig { Seed = RandomSeed() },
    };

    private int RandomSeed() => _random.Next(int.MaxValue);

    /// <summary>Кадр меню: описывает окна в gui. hits — клавиши кадра (окно настроек перевешивает по ним клавиши).</summary>
    public void Draw(IReadOnlyList<string> hits)
    {
        var gui = _forms.Gui;
        Logo(gui);
        switch (Screen)
        {
            case MenuScreen.Main: MainWindow(); break;
            case MenuScreen.New: NewGameWindow(); break;
            case MenuScreen.Saves: SavesWindow(); break;
            case MenuScreen.Network: NetworkWindow(); break;
            case MenuScreen.About: AboutWindow(); break;
            case MenuScreen.Settings:
                if (_settings.Draw(_forms, hits, Place(gui)))
                    Home();
                break;
        }
        if (Screen == MenuScreen.New)
            Preview.Step(gui.Now);
    }

    /// <summary>Место окна: у широкого экрана — слева, у узкого — по центру; над окном — логотип.</summary>
    private static Anchor Place(Gui gui)
    {
        var top = Math.Clamp(gui.Height * 0.12f, 16, 120) + 76;
        return gui.Width < Narrow ? new Anchor(CenterX: 0, Top: top) : new Anchor(Left: 56, Top: top);
    }

    private static float SmallWidth(Gui gui) => gui.Width < Narrow ? MathF.Min(280, gui.Width - 32) : 300;

    private static void Logo(Gui gui)
    {
        var narrow = gui.Width < Narrow;
        var top = Math.Clamp(gui.Height * 0.12f, 16, 120);
        gui.Box(new Style
        {
            Anchor = narrow ? new Anchor(CenterX: 0, Top: top) : new Anchor(Left: 56, Top: top),
            Width = narrow ? null : 300, Direction = Direction.Row, Align = Align.Center, Justify = Align.Center, Gap = 8, Passive = true,
        }, () =>
        {
            foreach (var letter in "KHAROS")
                gui.Text(letter.ToString(), new TextStyle { Font = FontName.Logo, Color = Colors.Bright, Shadow = Colors.Ink, Lift = 4 });
        });
    }

    private string Playtime(ulong tick)
    {
        var (hours, minutes) = SaveSlots.Playtime(tick);
        return hours == 0 ? _words.T("menu.minutes", ("minutes", minutes)) : _words.T("menu.hours", ("hours", hours), ("minutes", minutes));
    }

    private void MainWindow()
    {
        var gui = _forms.Gui;
        var last = _prefs.Last(_slots);
        var lastTitle = last switch
        {
            SlotLaunch slot => $"{slot.Slot.Name} — {Playtime(slot.Slot.Tick)}",
            ServerLaunch server => _words.T("menu.lastServer", ("url", server.Url)),
            _ => null,
        };
        _forms.Window("menu:main", _words["menu.title"], SmallWidth(gui), () =>
        {
            gui.Box(new Style { Direction = Direction.Column, Align = Align.Stretch, Gap = 6 }, () =>
            {
                // Продолжать нечего — кнопки нет, а не мёртвая серая. Есть что — это и есть главное действие.
                if (last is not null && _forms.Button("menu:resume", _words["common.resume"], primary: true, tip: lastTitle))
                    Play(last);
                if (_forms.Button("menu:new", _words["menu.newGame"]))
                    Open(MenuScreen.New);
                if (_forms.Button("menu:saves", _words["menu.saves"]))
                    Open(MenuScreen.Saves);
                if (_forms.Button("menu:network", _words["menu.network"]))
                    Open(MenuScreen.Network);
                if (_forms.Button("menu:settings", _words["common.settings"]))
                    Open(MenuScreen.Settings);
                if (_forms.Button("menu:about", _words["menu.about"]))
                    Open(MenuScreen.About);
            });
            if (_forms.Link("menu:github", "GitHub", center: true))
                OpenUrl?.Invoke(Repository);
        }, Place(gui));
    }

    private void NewGameWindow()
    {
        var gui = _forms.Gui;
        var width = MathF.Min(900, gui.Width - 32);
        var narrow = gui.Width < Narrow;
        var inner = width - 36;
        const float aside = 328;
        var form = narrow ? inner : inner - aside - 16;
        var game = Game;
        var preset = Array.FindIndex(Presets, option => SameChanges(Changes(game.Generator), option.Changes));
        var start = false;
        var toEditor = false;
        var back = _forms.Window("menu:new", _words["menu.newGame"], width, () =>
        {
            void Fields() => gui.Box(new Style { Direction = Direction.Column, Align = Align.Stretch, Gap = 10, Width = form }, () =>
            {
                _forms.Above(_words["menu.name"], () =>
                {
                    var name = _forms.TextField("menu:new:name", game.Name, maxLength: 40);
                    game.Name = name.Value;
                    if (name.Keys.Contains("enter"))
                        start = true;
                });
                _forms.Above(_words["menu.seed"], () => gui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 6 }, () =>
                {
                    if (_forms.NumberField("menu:new:seed", game.Generator.Seed, min: int.MinValue, max: int.MaxValue) is { } seed)
                        game.Generator = game.Generator with { Seed = (int)seed };
                    if (_forms.Button("menu:new:random", _words["menu.random"], tip: _words["menu.randomTip"]))
                        game.Generator = game.Generator with { Seed = RandomSeed() };
                }));
                _forms.Above(_words["menu.mapSize"], () =>
                {
                    var size = _forms.Chips("menu:new:size", [.. MapSizes.Select(n => n.ToString(CultureInfo.InvariantCulture))], Array.IndexOf(MapSizes, game.Size), form);
                    if (size >= 0)
                        game.Size = MapSizes[size];
                });
                _forms.Above(_words["menu.terrain"], () =>
                {
                    var picked = _forms.Chips("menu:new:preset", [.. Presets.Select(option => _words[option.Label])], preset, form);
                    if (picked >= 0)
                        game.Generator = Apply(new GeneratorConfig { Seed = game.Generator.Seed }, Presets[picked].Changes);
                });
                foreach (var knob in Knobs)
                {
                    var share = KnobValue(knob, game.Generator);
                    _forms.Row(_words[$"menu.knobs.{knob.Key}"], 150, () =>
                    {
                        var moved = _forms.Slider($"menu:new:knob:{knob.Key}", share, 0, 1, 0.01, form - 158 - 56);
                        if (moved != share)
                            game.Generator = Set(game.Generator, knob.Key, Math.Round((knob.From + moved * (knob.To - knob.From)) * 1000) / 1000);
                        gui.Box(new Style { Width = 48, Direction = Direction.Row, Justify = Align.End }, () => gui.Text(Math.Round(share * 100).ToString(CultureInfo.InvariantCulture)));
                    }, _words[$"menu.knobTips.{knob.Key}"], $"menu:new:knobLabel:{knob.Key}");
                }
                game.Fine = _forms.Details("menu:new:fine", _words["menu.fineTuning"], game.Fine, () =>
                {
                    if (GeneratorFields(game.Generator, form) is { } tuned)
                        game.Generator = tuned;
                });
                _forms.Above(_words["menu.dayLasts"], () =>
                {
                    var day = _forms.Chips("menu:new:day", [.. DayLengths.Select(seconds => seconds > 0 ? _words.T("menu.minutes", ("minutes", seconds / 60)) : _words["menu.dayStands"])], Array.IndexOf(DayLengths, game.DayLength), form);
                    if (day >= 0)
                        game.DayLength = DayLengths[day];
                });
                _forms.Row(_words["menu.startHour"], 150, () =>
                {
                    game.StartHour = _forms.Slider("menu:new:hour", game.StartHour, 0, 23.5, 0.5, form - 158 - 56);
                    gui.Box(new Style { Width = 48, Direction = Direction.Row, Justify = Align.End }, () => gui.Text(Clock(game.StartHour)));
                });
                game.WeatherChanges = _forms.Checkbox("menu:new:weather", game.WeatherChanges, _words["menu.weatherChanges"]);
            });
            void Side() => PreviewBox(game.Generator, game.Size, game.Size, narrow ? inner : 320);
            gui.Box(new Style { Direction = narrow ? Direction.Column : Direction.Row, Gap = 16, Align = narrow ? Align.Center : Align.Start }, () =>
            {
                if (narrow)
                {
                    Side();
                    Fields();
                }
                else
                {
                    Fields();
                    Side();
                }
            });
        }, Place(gui), _words["common.back"], () =>
        {
            if (_forms.Button("menu:new:editor", _words["menu.toEditor"]))
                toEditor = true;
            if (_forms.Button("menu:new:start", _words["menu.start"], primary: true))
                start = true;
        });
        if (back)
            Home();
        else if (start)
            StartNew();
        else if (toEditor)
            StartNew(editor: true);
    }

    /// <summary>Заводит слот новой игры и начинает её; editor — открывает его мир в редакторе (Shell/Editor*).</summary>
    private void StartNew(bool editor = false)
    {
        var game = Game;
        var name = string.IsNullOrWhiteSpace(game.Name) ? _words.T("menu.gameName", ("number", _saves.Count + 1)) : game.Name.Trim();
        var slot = _slots.Create(name, game.Size, game.Generator.Seed, settings: SettingsOf(game));
        Refresh();
        Play(editor ? new EditorLaunch(slot) : new SlotLaunch(slot));
    }

    /// <summary>Настройки нового мира поверх записи партии: сторона, местность (все поля генератора с зерном), погода.</summary>
    public static string SettingsOf(NewGame game)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("size", game.Size);
            json.WriteStartObject("land");
            json.WriteNumber("seed", game.Generator.Seed);
            foreach (var (key, value) in Changes(game.Generator))
                json.WriteNumber(key, value);
            if (game.Generator.Blank)
                json.WriteBoolean("blank", true);
            json.WriteEndObject();
            json.WriteStartObject("weather");
            json.WriteNumber("dayLength", game.DayLength);
            json.WriteNumber("startHour", game.StartHour);
            json.WriteBoolean("changes", game.WeatherChanges);
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string Clock(double hour) =>
        $"{(int)Math.Floor(hour):00}:{(int)Math.Floor(hour % 1 * 60):00}";

    /// <summary>Схема карты с подписью размера и долей скал и болот.</summary>
    private void PreviewBox(GeneratorConfig config, int width, int height, float side)
    {
        var gui = _forms.Gui;
        Preview.Show(config, width, height, gui.Now);
        gui.Box(new Style { Direction = Direction.Column, Align = Align.Center, Gap = 6 }, () =>
        {
            gui.Box(new Style { Id = "menu:preview", Skin = Skin.Well, Padding = 4, Alpha = Preview.Ready ? null : 0.6f }, () => gui.Minimap(side, side));
            var text = _words.T("menu.previewSize", ("width", width), ("height", height));
            if (Preview.Shares is { } shares)
                text += " · " + _words.T("menu.previewShares", ("rock", (int)Math.Round(shares.Rock * 100)), ("swamp", (int)Math.Round(shares.Swamp * 100)));
            gui.Text(text, new TextStyle { Color = Colors.Muted, Wrap = side });
        });
    }

    /// <summary>Ползунки тонкой настройки по группам; новые значения или null.</summary>
    private GeneratorConfig? GeneratorFields(GeneratorConfig values, float width)
    {
        var gui = _forms.Gui;
        GeneratorConfig? next = null;
        foreach (var (title, fields) in GeneratorGroups)
        {
            gui.Box(new Style { Skin = Skin.Well, Padding = (8, 10), Direction = Direction.Column, Gap = 6, Width = width }, () =>
            {
                gui.Text(_words[title], new TextStyle { Color = Colors.Dim });
                foreach (var field in fields)
                {
                    var value = Get(next ?? values, field.Key);
                    _forms.Row(_words[field.Label], 96, () =>
                    {
                        var moved = _forms.Slider($"gen:{field.Key}", value, field.Min, field.Max, field.Step, width - 20 - 96 - 48 - 16);
                        if (moved != value)
                            next = Set(next ?? values, field.Key, moved);
                        gui.Box(new Style { Width = 48, Direction = Direction.Row, Justify = Align.End }, () => gui.Text(value.ToString("0.###", CultureInfo.InvariantCulture)));
                    }, id: $"gen:{field.Key}:label");
                }
            });
        }
        return next;
    }

    private void SavesWindow()
    {
        var gui = _forms.Gui;
        var width = MathF.Min(560, gui.Width - 32);
        var inner = width - 36;
        var back = _forms.Window("menu:saves", _words["menu.saves"], width, () =>
        {
            if (_saves.Count == 0)
                _forms.Note(_words["menu.noSaves"], Colors.Muted);
            foreach (var slot in _saves.ToList())
                SaveRow(slot, inner);
            if (_importPath is not null)
            {
                _forms.Above(_words["settings.filePath"], () =>
                {
                    var field = _forms.TextField("menu:saves:path", _importPath, focus: _importPath.Length == 0, placeholder: "C:\\…\\game.kharos");
                    _importPath = field.Value;
                    if (field.Keys.Contains("enter"))
                        Import(field.Value);
                    else if (field.Keys.Contains("escape"))
                        _importPath = null;
                });
            }
            if (_error.Length > 0)
                _forms.Note(_error, Colors.Bad);
            if (_notice.Length > 0)
                _forms.Note(_notice, Colors.Muted);
        }, Place(gui), _words["common.back"], () =>
        {
            if (_forms.Button("menu:saves:load", _words["menu.loadFile"], primary: _importPath is not null))
            {
                if (_importPath is { Length: > 0 } path)
                    Import(path);
                else
                    _importPath = "";
            }
            if (_importPath is { Length: > 0 } editPath && _forms.Button("menu:saves:editor", _words["menu.fileToEditor"], tip: _words["menu.fileToEditorTip"]))
                Play(new EditorLaunch(editPath.Trim().Trim('"')));
            if (_forms.Button("menu:saves:new", _words["menu.newWorld"], primary: _saves.Count == 0))
                Open(MenuScreen.New);
        });
        if (back)
            Home();
    }

    /// <summary>Файл сохранения по пути — в новый слот; не сохранение — ошибка в окне.</summary>
    public void Import(string path)
    {
        path = path.Trim().Trim('"');
        if (ReadFile(path) is not { } bytes)
        {
            _error = _words.T("errors.notSave");
            return;
        }
        try
        {
            _slots.ImportedName = _words["menu.importedName"];
            _slots.Import(bytes);
            (_error, _importPath) = ("", null);
        }
        catch (SaveVersionException error)
        {
            _error = _words[error.Found < error.Expected ? "errors.saveTooOld" : "errors.saveTooNew"];
        }
        catch (FormatException)
        {
            _error = _words["errors.notSave"];
        }
        Refresh();
    }

    private void SaveRow(SaveSlot slot, float width)
    {
        var gui = _forms.Gui;
        var id = $"save:{slot.Id}";
        gui.Box(new Style { Skin = Skin.Well, Padding = 12, Direction = Direction.Column, Gap = 8 }, () =>
        {
            gui.Box(new Style { Direction = Direction.Column, Gap = 2 }, () =>
            {
                if (_renaming is { } renaming && renaming.Id == slot.Id)
                {
                    var field = _forms.TextField($"{id}:name", renaming.Name, width - 24, maxLength: 40, focus: true);
                    _renaming = (slot.Id, field.Value);
                    if (field.Keys.Contains("enter") && field.Value.Trim().Length > 0)
                    {
                        _slots.Rename(slot.Id, field.Value.Trim());
                        _renaming = null;
                        Refresh();
                    }
                    else if (field.Keys.Contains("escape") || !field.Focused && gui.Focused is not null)
                        _renaming = null;
                }
                else
                    gui.Text(slot.Name, new TextStyle { Color = Colors.Bright, Wrap = float.PositiveInfinity });
                var size = slot.Height is { } height && height != slot.Size ? $"{slot.Size}×{height}" : slot.Size.ToString(CultureInfo.InvariantCulture);
                gui.Text($"{SaveSlots.Date(slot.Updated)} · {Playtime(slot.Tick)} · {_words.T("menu.mapOf", ("size", size))} · {_words.T("menu.seedOf", ("seed", slot.Seed))}",
                    new TextStyle { Font = FontName.Small, Color = Colors.Muted, Wrap = float.PositiveInfinity });
            });
            gui.Box(new Style { Direction = Direction.Row, Gap = 6, Wrap = true }, () =>
            {
                if (_forms.Button($"{id}:play", _words["common.play"], primary: true))
                    Play(new SlotLaunch(slot));
                if (_forms.Button($"{id}:editor", _words["menu.editor"], tip: _words["menu.editorTip"]))
                    Play(new EditorLaunch(slot));
                if (_forms.Button($"{id}:rename", _words["menu.rename"]))
                    _renaming = (slot.Id, slot.Name);
                if (_forms.Button($"{id}:file", _words["menu.file"], tip: _words["menu.fileTip"]))
                {
                    if (_slots.Export(slot.Id) is not { } file)
                        _error = _words["menu.notSavedYet"];
                    else if (ExportFile?.Invoke($"{slot.Name}.kharos", file) is { } path)
                        (_error, _notice) = ("", _words.T("settings.fileSaved", ("path", path)));
                }
                if (_confirming == slot.Id)
                {
                    if (_forms.Button($"{id}:sure", _words["menu.sure"], danger: true))
                    {
                        _slots.Delete(slot.Id);
                        _confirming = null;
                        Refresh();
                    }
                }
                else if (_forms.Button($"{id}:remove", _words["menu.remove"]))
                    _confirming = slot.Id;
            });
        });
    }

    /// <summary>Адрес сервера годится: ws:// или wss:// и что-то после.</summary>
    public static bool ValidServer(string url) =>
        (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) && url.Length > url.IndexOf("//", StringComparison.Ordinal) + 2;

    private void Join(string url, string name)
    {
        _prefs.StoreServer(url, name);
        Play(new ServerLaunch(url, name.Trim()));
    }

    private void NetworkWindow()
    {
        var gui = _forms.Gui;
        var valid = ValidServer(_server.Url);
        var connect = false;
        var back = _forms.Window("menu:network", _words["menu.network"], SmallWidth(gui), () =>
        {
            var recent = _prefs.Servers();
            if (recent.Count > 0)
            {
                gui.Text(_words["menu.recentServers"], new TextStyle { Color = Colors.Dim });
                foreach (var item in recent)
                {
                    gui.Box(new Style { Skin = Skin.Well, Padding = 12, Direction = Direction.Column, Gap = 6 }, () =>
                    {
                        gui.Text(item.Url, new TextStyle { Color = Colors.Bright, Wrap = float.PositiveInfinity });
                        gui.Text(SaveSlots.Date(item.Played) + (item.Name.Length > 0 ? $" · {item.Name}" : ""), new TextStyle { Font = FontName.Small, Color = Colors.Muted, Wrap = float.PositiveInfinity });
                        gui.Box(new Style { Direction = Direction.Row, Gap = 6 }, () =>
                        {
                            if (_forms.Button($"recent:{item.Url}:play", _words["common.play"], primary: true))
                                Join(item.Url, _server.Name.Length > 0 ? _server.Name : item.Name);
                            if (_forms.Button($"recent:{item.Url}:forget", "×", tip: _words["menu.forget"]))
                                _prefs.Forget(item.Url);
                        });
                    });
                }
            }
            gui.Text(_words["menu.nick"], new TextStyle { Color = Colors.Dim });
            var nick = _forms.TextField("menu:network:nick", _server.Name, maxLength: 24, placeholder: _words["menu.nickHint"]);
            _server = (_server.Url, nick.Value);
            gui.Text(_words["menu.serverAddress"], new TextStyle { Color = Colors.Dim });
            var urlKeys = (IReadOnlyList<string>)[];
            gui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 6 }, () =>
            {
                var url = _forms.TextField("menu:network:url", _server.Url, placeholder: "ws://host:port");
                urlKeys = url.Keys;
                _server = (url.Value.Trim(), _server.Name);
                // «Этот компьютер» подставляет адрес — и стоит рядом с ним, а не среди действий окна.
                if (_forms.Button("menu:network:local", _words["menu.thisComputer"]))
                    _server = (ShellPrefs.DefaultServer, _server.Name);
            });
            _forms.Note(_words["menu.sharedWorld"], Colors.Muted);
            if ((nick.Keys.Contains("enter") || urlKeys.Contains("enter")) && valid)
                connect = true;
        }, Place(gui), _words["common.back"], () =>
        {
            if (_forms.Button("menu:network:connect", _words["common.connect"], primary: true, disabled: !valid))
                connect = true;
        });
        if (back)
            Home();
        else if (connect && valid)
            Join(_server.Url, _server.Name);
    }

    private void AboutWindow()
    {
        var gui = _forms.Gui;
        var back = _forms.Window("menu:about", _words["menu.about"], SmallWidth(gui), () =>
        {
            _forms.Note(_words["menu.aboutText"]);
            gui.Text(_words["menu.authors"].ToUpperInvariant(), new TextStyle { Color = Colors.Muted });
            foreach (var (name, url) in Authors)
                if (_forms.Link($"about:{name}", name))
                    OpenUrl?.Invoke(url);
            gui.Text(_words["menu.sourceCode"].ToUpperInvariant(), new TextStyle { Color = Colors.Muted });
            if (_forms.Link("about:repo", "github.com/vicimpa/kharos"))
                OpenUrl?.Invoke(Repository);
            gui.Text(_words.T("menu.protocolVersion", ("version", BuildInfo.Version), ("protocol", Welcome.ProtocolVersion)), new TextStyle { Color = Colors.Muted });
        }, Place(gui), _words["common.back"]);
        if (back)
            Home();
    }

    // ------------------------------------------------------------------ параметры генератора по имени

    private static double KnobValue(Knob knob, GeneratorConfig config) => Math.Clamp((Get(config, knob.Key) - knob.From) / (knob.To - knob.From), 0, 1);

    private static readonly GeneratorConfig Defaults = new();

    /// <summary>Числовые поля генератора по именам TS.</summary>
    private static readonly (string Key, Func<GeneratorConfig, double> Get, Func<GeneratorConfig, double, GeneratorConfig> Set)[] Numbers =
    [
        ("zoneScale", c => c.ZoneScale, (c, v) => c with { ZoneScale = v }),
        ("zoneWarp", c => c.ZoneWarp, (c, v) => c with { ZoneWarp = v }),
        ("swampLevel", c => c.SwampLevel, (c, v) => c with { SwampLevel = v }),
        ("rockLevel", c => c.RockLevel, (c, v) => c with { RockLevel = v }),
        ("biomeScale", c => c.BiomeScale, (c, v) => c with { BiomeScale = v }),
        ("peakChance", c => c.PeakChance, (c, v) => c with { PeakChance = v }),
        ("peakMinRadius", c => c.PeakMinRadius, (c, v) => c with { PeakMinRadius = v }),
        ("peakMaxRadius", c => c.PeakMaxRadius, (c, v) => c with { PeakMaxRadius = v }),
        ("doubleChance", c => c.DoubleChance, (c, v) => c with { DoubleChance = v }),
        ("tripleChance", c => c.TripleChance, (c, v) => c with { TripleChance = v }),
        ("saltSwampShift", c => c.SaltSwampShift, (c, v) => c with { SaltSwampShift = v }),
        ("saltRockShift", c => c.SaltRockShift, (c, v) => c with { SaltRockShift = v }),
        ("saltPeakFactor", c => c.SaltPeakFactor, (c, v) => c with { SaltPeakFactor = v }),
        ("redSwampShift", c => c.RedSwampShift, (c, v) => c with { RedSwampShift = v }),
        ("redRockShift", c => c.RedRockShift, (c, v) => c with { RedRockShift = v }),
        ("redPeakFactor", c => c.RedPeakFactor, (c, v) => c with { RedPeakFactor = v }),
        ("marshSwampShift", c => c.MarshSwampShift, (c, v) => c with { MarshSwampShift = v }),
        ("marshRockShift", c => c.MarshRockShift, (c, v) => c with { MarshRockShift = v }),
        ("marshPeakFactor", c => c.MarshPeakFactor, (c, v) => c with { MarshPeakFactor = v }),
        ("tierStep", c => c.TierStep, (c, v) => c with { TierStep = v }),
        ("cliffScale", c => c.CliffScale, (c, v) => c with { CliffScale = v }),
        ("cliffShare", c => c.CliffShare, (c, v) => c with { CliffShare = v }),
        ("depositChance", c => c.DepositChance, (c, v) => c with { DepositChance = v }),
        ("depositRichness", c => c.DepositRichness, (c, v) => c with { DepositRichness = v }),
    ];

    public static double Get(GeneratorConfig config, string key) => Array.Find(Numbers, n => n.Key == key).Get?.Invoke(config) ?? double.NaN;

    public static GeneratorConfig Set(GeneratorConfig config, string key, double value) =>
        Array.Find(Numbers, n => n.Key == key).Set?.Invoke(config, value) ?? config;

    private static GeneratorConfig Apply(GeneratorConfig config, IEnumerable<(string Key, double Value)> changes)
    {
        foreach (var (key, value) in changes)
            config = Set(config, key, value);
        return config;
    }

    /// <summary>Отличия местности от генератора по умолчанию, без зерна: их и хранит слот.</summary>
    public static List<(string Key, double Value)> Changes(GeneratorConfig config) =>
        [.. Numbers.Where(n => n.Get(config) != n.Get(Defaults)).Select(n => (n.Key, n.Get(config)))];

    private static bool SameChanges(List<(string Key, double Value)> changes, (string Key, double Value)[] preset) =>
        changes.Count == preset.Length && preset.All(change => changes.Contains(change));
}
