using Kharos.Client.Ui;
using Kharos.I18n;
using Kharos.Protocol;

namespace Kharos.Client.Shell;

/// <summary>
/// Чат поверх игры (chat.ts), как в Minecraft: клавиша чата (или «/») открывает строку ввода, Enter отправляет, Escape
/// закрывает. Закрытый чат показывает свежие сообщения, и через <see cref="Shown"/> секунд они гаснут; открытый —
/// всю историю с прокруткой, которая держится низа. Над строкой — подсказки дополнения команд, вариантов и ников
/// (<see cref="ChatCompletion"/>): Tab подставляет, стрелки выбирают, щелчок подставляет, не снимая фокус со строки.
/// Сообщения хоста приходят ключом и пишутся на языке игрока. Команда с паролем (/admin) спрашивает его скрытым полем.
/// </summary>
public sealed class ChatBox
{
    public const double Shown = 10, Fade = 1;
    public const int Recent = 8, Suggestions = 6;
    public const float Width = 480;

    /// <summary>Цвета — как у Minecraft, а не как у остального интерфейса: так его узнают.</summary>
    private static readonly Color White = Color.Hex(0xffffff), Shade = Color.Hex(0x3f3f3f), System = Color.Hex(0xffff55),
        SystemShade = Color.Hex(0x3f3f15), Whisper = Color.Hex(0xaaaaaa), Own = Color.Hex(0xffe066), AdminLine = Color.Hex(0xff5555);
    private static readonly Color LineBack = new(0, 0, 0, 115), SuggestionBack = new(0, 0, 0, 179), FieldBack = new(0, 0, 0, 153);

    public const string InputId = "chat:input";

    private readonly List<(ChatLine Line, double At)> _lines = [];
    private ChatLine? _lastSeen;
    private bool _opening;
    private int _chosen;

    /// <summary>Строка ввода открыта.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Набранное.</summary>
    public string Draft { get; private set; } = "";

    /// <summary>Строка спрашивает пароль администратора: набранное скрыто и уходит как /admin &lt;пароль&gt;.</summary>
    public bool Password { get; private set; }

    /// <summary>Открыть строку с текстом (клавиша чата — пусто, «/» — начало команды, просьба мода — его текст).</summary>
    public void Open(string text = "")
    {
        (IsOpen, _opening, Draft, _chosen, Password) = (true, true, text, 0, false);
    }

    public void Close() => (IsOpen, Draft, Password, _chosen) = (false, "", false, 0);

    /// <summary>Новые строки чата клиента получают время прихода (по нему закрытый чат их гасит).</summary>
    public void Take(IReadOnlyList<ChatLine> chat, double now)
    {
        // Новые — после последней виденной; её нет (ушла за предел списка) — новые все.
        var start = 0;
        for (var i = chat.Count - 1; i >= 0 && _lastSeen is not null; i--)
            if (ReferenceEquals(chat[i], _lastSeen))
            {
                start = i + 1;
                break;
            }
        for (var i = start; i < chat.Count; i++)
            _lines.Add((chat[i], now));
        if (chat.Count > 0)
            _lastSeen = chat[^1];
        if (_lines.Count > 200)
            _lines.RemoveRange(0, _lines.Count - 200);
    }

