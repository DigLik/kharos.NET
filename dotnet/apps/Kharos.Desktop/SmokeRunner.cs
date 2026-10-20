using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Kharos.Client.Game;
using Kharos.Client.Shell;
using Kharos.Client.Ui;
using Kharos.Engine.World;
using Kharos.Gpu;

namespace Kharos.Desktop;

/// <summary>
/// Исполнитель сценария смоука (Н7.3, П1) в цикле оболочки: перед кадром подставляет ввод шага (<see cref="Input"/>), после
/// кадра двигает шаг (<see cref="After"/>). Шаг — итератор: «yield» — ждать следующего кадра. У каждого шага срок
/// (ожидания — <see cref="DefaultWait"/> или timeout сценария, действия — <see cref="DefaultAct"/>, входы в партию —
/// <see cref="DefaultEnter"/>); вышел срок или шаг упал — отчёт и код 2, все шаги прошли — отчёт и код 0.
/// <para>
/// Глаголы: <c>click &lt;ui-id&gt;</c>; <c>type &lt;поле&gt; &lt;текст&gt;</c> (щелчок, Ctrl+A, текст); <c>key &lt;имя&gt;</c>
/// (ctrl+s — с модификаторами); <c>wait &lt;условие&gt; [срок]</c>; <c>order …</c> (<see cref="SmokeOrder"/>);
/// <c>chat &lt;текст&gt;</c>; <c>save &lt;слот&gt;</c> (одиночная партия: хост останавливается, мир — в слот, партия идёт дальше
/// из слота; редактор — «Сохранить» в слот); <c>leave</c> (в меню, слот сохраняется); <c>load &lt;слот&gt;</c>;
/// <c>editor &lt;слот&gt; [размер]</c> (нет слота — новый); <c>connect [ws://…]</c> (по умолчанию KHAROS_SERVER);
/// <c>shot &lt;png&gt; [цветов]</c> (без GPU — пропуск); <c>mark &lt;имя&gt;</c>; <c>assert &lt;выражение&gt;</c> (<see cref="SmokeExpr"/>).
/// </para>
/// <para>
/// Условия wait: <c>menu</c> (главное меню и первый показанный кадр витрины, не дольше <see cref="ShowcaseLimit"/>),
/// <c>game</c>, <c>tick&gt;=N</c> (<c>tick&gt;=+N</c> — от начала шага), <c>ui &lt;id&gt;</c>, <c>chat &lt;подстрока&gt;</c>,
/// <c>roster &lt;ник&gt; online|offline</c>, <c>allied &lt;ник&gt;</c>, <c>count &lt;вид&gt;[@чей] &lt;оп&gt; &lt;n&gt;</c>
/// (вид — имя или «раздел/имя», * — всё; чей — mine по умолчанию, all или ник), <c>damaged &lt;чей&gt;</c> (mine, all, enemy или
/// ник: прочность ниже полной), <c>lost</c> (партия оборвалась), <c>seconds N</c>, <c>frames N</c>.
/// </para>
/// </summary>
internal sealed partial class SmokeRunner
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^tick\s*(>=|<=|==|>|<)\s*(\+?)(\S+)")]
    private static partial System.Text.RegularExpressions.Regex TickCondition();

    public const double DefaultWait = 60, DefaultAct = 15, DefaultEnter = 180;

    /// <summary>Сколько ждать первого показанного кадра витрины меню, секунды; дольше — дальше без неё (в отчёте пометка).</summary>
    public const double ShowcaseLimit = 30;

    private readonly SmokeOptions _options;
    private readonly DesktopOptions _desktop;
    private readonly List<SmokeStep> _steps;
    private readonly double _waitTimeout;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, Mark> _marks = new(StringComparer.Ordinal);
    private readonly List<Result> _results = [];
    private ShellApp _app = null!;
    private Renderer? _renderer;
    private IEnumerator<bool>? _current;
    private SmokeStep? _step;
    private double _started, _deadline;
    private Inject? _inject;
    private string? _note;
    private int _frame;

    private SmokeRunner(SmokeOptions options, DesktopOptions desktop, List<SmokeStep> steps, double? timeout)
    {
        (_options, _desktop, _steps) = (options, desktop, steps);
        _waitTimeout = timeout ?? DefaultWait;
    }

    /// <summary>Отметка: тик, число сущностей и время смоука.</summary>
    private readonly record struct Mark(double Tick, double Entities, double Time);

    private sealed record Result(int Index, string Line, bool Ok, double Started, double Seconds, ulong Tick, int Entities, string? Note, string? Error);

    /// <summary>Ввод следующего кадра от шага.</summary>
    private sealed record Inject((float X, float Y)? Pointer, PointerButtons Down = PointerButtons.None, PointerButtons Pressed = PointerButtons.None,
        PointerButtons Released = PointerButtons.None, string[]? Hits = null, string[]? Keys = null, string Text = "", bool Ctrl = false, bool Shift = false, bool Alt = false);

    /// <summary>Файл отчёта.</summary>
    public string ReportPath => _options.Report ?? Path.Combine(FileStorage.ProfileDirectory, "smoke-report.json");

    /// <summary>
    /// Смоук не трогает настоящий профиль: без KHAROS_HOME профиль — новая папка во временной (её путь — в журнале).
    /// </summary>
    public static void IsolateProfile()
    {
        if (Environment.GetEnvironmentVariable(FileStorage.HomeVariable) is { Length: > 0 })
            return;
        var home = Path.Combine(Path.GetTempPath(), "kharos-smoke-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable(FileStorage.HomeVariable, home);
    }

    /// <summary>Читает сценарий; не прочитался — пишет отчёт с ошибкой и даёт null.</summary>
    public static SmokeRunner? Load(SmokeOptions options, DesktopOptions desktop)
    {
        try
        {
            var (steps, timeout) = SmokeScript.Parse(File.ReadAllText(options.Script));
            DesktopLog.Write($"смоук: {options.Script}, шагов {steps.Count}, профиль {FileStorage.ProfileDirectory}");
            return new SmokeRunner(options, desktop, steps, timeout);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            var runner = new SmokeRunner(options, desktop, [], null);
            DesktopLog.Write($"смоук: сценарий не прочитан: {error.Message}");
            runner.WriteReport(false, $"сценарий не прочитан: {error.Message}");
            return null;
        }
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>Ввод кадра: настоящий ввод скрытого окна не в счёт — только то, что подставил шаг.</summary>
    public ClientInput Input(ClientInput real)
    {
        var inject = _inject;
        _inject = null;
        return real with
        {
            Pointer = inject?.Pointer, Down = inject?.Down ?? PointerButtons.None, Pressed = inject?.Pressed ?? PointerButtons.None,
            Released = inject?.Released ?? PointerButtons.None, Wheel = 0, Hits = inject?.Hits ?? [], Keys = inject?.Keys ?? [], Text = inject?.Text ?? "",
            Ctrl = inject?.Ctrl ?? false, Shift = inject?.Shift ?? false, Alt = inject?.Alt ?? false, Focused = true, Touch = false,
        };
    }

    /// <summary>После кадра: двигает шаги. null — дальше, иначе код выхода (0 — все шаги прошли, 2 — шаг провалился).</summary>
    public int? After(ShellApp app, Renderer? renderer)
    {
        (_app, _renderer) = (app, renderer);
        _frame++;
        // Мгновенные шаги (mark, assert, chat) идут подряд в одном кадре; ограничение — от бесконечного цикла.
        for (var guard = 0; guard < 256; guard++)
        {
            if (_current is null)
            {
                var next = _step is null ? 0 : _step.Index + 1;
                if (next >= _steps.Count)
                    return Finish();
                Begin(_steps[next]);
            }
            bool more;
            try
            {
                more = _current!.MoveNext();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return Fail(error is SmokeFailure ? error.Message : $"{error.GetType().Name}: {error.Message}");
            }
            if (more)
                return Now > _deadline ? Fail($"срок {_deadline - _started:0.#} с вышел") : null;
            Complete();
        }
        return null;
    }

    private void Begin(SmokeStep step)
    {
        _step = step;
        _note = null;
        _started = Now;
        _deadline = _started + (step.Timeout ?? DefaultTimeout(step));
        _current = Lazy(step).GetEnumerator();
        DesktopLog.Write($"смоук: шаг {step.Index} «{step.Line}»");
    }

    private double DefaultTimeout(SmokeStep step) => step.Verb switch
    {
        "wait" => TrailingTimeout(step) ?? _waitTimeout,
        "connect" or "load" or "editor" or "save" => DefaultEnter,
        _ => DefaultAct,
    };

    /// <summary>«wait &lt;условие&gt; [срок]»: срок — лишнее число после слов условия.</summary>
    private static double? TrailingTimeout(SmokeStep step)
    {
        var words = step.Words;
        if (words.Length == 0)
            return null;
        var arity = words[0] switch
        {
            "menu" or "game" or "lost" => 1,
            "ui" or "allied" or "damaged" or "seconds" or "frames" => 2,
            "roster" => 3,
            "count" => 4,
            var tick when tick.StartsWith("tick", StringComparison.Ordinal) => 1,
            _ => int.MaxValue,
        };
        return words.Length == arity + 1 && double.TryParse(words[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
    }

    private void Complete()
    {
        var step = _step!;
        var (tick, entities) = Snapshot();
        _results.Add(new Result(step.Index, step.Line, true, _started, Now - _started, tick, entities, _note, null));
        DesktopLog.Write($"смоук: шаг {step.Index} готов за {Now - _started:0.00} с" + (_note is null ? "" : $" ({_note})"));
        _current?.Dispose();
        _current = null;
    }

    private int Fail(string error)
    {
        var step = _step!;
        var (tick, entities) = Snapshot();
        _results.Add(new Result(step.Index, step.Line, false, _started, Now - _started, tick, entities, _note, error));
        DesktopLog.Write($"смоук: шаг {step.Index} «{step.Line}» провален: {error}");
        _current?.Dispose();
        _current = null;
        WriteReport(false, $"шаг {step.Index}: {error}");
        return 2;
    }

    private int Finish()
    {
        DesktopLog.Write($"смоук: все {_steps.Count} шагов прошли за {Now:0.0} с");
        WriteReport(true, null);
        return 0;
    }

    private void WriteReport(bool ok, string? error)
    {
        try
        {
            var path = ReportPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            var (tick, entities) = _app is null ? (0UL, 0) : Snapshot();
            json.WriteStartObject();
            json.WriteBoolean("ok", ok);
            json.WriteString("script", _options.Script);
            json.WriteString("home", FileStorage.ProfileDirectory);
            json.WriteString("gpu", _desktop.NoGpu ? "none" : "window");
            if (error is not null)
                json.WriteString("error", error);
            if (_results.LastOrDefault(r => !r.Ok) is { } failed)
                json.WriteNumber("failed", failed.Index);
            else
                json.WriteNull("failed");
            json.WriteNumber("seconds", Math.Round(Now, 3));
            json.WriteNumber("frames", _frame);
            json.WriteNumber("tick", tick);
            json.WriteNumber("entities", entities);
            json.WriteStartArray("steps");
            foreach (var result in _results)
            {
                json.WriteStartObject();
                json.WriteNumber("index", result.Index);
                json.WriteString("do", result.Line);
                json.WriteBoolean("ok", result.Ok);
                json.WriteNumber("at", Math.Round(result.Started, 3));
                json.WriteNumber("seconds", Math.Round(result.Seconds, 3));
                json.WriteNumber("tick", result.Tick);
                json.WriteNumber("entities", result.Entities);
                if (result.Note is not null)
                    json.WriteString("note", result.Note);
                if (result.Error is not null)
                    json.WriteString("error", result.Error);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartObject("marks");
            foreach (var (name, mark) in _marks)
            {
                json.WriteStartObject(name);
                json.WriteNumber("tick", mark.Tick);
                json.WriteNumber("entities", mark.Entities);
                json.WriteNumber("time", Math.Round(mark.Time, 3));
                json.WriteEndObject();
            }
            json.WriteEndObject();
            json.WriteEndObject();
            json.Flush();
            DesktopLog.Write($"смоук: отчёт {path}");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            DesktopLog.Write($"смоук: отчёт не записан: {failure.Message}");
        }
    }

    // ------------------------------------------------------------------ состояние

    private ClientGame? Game => _app.Session is { State: SessionState.Ready, Game: { } game } ? game : null;

    private ClientGame NeedGame() => Game ?? throw new SmokeFailure("нет идущей партии");

    private (ulong Tick, int Entities) Snapshot() =>
        _app.Session is { State: SessionState.Ready, Game: { } game } ? (game.Replica.Tick, game.Replica.World?.Count ?? 0) : (0UL, 0);

    /// <summary>Интерфейс, в котором сейчас узлы: партии (идёт) или меню и окон подключения.</summary>
    private Gui Gui => Game?.Gui ?? _app.Gui;

    private bool TryRect(string id, out Rect rect)
    {
        if (Gui.RectOf(id) is { W: > 0, H: > 0 } found)
        {
            rect = found;
            return true;
        }
        rect = default;
        return false;
    }

    private double? Resolve(string word)
    {
        var (tick, entities) = Snapshot();
        switch (word)
        {
            case "tick": return tick;
            case "entities": return entities;
            case "time": return Now;
        }
        var dot = word.LastIndexOf('.');
        if (dot > 0 && _marks.TryGetValue(word[..dot], out var mark))
        {
            return word[(dot + 1)..] switch
            {
                "tick" => mark.Tick,
                "entities" => mark.Entities,
                "time" => mark.Time,
                _ => null,
            };
        }
        return null;
    }

    /// <summary>Номер игрока по «чей»: mine — свой, all — любой (null), иначе ник.</summary>
    private static uint? Owner(ClientGame game, string who) => who switch
    {
        "" or "mine" => game.Replica.Player,
        "all" or "any" or "*" => null,
        _ => game.Players.FirstOrDefault(p => string.Equals(p.Name, who, StringComparison.OrdinalIgnoreCase))?.Player
            ?? throw new SmokeFailure($"нет игрока «{who}»"),
    };

    /// <summary>Сущности вида kind у хозяина owner (null — у всех).</summary>
    private static List<uint> Entities(ClientGame game, string kind, uint? owner)
    {
        var found = new List<uint>();
        if (game.Replica.World is not { } world)
            return found;
        var protos = game.Content.Protos.ToDictionary(p => p.Id);
        var store = world.Store(Builtin.Proto);
        var ids = store.Column(0);
        var entities = store.Entities;
        for (var row = 0; row < entities.Length; row++)
        {
            var entity = entities[row];
            if (kind != "*")
            {
                if (!protos.TryGetValue((int)ids[row], out var proto) || (proto.Name != kind && $"{proto.Section}/{proto.Name}" != kind))
                    continue;
            }
            if (owner is { } player && !(world.TryNumber(entity, Builtin.Owner, 0, out var number) && (uint)number == player))
                continue;
            found.Add(entity);
        }
        found.Sort();
        return found;
    }

    /// <summary>«@вид», «@вид.x+3» для order.</summary>
    private double[] Reference(string text)
    {
        var game = NeedGame();
        var offset = 0.0;
        var cut = text.IndexOfAny(['+', '-'], 1);
        if (cut > 0)
        {
            offset = double.Parse(text[cut..], NumberStyles.Float, CultureInfo.InvariantCulture);
            text = text[..cut];
        }
        var axis = -1;
        if (text.EndsWith(".x", StringComparison.Ordinal) || text.EndsWith(".y", StringComparison.Ordinal))
        {
            axis = text[^1] == 'x' ? 0 : 1;
            text = text[..^2];
        }
        var entities = Entities(game, text, game.Replica.Player);
        if (entities.Count == 0)
            throw new SmokeFailure($"нет своих «{text}»");
        if (axis < 0)
            return [.. entities.Select(e => (double)e + offset)];
        var world = game.Replica.World!;
        return world.TryNumber(entities[0], Builtin.Position, axis, out var at) ? [at + offset] : throw new SmokeFailure($"у «{text}» нет места");
    }

    // ------------------------------------------------------------------ шаги

    /// <summary>Шаг начинается на первом MoveNext — его ошибки ловит <see cref="After"/>.</summary>
    private IEnumerable<bool> Lazy(SmokeStep step)
    {
        foreach (var frame in Run(step))
            yield return frame;
    }

    private IEnumerable<bool> Run(SmokeStep step)
    {
        var words = step.Words;
        string Arg(int i) => i < words.Length ? words[i] : throw new SmokeFailure($"{step.Verb}: не хватает слов");
        switch (step.Verb)
        {
            case "click":
                return Click(Arg(0));
            case "type":
                return Type(Arg(0), step.Field("text") ?? string.Join(' ', words.Skip(1)));
            case "key":
                return Key(Arg(0));
            case "wait":
                return Wait(step);
            case "order":
                var (name, payload) = SmokeOrder.Build(step, Reference);
                NeedGame().Send(name, payload);
                _note = $"{name}, {payload.Length} Б";
                return [];
            case "chat":
                NeedGame().Say(step.Field("text") ?? step.Rest);
                return [];
            case "save":
                return Save(Arg(0));
            case "leave":
                _app.Leave(save: true);
                return [];
            case "load":
                return Load(Arg(0));
            case "editor":
                return Editor(Arg(0), words.Length > 1 && int.TryParse(words[1], out var size) ? size : 128);
            case "connect":
                return Connect(words.Length > 0 ? words[0] : _desktop.Server ?? throw new SmokeFailure("connect: нет адреса и KHAROS_SERVER"));
            case "shot":
                Shot(Arg(0), words.Length > 1 && int.TryParse(words[1], out var colors) ? colors : 0);
                return [];
            case "mark":
                var (tick, entities) = Snapshot();
                _marks[Arg(0)] = new Mark(tick, entities, Now);
                _note = $"тик {tick}, сущностей {entities}";
                return [];
            case "assert":
                if (!SmokeExpr.Check(step.Rest, Resolve))
                    throw new SmokeFailure($"ложно: {step.Rest} ({Explain(step.Rest)})");
                return [];
            default:
                throw new SmokeFailure($"нет глагола {step.Verb}");
        }
    }

    /// <summary>Значения слов выражения для сообщения о провале.</summary>
    private string Explain(string expr) =>
        string.Join(", ", expr.Split([' ', '=', '!', '<', '>', '+', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Distinct().Select(word => Resolve(word) is { } value ? $"{word}={value.ToString(CultureInfo.InvariantCulture)}" : null).OfType<string>());

    private IEnumerable<bool> Click(string id)
    {
        Rect rect;
        while (!TryRect(id, out rect))
            yield return true;
        var at = (rect.X + rect.W / 2, rect.Y + rect.H / 2);
        _inject = new Inject(at);
        yield return true;
        _inject = new Inject(at, Down: PointerButtons.Left, Pressed: PointerButtons.Left);
        yield return true;
        _inject = new Inject(at, Released: PointerButtons.Left);
        yield return true;
    }

    private IEnumerable<bool> Type(string field, string text)
    {
        foreach (var frame in Click(field))
            yield return frame;
        _inject = new Inject(null, Hits: ["a"], Keys: ["ctrl", "a"], Ctrl: true);
        yield return true;
        _inject = new Inject(null, Text: text);
        yield return true;
    }

    private IEnumerable<bool> Key(string combo)
    {
        var parts = combo.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries);
        var key = parts[^1];
        var mods = parts[..^1];
        _inject = new Inject(null, Hits: [key], Keys: [.. mods, key], Ctrl: mods.Contains("ctrl"), Shift: mods.Contains("shift"), Alt: mods.Contains("alt"));
        yield return true;
    }

    private IEnumerable<bool> Wait(SmokeStep step)
    {
        var words = step.Words;
        if (words.Length == 0)
            throw new SmokeFailure("wait: нет условия");
        var condition = Condition(step, words);
        while (!condition())
            yield return true;
    }

    private Func<bool> Condition(SmokeStep step, string[] words)
    {
        var head = words[0];
        string Arg(int i) => i < words.Length ? words[i] : throw new SmokeFailure($"wait {head}: не хватает слов");
        if (head.StartsWith("tick", StringComparison.Ordinal))
        {
            var match = TickCondition().Match(step.Rest);
            if (!match.Success)
                throw new SmokeFailure($"wait: не разобрать «{step.Rest}» (ждал tick>=N или tick>=+N)");
            var op = match.Groups[1].Value;
            var relative = match.Groups[2].Value == "+";
            var target = (relative ? (double)Snapshot().Tick : 0) + SmokeExpr.Value(match.Groups[3].Value, Resolve);
            _note = $"тик {op} {target}";
            return () => FailFast() && Game is { } game && SmokeExpr.Compare(game.Replica.Tick, op, target);
        }
        switch (head)
        {
            case "menu":
                var idle = 0;
                return () =>
                {
                    if (_app.Session is not null)
                        return false;
                    if (Shell.ShowcaseShown(_app))
                        return true;
                    // Витрины нет (у модов нет партии showcase): партия фона не встала и ждёт повтора — ждать нечего.
                    // Два кадра подряд: между слайдами партии фона тоже нет, но лишь до следующего кадра.
                    idle = _app.Background is ShowcaseBackground { Running: false, Ready: false, Slides: > 0 } ? idle + 1 : 0;
                    if (idle >= 3)
                    {
                        _note = "витрины нет";
                        return true;
                    }
                    if (Now - _started < ShowcaseLimit)
                        return false;
                    _note = $"витрина не показалась за {ShowcaseLimit} с";
                    return true;
                };
            case "game":
                return () => FailFast() && Game is { Replica.Ready: true };
            case "lost":
                return () => _app.Session is { State: SessionState.Failed };
            case "ui":
                var id = Arg(1);
                return () => TryRect(id, out _);
            case "chat":
                var needle = step.Rest[4..].Trim();
                return () => Game is { } game && game.Chat.Any(line => game.TextOf(line).Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || line.Text.Contains(needle, StringComparison.OrdinalIgnoreCase));
            case "roster":
                var (nick, online) = (Arg(1), !string.Equals(Arg(2), "offline", StringComparison.OrdinalIgnoreCase));
                return () => Game is { } game && game.Players.Any(p => string.Equals(p.Name, nick, StringComparison.OrdinalIgnoreCase) && p.Online == online);
            case "allied":
                var ally = Arg(1);
                return () => FailFast() && Game is { } game && game.Players.FirstOrDefault(p => string.Equals(p.Name, ally, StringComparison.OrdinalIgnoreCase)) is { } player && game.Allied(player.Player);
            case "count":
                var kind = Arg(1);
                var who = "mine";
                if (kind.IndexOf('@') is > 0 and var at)
                    (kind, who) = (kind[..at], kind[(at + 1)..]);
                var cmp = Arg(2);
                var n = SmokeExpr.Value(Arg(3), Resolve);
                return () =>
                {
                    if (!FailFast() || Game is not { } game)
                        return false;
                    var count = Entities(game, kind, Owner(game, who)).Count;
                    _note = $"{kind}: {count}";
                    return SmokeExpr.Compare(count, cmp, n);
                };
            case "damaged":
                var whose = Arg(1);
                return () => FailFast() && Game is { } game && Damaged(game, whose);
            case "seconds":
                var seconds = double.Parse(Arg(1), CultureInfo.InvariantCulture);
                return () => Now - _started >= seconds;
            case "frames":
                var frames = int.Parse(Arg(1), CultureInfo.InvariantCulture);
                var until = _frame + frames;
                return () => _frame >= until;
            default:
                throw new SmokeFailure($"wait: нет условия «{head}»");
        }
    }

    /// <summary>Партия не состоялась или оборвалась — ждать её нечего: провал сразу.</summary>
    private bool FailFast() =>
        _app.Session is { State: SessionState.Failed or SessionState.Password } failed
            ? throw new SmokeFailure($"партия не состоялась: {failed.Error ?? failed.State.ToString()}")
            : true;

    private static bool Damaged(ClientGame game, string whose)
    {
        if (game.Replica.World is not { } world || world.Schema.Find("Health") is not { } health)
            return false;
        var def = world.Schema[health];
        var (value, max) = (def.FieldIndex("value"), def.FieldIndex("max"));
        var me = game.Replica.Player;
        uint? owner = whose == "enemy" ? null : Owner(game, whose);
        foreach (var entity in world.Store(health).Entities)
        {
            var mine = world.TryNumber(entity, Builtin.Owner, 0, out var number) ? (uint)number : 0;
            if (whose == "enemy" ? mine == me || mine == 0 : owner is { } o && mine != o)
                continue;
            if (world.TryNumber(entity, health, value, out var hp) && world.TryNumber(entity, health, max, out var full) && hp < full)
                return true;
        }
        return false;
    }

    /// <summary>Ждать, пока начатая партия встанет; не встала — провал.</summary>
    private IEnumerable<bool> Enter()
    {
        while (_app.Session is { State: SessionState.Connecting } || _app.Session?.Game is { Replica.Ready: false })
            yield return true;
        FailFast();
        if (_app.Session is null)
            throw new SmokeFailure("партия закрылась");
    }

    private SaveSlot? FindSlot(string name) =>
        _app.Slots.List().Find(slot => slot.Name == name) ?? _app.Slots.Find(name);

    private IEnumerable<bool> Save(string name)
    {
        byte[]? save;
        var local = _app.Session as LocalSession;
        if (_app.Session is IEditorSession editor)
            save = editor.SaveMap();
        else if (local is not null)
            save = local.StopAndSave();
        else
            throw new SmokeFailure("save: только одиночная партия или редактор");
        if (save is null)
            throw new SmokeFailure("save: мир не снят");
        var slot = FindSlot(name);
        if (slot is null)
        {
            var info = SaveInfo.Read(save);
            var (size, height, landSeed) = info.Map();
            slot = _app.Slots.Create(name, size ?? 256, landSeed ?? (long)info.Seed, height, info.Game, info.Settings);
        }
        if (!_app.Slots.Store(slot.Id, save))
            throw new SmokeFailure("save: слот не записан");
        _note = $"слот {slot.Id}, {save.Length / 1024} КБ, тик {SaveInfo.Read(save).Tick}";
        if (local is null)
            yield break;
        // Хост одиночной партии остановлен ради снимка — партия продолжается из слота.
        _app.Start(new SlotLaunch(_app.Slots.Find(slot.Id)!));
        foreach (var frame in Enter())
            yield return frame;
    }

    private IEnumerable<bool> Load(string name)
    {
        var slot = FindSlot(name) ?? throw new SmokeFailure($"load: нет слота «{name}»");
        _app.Start(new SlotLaunch(slot));
        return Enter();
    }

    private IEnumerable<bool> Editor(string name, int size)
    {
        var slot = FindSlot(name) ?? _app.Slots.Create(name, size, 1);
        _app.Start(new EditorLaunch(slot));
        return Enter();
    }

    private IEnumerable<bool> Connect(string url)
    {
        _app.Start(new ServerLaunch(url, _desktop.Name ?? "", _desktop.Password));
        return Enter();
    }

    private void Shot(string path, int minColors)
    {
        if (_renderer is null)
        {
            _note = "без GPU — снимка нет";
            return;
        }
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        _renderer.SavePng(path);
        var pixels = _renderer.ReadPixels();
        var colors = new HashSet<uint>();
        for (var i = 0; i + 3 < pixels.Length && colors.Count < 100_000; i += 4)
            colors.Add(BitConverter.ToUInt32(pixels, i));
        _note = $"{path}, цветов {colors.Count}";
        if (colors.Count < minColors)
            throw new SmokeFailure($"shot: цветов {colors.Count} меньше {minColors} — кадр пуст");
    }
}

/// <summary>Провал шага смоука с понятной причиной.</summary>
internal sealed class SmokeFailure(string message) : Exception(message);
