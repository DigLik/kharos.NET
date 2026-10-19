using System.Text;
using Kharos.Mod.Sdk.Client;
using static Base.Client.Widgets;

namespace Base.Client;

/// <summary>
/// Раздел Steps: «Первые шаги» новичка (game/firstSteps.ts, steps в gui/hud.ts) и память «видел кнопку» (game/seen.ts).
/// Окно в правом верхнем углу: список с галочками, у текущего — что сделать и зачем, «Показать» ведёт камеру к месту
/// (CameraShake.FlyTo). Заголовок сворачивает список, крестик прячет совсем; вид окна (open, folded, off) хранится в
/// настройках мода (Prefs «steps»). Мир, где всё пройдено уже при входе, списка не показывает.
/// <para>
/// Для сетки команд (раздел Hud, К3а/К3б) раздел кладёт в <c>c.Shared["steps.current"]</c> текущий <see cref="StepSpec"/>
/// (или null) — кнопку этого здания, юнита, разворота, исследований или продажи сетка подсвечивает (hint); а
/// <see cref="Seen"/> хранит «новые» кнопки (fresh, seen).
/// </para>
/// </summary>
internal sealed class StepsSection : Section
{
    /// <summary>Ключи c.Shared: текущий шаг (<see cref="StepSpec"/> или null), пройденные (bool[]), место (<see cref="StepTarget"/> или null).</summary>
    public const string CurrentKey = "steps.current", DoneKey = "steps.done", TargetKey = "steps.target";

    /// <summary>Ключ настройки вида окна в Prefs.</summary>
    public const string PrefKey = "steps";

    /// <summary>Раз в сколько секунд пересчитываются шаги: мир читается по слою месторождений, это не бесплатно.</summary>
    private const double Every = 0.2;

    private StepsReader? _reader;
    private ClientContext? _owner;
    private bool[] _done = [];
    private StepTarget? _target;
    private double _readAt = double.NegativeInfinity;
    private bool? _known;

    public override void Setup(ClientBuilder client) => client.Ui("base.steps", Frame);

    public override void Load(BaseState state)
    {
        base.Load(state);
        _reader = new StepsReader(state);
    }

    private static string Shown(ClientContext c) => c.Prefs.GetString(PrefKey, "open") is var value && value is "folded" or "off" ? value : "open";

    public override void Frame(ClientContext c)
    {
        var editor = (c.Shell & ShellMode.Editor) != 0;
        if (Profile?.Ui.Contains("hud") != true || editor || _reader is null)
        {
            c.Shared.Remove(CurrentKey);
            return;
        }
        if (!ReferenceEquals(_owner, c))
        {
            _owner = c;
            _known = null;
            _readAt = double.NegativeInfinity;
        }
        if (c.Time - _readAt >= Every)
        {
            _readAt = c.Time;
            _done = _reader.Done(c);
            var step = StepsReader.Current(_done);
            _target = step is null ? null : _reader.Target(c, step.Key);
        }
        var current = StepsReader.Current(_done);
        var shown = Shown(c);
        c.Shared[DoneKey] = _done;
        // Подсказка сетки — и при спрятанном окне (steps в game/hud.ts), место на карте — нет (stepOf в cursorPass.ts).
        c.Shared[CurrentKey] = current;
        c.Shared[TargetKey] = shown == "off" ? null : _target;

        var finished = _done.All(done => done);
        _known ??= finished;
        if (shown == "off" || (finished && _known == true))
            return;
        Draw(c, shown, finished, current);
    }

