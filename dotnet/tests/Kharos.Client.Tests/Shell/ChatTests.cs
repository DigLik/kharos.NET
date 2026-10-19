using Kharos.Client.Shell;
using Kharos.Client.Ui;
using Kharos.I18n;
using Kharos.Protocol;

namespace Kharos.Client.Tests.Shell;

/// <summary>Чат оболочки: дополнение команд (chatCommands.ts), строка ввода, пароль администратора, строки на языке игрока.</summary>
public sealed class ChatTests
{
    private static readonly CommandInfo[] Commands =
    [
        new("help", [], "список команд"),
        new("msg", ["<player>", "<text>"], "личное сообщение"),
        new("me", ["<text>"], "о себе"),
        new("spawn", ["<unit>", "[count]", "[player]"], "поставить юнитов", Choices: [["buggy", "infantry", "harvester"], null, null]),
        new("admin", ["<password>"], "режим администратора", Secret: true),
    ];

    private static readonly string[] Names = ["Игрок 3", "Ann", "anton"];

    private static List<Completion> Complete(string draft, string lang = "ru") => ChatCompletion.Complete(draft, Commands, Names, Texts.Shell(), lang);

    [Fact]
    public void CommandsCompleteByPrefixWithLocalizedLabels()
    {
        Assert.Empty(Complete("привет"));
        Assert.Equal(["/help ", "/msg ", "/me ", "/spawn ", "/admin "], Complete("/").Select(c => c.Text));
        var me = Assert.Single(Complete("/ME"));
        Assert.Equal("/me ", me.Text);
        Assert.Equal(["/msg ", "/me "], Complete("/m").Select(c => c.Text));
        // Подпись — на языке игрока: аргументы и описание из словаря.
        var msg = Complete("/msg")[0];
        Assert.Equal("/msg <игрок> <текст>", msg.Label);
        Assert.Equal("личное сообщение", msg.Help);
        Assert.Equal("/msg <player> <text>", Complete("/msg", "en")[0].Label);
        Assert.Empty(Complete("/nope "));
    }

    [Fact]
    public void ArgumentsCompleteFromChoicesAndNicksQuotedWithSpaces()
    {
        Assert.Equal(["/spawn buggy "], Complete("/spawn b").Select(c => c.Text));
        Assert.Equal(["buggy", "infantry", "harvester"], Complete("/spawn ").Select(c => c.Label));
        Assert.Equal("юнит", Complete("/spawn ")[0].Help.Trim('<', '>'));
        Assert.Equal(["/msg \"Игрок 3\" "], Complete("/msg и").Select(c => c.Text));
        Assert.Equal(["/msg Ann ", "/msg anton "], Complete("/msg an").Select(c => c.Text));
        // Открытая кавычка — ник ещё набирается вместе с пробелом.
        Assert.Equal(["/msg \"Игрок 3\" "], Complete("/msg \"Игрок ").Select(c => c.Text));
        // Дальше ника — текст: подсказка самой команды.
        Assert.Equal(["/msg Ann при"], Complete("/msg Ann при").Select(c => c.Text));
        Assert.Equal(["Игрок 3", "x y"], ChatCompletion.SplitWords("\"Игрок 3\" \"x y"));
    }

    [Fact]
    public void ChatSendsLineAndAsksAdminPasswordHidden()
    {
        using var fonts = Fonts.Load();
        var gui = new Gui(fonts);
        var words = new Words(Texts.Shell(), () => "ru");
        var chat = new ChatBox();
        var said = new List<string>();
        var time = 0.0;
        List<Completion> Frame(string[]? hits = null, string text = "")
        {
            time += 0.016;
            gui.Begin(new UiInput { Time = time, Width = 800, Height = 600, Hits = hits ?? [], Text = text });
            var suggestions = chat.Draw(gui, words, Commands, Names, admin: false, self: 1, line => line.Text, said.Add, 12, 12);
            gui.Finish();
            return suggestions;
        }

        chat.Open("/");
        Assert.Equal(5, Frame().Count);
        Assert.Equal(ChatBox.InputId, gui.Focused);
        Frame(text: "sp");
        Assert.Equal("/sp", chat.Draft);
        Frame(hits: ["tab"]);
        Assert.Equal("/spawn ", chat.Draft);
        // Подставленное поле берёт в следующем кадре; набор — после него.
        Frame();
        Frame(text: "i");
        Frame(hits: ["tab"]);
        Assert.Equal("/spawn infantry ", chat.Draft);
        Frame();
        Frame(hits: ["enter"]);
        Assert.Equal(["/spawn infantry "], said);
        Assert.False(chat.IsOpen);

        // /admin спрашивает пароль скрытым полем и шлёт его командой; в строке чата его не видно.
        chat.Open("/admin");
        Frame();
        Frame(hits: ["enter"]);
        Assert.True(chat.Password);
        Assert.True(chat.IsOpen);
        Frame();
        Frame(text: "pw");
        Frame(hits: ["enter"]);
        Assert.Equal(["/spawn infantry ", "/admin pw"], said);
        Assert.False(chat.IsOpen);

        // Escape закрывает, ничего не отправив.
        chat.Open();
        Frame();
        Frame(text: "привет");
        Frame(hits: ["escape"]);
        Assert.False(chat.IsOpen);
        Assert.Equal(2, said.Count);
    }

    [Fact]
    public void ClosedChatShowsRecentLinesThenFades()
    {
        var chat = new ChatBox();
        var lines = new List<ChatLine> { new(2, "Ann", "привет"), new(0, "", "joins", ChatKind.System, Args: ["name", "Ann"]) };
        using var fonts = Fonts.Load();
        var gui = new Gui(fonts);
        var words = new Words(Texts.Shell(), () => "ru");
        var shown = new List<string>();
        void Frame(double time)
        {
            shown.Clear();
            gui.Begin(new UiInput { Time = time, Width = 800, Height = 600 });
            chat.Draw(gui, words, [], [], false, 1, line => { shown.Add(line.Text); return line.Text; }, _ => { }, 12, 12);
            gui.Finish();
        }
        chat.Take(lines, now: 0);
        chat.Take(lines, now: 1);
        Frame(2);
        Assert.Equal(["привет", "joins"], shown);
        lines.Add(new ChatLine(1, "Я", "и тебе"));
        chat.Take(lines, now: 5);
        Frame(5.5);
        Assert.Equal(["привет", "joins", "и тебе"], shown);
        Frame(12);
        Assert.Equal(["и тебе"], shown);
        Frame(30);
        Assert.Empty(shown);
        Assert.Null(gui.RectOf("chat"));
    }

    [Fact]
    public void CommandsCarryChoicesAndEditorRightOverTheWire()
    {
        var message = Messages.Commands(Commands, admin: true, editor: true);
        var (commands, admin, editor) = Messages.ReadRights(message);
        Assert.True(admin && editor);
        var spawn = commands.Single(command => command.Name == "spawn");
        Assert.Equal(["buggy", "infantry", "harvester"], spawn.Choices![0]!);
        Assert.Null(spawn.Choices[1]);
        Assert.True(commands.Single(command => command.Name == "admin").Secret);
        Assert.Null(commands.Single(command => command.Name == "help").Choices);
        // Без редактора байта нет — сообщение как прежде.
        Assert.False(Messages.ReadRights(Messages.Commands([new CommandInfo("help", [], "")], admin: false)).Editor);
        Assert.Equal(2, Messages.ReadCommands(Messages.Commands([new CommandInfo("help", [], ""), new CommandInfo("me", ["<text>"], "")], false)).Commands.Count);
    }
}
