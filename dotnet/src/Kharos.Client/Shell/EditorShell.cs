using Kharos.Client.Game;
using Kharos.Client.Ui;
using Kharos.Protocol;

namespace Kharos.Client.Shell;

/// <summary>
/// Режим редактора у оболочки (Н6-3; editor.ts): право, полоса режима, сохранение и выход. Панель с кистями — у мода
/// (Base.Client/Editor), он видит режим флагом <see cref="Kharos.Mod.Abi.ClientAbi.ShellEditor"/> входа кадра — его
/// ставит оболочка, пока у подключения есть право редактора (<see cref="ClientGame.Editor"/>).
/// <list type="bullet">
/// <item>Из меню (<see cref="EditorLaunch"/>): партия поднимается без тумана, оболочка сама просит право — /admin, затем
/// /editor (хост пропускает строки чата не чаще раза в полсекунды). «Сохранить» пишет мир в тот же слот или файл
/// (<see cref="IEditorSession.SaveMap"/>), «Выйти» с несохранённым спрашивает ещё раз.</item>
/// <item>Из идущей партии: /editor в чате (нужен администратор) — хост даёт право; «Выйти из редактора» — /editor снова.
/// Мир — живой, правки сразу у всех, сохраняет его сама партия.</item>
/// </list>
/// </summary>
public sealed class EditorShell(SaveSlots slots)
{
    /// <summary>Сколько ждать между /admin и /editor, секунды (ChatGap хоста — 0,5 с).</summary>
    public const double ChatGap = 0.6;

    private ClientGame? _game;
    private int _asked;
    private double _askedAt;
    private (ulong World, int Land) _saved;
    private bool _leaving;

    /// <summary>Ответ последнего сохранения (на языке игрока) или пусто.</summary>
    public string Status { get; private set; } = "";

    /// <summary>Есть ли несохранённые правки в мире редактора из меню.</summary>
    public bool Dirty => _game is { } game && Mark(game) != _saved;

    /// <summary>Режим редактора на экране: право есть или партия — редактор из меню.</summary>
    public static bool Active(IGameSession session) => session.Game?.Editor == true || session.Launch is EditorLaunch;

    /// <summary>
    /// Кадр партии с окнами оболочки: попросить право (редактор из меню) и нарисовать полосу режима. true — игрок вышел
    /// из редактора меню (оболочке — уйти в меню).
    /// </summary>
    public bool Overlay(Gui gui, Forms forms, Words words, IGameSession session, ClientGame game)
    {
        if (_game != game)
            (_game, _asked, _askedAt, _saved, _leaving, Status) = (game, 0, double.NegativeInfinity, Mark(game), false, "");
        var launch = session.Launch as EditorLaunch;
        if (launch is not null)
            AskRight(game, gui.Now);
        if (!Active(session))
            return false;
        var leave = false;
        var title = launch is not null ? words["editor.title"] : words["editor.worldTitle"];
        var subtitle = launch is not null ? launch.Name : words["editor.liveWorld"];
        if (Status.Length > 0)
            subtitle += " · " + Status;
        gui.Box(new Style
        {
            Id = "editor:bar", Anchor = new Anchor(Top: 8, CenterX: 0), Skin = Skin.Panel, Padding = (6, 10),
            Direction = Direction.Row, Align = Align.Center, Gap = 8, MaxWidth = MathF.Max(200, gui.Width - 360),
        }, () =>
        {
            gui.Text(title, new TextStyle { Font = FontName.Title, Color = Colors.Bright });
            gui.Text(subtitle, new TextStyle { Color = Colors.Dim, Ellipsis = true });
            if (launch is null)
            {
                if (forms.Button("editor:leave", words["editor.leave"], height: 26, padding: (2, 8)))
                    game.Say("/editor");
                return;
            }
            var dirty = Dirty;
            if (forms.Button("editor:save", words[launch.Slot is not null ? "editor.save" : "editor.downloadFile"] + (dirty ? " •" : ""), primary: true, height: 26, padding: (2, 8)))
                Save(session, launch, words, game);
            if (forms.Button("editor:exit", _leaving ? words["editor.exit"] + "?" : words["editor.exit"], danger: _leaving, height: 26, padding: (2, 8),
                tip: _leaving ? words["editor.exitConfirm"] : null))
            {
                if (Dirty && !_leaving)
                    _leaving = true;
                else
                    leave = true;
            }
        });
        return leave;
    }

    /// <summary>Сколько ждать права после /editor, прежде чем попросить ещё раз (хост мог отбросить строку как частую), секунды.</summary>
    public const double RetryTime = 2;

    /// <summary>
    /// Право редактора для партии из меню: /admin (локально без пароля), а когда право администратора пришло — через
    /// полсекунды /editor. Полсекунды считаются от ответа хоста, а не от просьбы: первые кадры бывают долгими, и строка,
    /// посланная по часам клиента, у хоста могла бы попасть в ChatGap и пропасть. Нет права и через
    /// <see cref="RetryTime"/> — /editor ещё раз.
    /// </summary>
    private void AskRight(ClientGame game, double now)
    {
        if (game.Editor)
            return;
        switch (_asked)
        {
            case 0 when !game.Admin:
                game.Say("/admin");
                (_asked, _askedAt) = (1, now);
                break;
            case 0 or 1 when game.Admin:
                (_asked, _askedAt) = (2, now);
                break;
            case 2 when now - _askedAt >= ChatGap:
            case 3 when now - _askedAt >= RetryTime:
                game.Say("/editor");
                (_asked, _askedAt) = (3, now);
                break;
        }
    }

    /// <summary>Мир — в слот или файл, откуда он открыт.</summary>
    public void Save(IGameSession session, EditorLaunch launch, Words words, ClientGame game)
    {
        try
        {
            if (session is not IEditorSession editor || editor.SaveMap() is not { } save)
                return;
            if (launch.Slot is { } slot)
            {
                if (!slots.Store(slot.Id, save))
                    return;
                Status = words["editor.savedToSlot"];
            }
            else if (launch.File is { } file)
            {
                SaveFile.WriteAtomic(file, save);
                Status = words["editor.savedToFile"];
            }
            (_saved, _leaving) = (Mark(game), false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            Status = error.Message;
        }
    }

    /// <summary>Что видно о правках: ревизия мира реплики и карты.</summary>
    private static (ulong World, int Land) Mark(ClientGame game) => (game.Replica.World?.Revision ?? 0, game.Replica.LandRevision);
}