    private void Draw(ClientContext c, string shown, bool finished, StepSpec? current)
    {
        var ui = c.Ui;
        Tr t = (key, args) => c.Text(key, args);
        var top = c.Shared.TryGetValue(TopBar.TopKey, out var value) && value is float height ? height : TopBar.HeightOf(ui.Width);
        var count = _done.Count(done => done);
        var width = Math.Min(300, ui.Width - 24);
        var narrow = ui.Width < TopBar.Narrow;
        void Set(string next) => c.Prefs.Set(PrefKey, next);
        ui.Box(new Style { Id = "steps", Anchor = new Anchor(Right: 12, Top: top + 6), Width = width, Skin = Skin.Panel, Padding = (8, 10, 10, 10), Direction = Direction.Column, Align = Align.Stretch, Gap = 6 }, () =>
        {
            ui.Box(new Style { Direction = Direction.Row, Gap = 4 }, () =>
            {
                if (ui.Button("steps:toggle", new Style { Direction = Direction.Row, Align = Align.Center, Height = 28, Padding = (0, 8), Gap = 6 }, () =>
                {
                    ui.Text(t("steps.title"), Shadowed());
                    ui.Text($"{count}/{StepsReader.Steps.Count}", new TextStyle { Font = FontName.Small, Color = Gold, Shadow = Ink });
                    ui.Text(shown == "open" ? "▾" : "▸", new TextStyle { Font = FontName.Small, Shadow = Ink });
                }) is not null)
                    Set(shown == "open" ? "folded" : "open");
                ui.Tip("steps:toggle", t(shown == "open" ? "steps.foldTip" : "steps.unfoldTip"));
                if (ui.Button("steps:close", new Style { Direction = Direction.Row, Align = Align.Center, Height = 28, Padding = (0, 8) }, () => ui.Text("×", Shadowed())) is not null)
                    Set("off");
                ui.Tip("steps:close", t("steps.closeTip"));
            });
            if (shown != "open")
                return;
            if (finished)
            {
                ui.Box(new Style { Padding = (0, 2) }, () => ui.Text(t("steps.finished"), new TextStyle { Color = Good, Wrap = float.PositiveInfinity }));
                return;
            }
            var index = Array.IndexOf(_done, false);
            ui.Box(new Style { Direction = Direction.Column, Align = Align.Stretch, Gap = 4, Padding = (0, 2) }, () =>
            {
                for (var i = 0; i < StepsReader.Steps.Count; i++)
                {
                    // На узком экране — только текущий шаг: список закрыл бы поле.
                    if (narrow && i != index)
                        continue;
                    var key = StepsReader.Steps[i].Key;
                    var color = _done[i] ? Dim : i == index ? Bright : Muted;
                    var line = i;
                    ui.Box(new Style { Id = "step:" + key, Direction = Direction.Row, Gap = 8 }, () =>
                    {
                        ui.Box(new Style { Width = 14, Direction = Direction.Row, Justify = Align.End }, () =>
                            ui.Text(_done[line] ? "✓" : (line + 1).ToString(), new TextStyle { Color = _done[line] ? Good : line == index ? Team : color }));
                        ui.Box(new Style { Direction = Direction.Column, Gap = 2, Grow = 1 }, () =>
                        {
                            ui.Text(t($"steps.items.{key}.title"), new TextStyle { Color = color, Wrap = float.PositiveInfinity });
                            if (line != index)
                                return;
                            ui.Text(t($"steps.items.{key}.tip"), new TextStyle { Font = FontName.Small, Color = Soft, Wrap = float.PositiveInfinity });
                            if (_target is not { } target)
                                return;
                            ui.Space(0, 2);
                            if (ui.Button("steps:show", new Style { Direction = Direction.Row, Align = Align.Center, Height = 28, Padding = (0, 10) }, () => ui.Text(t("steps.show"), Shadowed())) is not null)
                                CameraShake.FlyTo(c, target.X + target.Width / 2, target.Y + target.Height / 2);
                            ui.Tip("steps:show", t("steps.showTip"));
                        });
                    });
                }
            });
        });
    }
}

/// <summary>
/// Кнопки стройки, на которые игрок уже наводил или нажимал (game/seen.ts): здания и инструменты протяжки. Остальные
/// открытые сетка подсвечивает как новые, а с ними и их разделы. Хранится в настройках мода (Prefs «seen», ключи через
/// перевод строки) — это знакомство игрока с игрой, а не мир.
/// </summary>
internal static class Seen
{
    private const string Key = "seen";
    private static ClientContext? _owner;
    private static HashSet<string> _set = [];

    private static HashSet<string> Load(ClientContext c)
    {
        if (!ReferenceEquals(_owner, c))
        {
            _owner = c;
            _set = [.. c.Prefs.GetString(Key).Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        }
        return _set;
    }

    /// <summary>Видел ли игрок эту кнопку — навёл или нажал: вид здания или инструмент протяжки.</summary>
    public static bool Is(ClientContext c, string key) => Load(c).Contains(key);

    /// <summary>Игрок навёл на кнопку или нажал её: дальше она не новая.</summary>
    public static void Mark(ClientContext c, string key)
    {
        var set = Load(c);
        if (set.Add(key))
            c.Prefs.Set(Key, Encoding.UTF8.GetBytes(string.Join('\n', set)));
    }
}