    /// <summary>
    /// Чат в gui у левого нижнего угла. say — отправить строку хосту; text — строка на языке игрока; self — свой номер
    /// (свои строки — другим цветом). Возвращает подсказки кадра (для тестов).
    /// </summary>
    public List<Completion> Draw(Gui gui, Words words, IReadOnlyList<CommandInfo> commands, IReadOnlyList<string> names, bool admin, uint self,
        Func<ChatLine, string> text, Action<string> say, float left, float bottom)
    {
        var width = MathF.Min(Width, gui.Width - 24);
        var now = gui.Now;
        var shown = IsOpen ? _lines : [.. _lines.TakeLast(Recent).Where(item => now - item.At < Shown)];
        if (!IsOpen && shown.Count == 0)
            return [];
        var suggestions = Password ? [] : ChatCompletion.Complete(Draft, commands, names, words.Texts, words.Lang).Take(Suggestions).ToList();
        var pick = Math.Min(_chosen, suggestions.Count - 1);
        gui.Open(new Style { Id = "chat", Anchor = new Anchor(Left: left, Bottom: bottom), Width = width, Direction = Direction.Column, Passive = !IsOpen });
        try
        {
            gui.Box(new Style { Id = "chat:lines", Direction = Direction.Column, MaxHeight = MathF.Round(gui.Height * 0.4f), Scroll = IsOpen, ScrollEnd = IsOpen, Clip = true, Align = Align.Stretch }, () =>
            {
                foreach (var (line, at) in shown)
                {
                    var fade = IsOpen ? 1f : (float)Math.Min(1, (Shown - (now - at)) / Fade);
                    gui.Box(new Style { Background = LineBack, Padding = (1, 6), Alpha = fade }, () =>
                    {
                        var body = text(line);
                        var style = new TextStyle { Wrap = width - 12, Lift = 2 };
                        if (line.Kind == ChatKind.System)
                            gui.Rich([Run.Of(line.Name.Length > 0 ? $"{line.Name} {body}" : body, style with { Color = System, Shadow = SystemShade })], width - 12);
                        else if (line.Kind == ChatKind.Whisper)
                            gui.Rich([Run.Of($"<{line.Name} → {line.To}> {body}", style with { Color = Whisper, Shadow = Shade })], width - 12);
                        else
                            gui.Rich([Run.Of($"<{line.Name}>", style with { Color = line.Player == self ? Own : White, Shadow = Shade }), Run.Of($" {body}", style with { Color = White, Shadow = Shade })], width - 12);
                    });
                }
            });
            if (!IsOpen)
                return suggestions;
            if (suggestions.Count > 0)
            {
                gui.Box(new Style { Direction = Direction.Column, Align = Align.Stretch, Padding = (2, 0, 0, 0) }, () =>
                {
                    for (var i = 0; i < suggestions.Count; i++)
                    {
                        var id = $"chat:suggestion:{i}";
                        var item = suggestions[i];
                        var chosen = i == pick;
                        gui.Box(new Style { Id = id, KeepFocus = true, Background = SuggestionBack, Padding = (1, 6), Direction = Direction.Row, Gap = 6 }, () =>
                        {
                            gui.Text(item.Label, new TextStyle { Color = chosen ? System : White });
                            gui.Text(item.Help, new TextStyle { Color = Whisper });
                        });
                        if (gui.Clicked(id) is not null)
                            (Draft, _chosen) = (item.Text, 0);
                    }
                });
            }
            var field = gui.Input(InputId, Draft, new FieldOptions
            {
                Width = width, Password = Password, MaxLength = 200, Focus = _opening, Background = FieldBack,
                Placeholder = Password ? words["hud.adminPassword"] : admin ? words["hud.adminMode"] : null,
                Outline = admin ? AdminLine : null, Color = White,
            });
            _opening = false;
            if (field.Value != Draft)
                (Draft, _chosen) = (field.Value, 0);
            // Строка ушла из фокуса (щелчок мимо) — чат закрывается, как по Escape.
            var blurred = !field.Focused;
            foreach (var key in field.Keys)
            {
                switch (key)
                {
                    case "escape":
                        Close();
                        // Строка ушла — клавиши сразу снова у игры, а не через кадр.
                        gui.Blur();
                        return suggestions;
                    case "tab" when pick >= 0:
                        (Draft, _chosen) = (suggestions[pick].Text, 0);
                        break;
                    case "up" or "down" when suggestions.Count > 0:
                        _chosen = (pick + (key == "up" ? -1 : 1) + suggestions.Count) % suggestions.Count;
                        break;
                    case "enter":
                        Submit(commands, say);
                        if (!IsOpen)
                            gui.Blur();
                        return suggestions;
                }
            }
            if (blurred)
                Close();
            return suggestions;
        }
        finally
        {
            gui.Close();
        }
    }

    /// <summary>Enter: команда с паролем спрашивает его скрытым полем, иначе строка уходит хосту.</summary>
    private void Submit(IReadOnlyList<CommandInfo> commands, Action<string> say)
    {
        var first = Draft.Trim().ToLowerInvariant().Split(' ')[0];
        var secret = commands.FirstOrDefault(command => command.Secret && first == $"/{command.Name}");
        if (!Password && secret is not null)
        {
            (Password, Draft, _opening) = (true, "", true);
            return;
        }
        if (Password)
        {
            if (Draft.Length > 0)
                say($"/admin {Draft}");
        }
        else if (Draft.Trim().Length > 0)
            say(Draft);
        Close();
    }
}
