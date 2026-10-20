using System.Text;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Kharos.Base.Scenario;
using Kharos.Content;
using Kharos.Engine.World;
using Kharos.Protocol;
using MemoryPack;

namespace Kharos.Host.Tests;

/// <summary>
/// Чат и команды хоста (packages/tests/chat.test.ts, docs/dotnet-n6.md, Н6-7). Хост и его чат — движка, но команды /give,
/// /spawn, /killunits, /heal, /credits и вести о поражении и пуске — мода base, поэтому партия настоящая: Sim с модом
/// base, поверх неё HostSession с подключениями. Два заголовка TS — дополнение команд — проверяет клиент
/// (Kharos.Client.Tests/Shell/ChatTests.cs) под теми же названиями.
/// </summary>
public sealed class ChatTests
{
    private static readonly object Rules = new { protection = 0, techTree = false };
    private static readonly Kharos.I18n.Texts Shell = Kharos.I18n.Texts.Shell();

    /// <summary>Строка чата на русском: сообщения хоста идут ключом и подстановками, слова человека — как есть.</summary>
    private static string Shown(ChatLine line) => line.Args is null ? line.Text : Shell.Host("ru", line.Text, line.Args);

    /// <summary>Подключённый клиент: что пришло в чат (игроков, хоста о игроках, хоста лично ему), команды, игроки.</summary>
    private sealed class Peer
    {
        public Peer(Connection connection, MemoryTransport end)
        {
            Connection = connection;
            End = end;
        }

        public Connection Connection { get; }
        public MemoryTransport End { get; }
        public uint Player => Connection.Player;
        public List<ChatLine> Lines { get; } = [];
        public List<ChatLine> Notices { get; } = [];
        public List<string> Replies { get; } = [];
        public List<string> CommandNames { get; private set; } = [];
        public bool Admin { get; private set; }
        public List<List<string>> Rosters { get; } = [];

        public void Pump()
        {
            while (End.TryReceive(out var message))
                switch ((MessageType)message[0])
                {
                    case MessageType.Chat:
                        foreach (var line in Messages.ReadChat(message))
                        {
                            if (line.Kind != ChatKind.System)
                                Lines.Add(line);
                            else if (line.Name.Length > 0)
                                Notices.Add(line);
                            else
                                Replies.Add(Shown(line));
                        }
                        break;
                    case MessageType.Commands:
                        var (commands, admin) = Messages.ReadCommands(message);
                        CommandNames = [.. commands.Select(command => command.Name)];
                        Admin = admin;
                        break;
                    case MessageType.Players:
                        Rosters.Add([.. Messages.ReadPlayers(message).Select(info => info.Name)]);
                        break;
                }
        }

        public void Say(string text) => End.Send(Messages.Say(text));
    }

    /// <summary>Часы, которые двигает тест: <see cref="HostSessionOptions.Time"/>.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    /// <summary>Партия base с хостом сессии: Sim ставит сцену (правки без тика), сессия ведёт игроков и тики.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly List<Peer> _peers = [];

        public Rig(string game = "test", string? password = null, TimeSpan? gap = null, bool zeroGap = true)
        {
            Assert.SkipWhen(Sim.Unavailable(Sim.DefaultMode) is { } reason, Sim.Unavailable(Sim.DefaultMode) ?? "");
            Sim = Sim.Create(new SimOptions { Game = game, Size = game == "test" ? 128 : null, Land = "default", Rules = Rules, Players = [] });
            Session = new HostSession(Sim.Host, new HostSessionOptions
            {
                SendRate = 0,
                AdminPassword = password,
                ChatGap = gap ?? (zeroGap ? TimeSpan.Zero : TimeSpan.FromMilliseconds(500)),
                Time = Time,
                Log = Log.Add,
            });
        }

        public Sim Sim { get; }
        public HostSession Session { get; }

        /// <summary>Часы сессии: стоят, пока тест их не сдвинет (пауза чата и запрет /admin не зависят от загрузки машины).</summary>
        public ManualTime Time { get; } = new();
        public List<string> Log { get; } = [];

        public Peer Join(string name, string? token = null)
        {
            var (hostEnd, end) = MemoryTransport.Pair();
            var peer = new Peer(Session.Join(hostEnd, token, name), end);
            _peers.Add(peer);
            peer.Pump();
            return peer;
        }

        public Peer Tab(Peer of)
        {
            var (hostEnd, end) = MemoryTransport.Pair();
            var peer = new Peer(Session.Connect(hostEnd, of.Player), end);
            _peers.Add(peer);
            peer.Pump();
            return peer;
        }

        public void Step(int ticks = 1)
        {
            for (var i = 0; i < ticks; i++)
            {
                Session.Step();
                foreach (var peer in _peers)
                    peer.Pump();
            }
        }

        /// <summary>Строка от игрока и тик: команда выполнена, ответ дошёл.</summary>
        public void Say(Peer peer, string text)
        {
            peer.Say(text);
            Step();
        }

        /// <summary>Администратор консоли: ответы строками, тик после команды.</summary>
        public List<string> Console(string text)
        {
            var replies = new List<string>();
            Session.Command(text, new Caller(null, true, replies.Add));
            Step();
            return replies;
        }

        /// <summary>Свободное место под здание этого вида: левый верхний тайл.</summary>
        public (int X, int Y) Spot(string type, int skip = 0)
        {
            for (var y = 10; y < 110; y += 2)
                for (var x = 10; x < 110; x += 2)
                    if (Sim.Query<bool>(KernelQueries.CanPlace, new PlaceArgs { Type = type, X = x, Y = y }) && skip-- <= 0)
                        return (x, y);
            throw new InvalidOperationException($"нет места под {type}");
        }

        public void Dispose() => Sim.Dispose();
    }

    [Fact(DisplayName = "чат: сообщение уходит всем с ником автора, хост его не хранит")]
    public void MessageGoesToEveryoneWithAuthorName()
    {
        using var rig = new Rig(zeroGap: false);
        var one = rig.Join("Первый");
        var two = rig.Join("Второй");
        rig.Say(one, "  привет\u0007   всем  ");
        Assert.Equal([(one.Player, "Первый", "привет всем")], one.Lines.Select(line => (line.Player, line.Name, line.Text)));
        Assert.Equal(one.Lines, two.Lines);
        // Чаще раза в полсекунды — не проходит; пустое — тоже.
        one.Say("ещё");
        two.Say("   ");
        rig.Step();
        Assert.Single(two.Lines);
        rig.Time.Advance(TimeSpan.FromMilliseconds(550));
        rig.Say(one, new string('x', 250));
        Assert.Equal(Kharos.Host.PlayerRoster.MaxChat, two.Lines[^1].Text.Length);
        var late = rig.Join("Поздний");
        Assert.Empty(late.Lines);
    }

    [Fact(DisplayName = "сервер пишет в чат, кто зашёл и вышел; вторая вкладка того же игрока — не в счёт")]
    public void ServerAnnouncesJoinsAndLeaves()
    {
        using var rig = new Rig();
        var one = rig.Join("Первый");
        var two = rig.Join("Второй");
        rig.Step();
        Assert.Contains(one.Notices, line => line is { Name: "Второй", Text: "joins", Args: not null } && line.Player == two.Player);
        var count = one.Notices.Count;
        var tab = rig.Tab(two);
        rig.Step();
        Assert.Equal(count, one.Notices.Count);
        two.End.Dispose();
        rig.Step();
        Assert.Equal(count, one.Notices.Count);
        tab.End.Dispose();
        rig.Step();
        Assert.Equal("leaves", one.Notices[^1].Text);
        Assert.Equal("Второй", one.Notices[^1].Name);
    }

    [Fact(DisplayName = "сервер пишет в чат о поражении игрока один раз")]
    public void ServerAnnouncesDefeatOnce()
    {
        using var rig = new Rig();
        var one = rig.Join("Первый");
        var two = rig.Join("Второй");
        // Игрок жив, пока у него есть MCV: стоит один — и обход его заметил.
        var (x, y) = rig.Spot("command");
        var mcv = rig.Sim.PutUnit("mcv", two.Player, x + 1.5, y + 1.5);
        rig.Step(12);
        Assert.DoesNotContain(one.Notices, line => line.Text == "defeated");
        rig.Sim.Erase(mcv);
        // Поражения хост проверяет раз в несколько тиков.
        rig.Step(20);
        Assert.Equal([(two.Player, "Второй")], one.Notices.Where(line => line.Text == "defeated").Select(line => (line.Player, line.Name)));
        Assert.Equal("терпит поражение", Shown(one.Notices.Single(line => line.Text == "defeated")));
        rig.Step(30);
        Assert.Single(one.Notices, line => line.Text == "defeated");
    }

    [Fact(DisplayName = "команды: ответ только автору, админские скрыты без /admin, пароль в чат не уходит")]
    public void CommandRepliesGoToAuthorAndAdminOnesAreHidden()
    {
        using var rig = new Rig(password: "секрет");
        var one = rig.Join("Первый");
        var two = rig.Join("Второй");
        var spy = rig.Join("Третий");
        Assert.DoesNotContain("kick", spy.CommandNames);
        Assert.DoesNotContain("spawn", spy.CommandNames);
        Assert.Contains("give", spy.CommandNames);
        rig.Say(spy, "/kick Первый");
        Assert.Contains(spy.Replies, reply => reply.StartsWith("Нет команды /kick"));
        Assert.Empty(one.Replies);
        rig.Say(spy, "/spawn tank");
        Assert.Contains(spy.Replies, reply => reply.StartsWith("Нет команды /spawn"));
        rig.Say(spy, "/admin неверно");
        Assert.Contains("Неверный пароль", spy.Replies);
        Assert.DoesNotContain(one.Lines.Concat(one.Notices), line => line.Text.Contains("неверно"));
        Assert.DoesNotContain(two.Lines.Concat(two.Notices), line => line.Text.Contains("неверно"));
        // После неверного пароля несколько секунд не пускает и с верным.
        rig.Say(spy, "/admin секрет");
        Assert.False(spy.Admin);
        rig.Time.Advance(TimeSpan.FromMilliseconds(3100));
        rig.Say(spy, "/admin секрет");
        Assert.True(spy.Admin);
        Assert.Contains("kick", spy.CommandNames);
        Assert.Contains("spawn", spy.CommandNames);
        rig.Say(spy, "/say всем привет");
        Assert.Equal(("[Сервер]", "всем привет"), (two.Notices[^1].Name, two.Notices[^1].Text));
        rig.Say(spy, "/credits Второй +500");
        Assert.Contains(spy.Replies, reply => reply.StartsWith("У Второй теперь"));
        Assert.DoesNotContain(two.Replies, reply => reply.StartsWith("У Второй теперь"));
    }

    [Fact(DisplayName = "консоль: /msg уходит только адресату, /kick отключает игрока")]
    public void ConsoleMessagesGoToAddresseeAndKickDisconnects()
    {
        using var rig = new Rig();
        var one = rig.Join("Первый");
        var two = rig.Join("Игрок 2");
        var three = rig.Join("Третий");
        rig.Say(one, "/msg \"Игрок 2\" тайна, \"в кавычках\"");
        Assert.Contains(two.Lines, line => line is { Kind: ChatKind.Whisper, Name: "Первый", Text: "тайна, \"в кавычках\"", To: "Игрок 2" });
        Assert.Empty(three.Lines);
        var four = rig.Join("Четвёртый");
        rig.Console("kick Четвёртый");
        Assert.True(four.End.Closed);
        Assert.Contains("Нет команды", rig.Console("nope")[^1]);
    }

    [Fact(DisplayName = "админ: spawn у базы, killunits, heal, reset и reveal")]
    public void AdminSpawnKillHealResetAndReveal()
    {
        using var rig = new Rig("skirmish");
        var one = rig.Join("Первый");
        rig.Step(2);
        var home = rig.Spot("command");
        var core = rig.Sim.PutBuilding("command", home.X, home.Y, one.Player);
        int Units() => rig.Sim.Of(one.Player, "Unit").Count;
        var before = Units();
        Assert.True(before > 0);
        var replies = rig.Console("spawn tank 3 Первый");
        Assert.Equal(before + 3, Units());
        Assert.Contains(one.Replies.Concat(replies), reply => reply.Contains("tank × 3"));
        Assert.Contains("Юниты:", rig.Console("spawn nonsense")[^1]);
        Assert.Equal(before + 3, Units());
        // Рана и лечение: здание с половиной прочности после /heal цело.
        rig.Sim.SetHealth(0.3, core);
        Assert.Contains("починено", rig.Console("heal Первый")[^1]);
        Assert.Equal(rig.Sim.Num(core, "Health", "max"), rig.Sim.Num(core, "Health", "value"));
        Assert.Contains("Убрано юнитов: " + (before + 3), rig.Console("killunits all")[^1]);
        Assert.Equal(0, Units());
        rig.Console("reset Первый");
        Assert.True(Units() > 0);
        Assert.False(rig.Sim.Explored(one.Player, 505.5, 505.5));
        rig.Console("reveal Первый");
        Assert.True(rig.Sim.Explored(one.Player, 505.5, 505.5));
        Assert.Contains("Первый", rig.Console("players")[^1]);
    }

    [Fact(DisplayName = "/heal и /killunits без игрока — про автора, как в host.ts; из консоли без игрока — «Укажите игрока»")]
    public void HealAndKillUnitsWithoutPlayerActOnAuthor()
    {
        using var rig = new Rig("skirmish", password: "секрет");
        var admin = rig.Join("Админ");
        var other = rig.Join("Другой");
        rig.Step(2);
        rig.Say(admin, "/admin секрет");
        int Units(Peer peer) => rig.Sim.Of(peer.Player, "Unit").Count;
        var others = Units(other);
        Assert.True(Units(admin) > 0 && others > 0);
        var home = rig.Spot("command");
        var core = rig.Sim.PutBuilding("command", home.X, home.Y, admin.Player);
        rig.Sim.SetHealth(0.3, core);
        rig.Say(admin, "/heal");
        Assert.Equal(rig.Sim.Num(core, "Health", "max"), rig.Sim.Num(core, "Health", "value"));
        Assert.Contains(admin.Replies, reply => reply.Contains("починено"));
        rig.Say(admin, "/killunits");
        Assert.Equal(0, Units(admin));
        Assert.Equal(others, Units(other));
        Assert.Contains(admin.Replies, reply => reply.StartsWith("Убрано юнитов:"));
        Assert.Equal("Укажите игрока", rig.Console("heal")[^1]);
        Assert.Equal("Укажите игрока", rig.Console("killunits")[^1]);
    }

    [Fact(DisplayName = "дополнение: команды по началу, ники в кавычках, если в них пробел")]
    public void CompletionCommandsByPrefixAndQuotedNames()
    {
        CommandInfo[] commands = [new("msg", ["<player>", "<text>"], ""), new("me", ["<action>"], "")];
        List<string> Complete(string draft, params string[] names) => [.. Kharos.Client.Shell.ChatCompletion.Complete(draft, commands, names).Select(item => item.Text)];
        Assert.Equal(["/msg ", "/me "], Complete("/m"));
        Assert.Equal(["/msg \"Игрок 2\" "], Complete("/msg иг", "Игрок 2", "Вася"));
        Assert.Equal(["/msg Вася "], Complete("/msg ", "Вася"));
        Assert.Empty(Complete("привет", "Вася"));
    }

    [Fact(DisplayName = "дополнение аргумента из списка: виды юнитов")]
    public void CompletionOfArgumentFromChoices()
    {
        CommandInfo[] commands = [new("spawn", ["<юнит>"], "", Choices: [["tank", "trike"]])];
        Assert.Equal(["/spawn tank ", "/spawn trike "], Kharos.Client.Shell.ChatCompletion.Complete("/spawn t", commands, []).Select(item => item.Text));
    }

    [Fact(DisplayName = "команда мода из mod.json: виды юнитов для дополнения хост берёт из данных мода")]
    public void DeclaredCommandGetsUnitChoicesFromData()
    {
        using var rig = new Rig();
        var info = Assert.Single(rig.Session.Commands.Values, command => command.Name == "spawn").Info;
        Assert.Equal(["<unit>", "[count]", "[player]"], info.Args);
        Assert.Contains("tank", info.Choices![0]!);
        Assert.Null(info.Choices[1]);
    }

    [Fact(DisplayName = "редактор живого мира: только после /editor, мир без тумана, правки применяются к миру хоста")]
    public void LiveEditorOnlyAfterEditorCommand()
    {
        using var rig = new Rig(password: "секрет");
        var admin = rig.Join("Админ");
        var other = rig.Join("Другой");
        int Units() => rig.Sim.Of(other.Player, "Unit").Count;
        var before = Units();
        var (x, y) = rig.Spot("command");
        void Edit(double dx = 0) => admin.End.Send(DeltaCodec.Order(EditUnitOrder.Name, MemoryPackSerializer.Serialize(new EditUnitOrder { Type = "tank", X = x + 1.5 + dx, Y = y + 1.5, Player = other.Player })));
        // Без редактора правка молча отбрасывается.
        Edit();
        rig.Step();
        Assert.Equal(before, Units());
        rig.Say(admin, "/admin секрет");
        // /editor — не для того, кто просто знает про чат: нужен режим администратора.
        Assert.False(admin.Connection.Editor);
        rig.Say(admin, "/editor");
        Assert.True(admin.Connection.Editor);
        Assert.Contains(admin.Replies, reply => reply.StartsWith("Редактор"));
        Edit();
        rig.Step();
        Assert.Equal(before + 1, Units());
        // Другой правок не делает: у него нет права.
        other.End.Send(DeltaCodec.Order(EditUnitOrder.Name, MemoryPackSerializer.Serialize(new EditUnitOrder { Type = "tank", X = x + 3.5, Y = y + 1.5, Player = other.Player })));
        rig.Step();
        Assert.Equal(before + 1, Units());
    }

    [Fact(DisplayName = "/hide: скрытого администратора нет в списке игроков у других, вход не объявляется")]
    public void HiddenAdminIsNotListedAndNotAnnounced()
    {
        using var rig = new Rig(password: "секрет");
        var admin = rig.Join("Админ");
        var other = rig.Join("Другой");
        var watcher = rig.Join("Смотрящий");
        rig.Say(admin, "/admin секрет");
        rig.Say(admin, "/hide");
        Assert.DoesNotContain("Админ", watcher.Rosters[^1]);
        Assert.Contains("Админ", admin.Rosters[^1]);
        Assert.Equal(("Админ", "leaves"), (other.Notices[^1].Name, other.Notices[^1].Text));
        var count = other.Notices.Count;
        var token = admin.Connection.Token;
        admin.End.Dispose();
        rig.Step();
        rig.Join("Админ", token);
        Assert.Equal(count, other.Notices.Count);
    }

    [Fact(DisplayName = "сервер пишет в чат о подготовке ядерного пуска один раз на пуск, без места и цели")]
    public void ServerAnnouncesNukeLaunchOncePerLaunchWithoutPlaceOrTarget()
    {
        using var rig = new Rig();
        var one = rig.Join("Первый");
        var two = rig.Join("Второй");
        var (x, y) = rig.Spot("silo");
        var silo = rig.Sim.PutBuilding("silo", x, y, two.Player);
        rig.Sim.Set(silo, "Silo.ready", 1);
        rig.Sim.Send(two.Player, new LaunchOrder { Silo = silo, X = 40, Y = 40 });
        rig.Step(10);
        var launches = one.Notices.Where(line => line.Text == "nukeLaunch").ToList();
        var launch = Assert.Single(launches);
        Assert.Equal((two.Player, "Второй", ChatKind.System), (launch.Player, launch.Name, launch.Kind));
        Assert.Equal("готовит ядерный пуск", Shown(launch));
        Assert.DoesNotContain("40", string.Join(' ', launch.Args ?? []));
        // Навели заново — это тот же пуск: второй вести нет.
        rig.Sim.Send(two.Player, new LaunchOrder { Silo = silo, X = 50, Y = 50 });
        rig.Step(10);
        Assert.Single(one.Notices, line => line.Text == "nukeLaunch");
    }

    // ===== Команды мода: объявление, разбор, права =====

    [Fact(DisplayName = "команды мода: нет обязательного слова, игрока или вида юнита — ответ автору, а приказ не уходит; приказ мимо чата без права админа отказан")]
    public void ModCommandsAnswerOnBadInputAndKeepTheirRights()
    {
        using var rig = new Rig(password: "секрет");
        var admin = rig.Join("Админ");
        var other = rig.Join("Другой");
        rig.Say(admin, "/admin секрет");
        rig.Say(admin, "/give");
        Assert.Contains(admin.Replies, reply => reply.StartsWith("Как: /give"));
        rig.Say(admin, "/give Никто 5");
        Assert.Contains(admin.Replies, reply => reply == "Нет игрока «Никто»");
        rig.Say(admin, "/give Другой 5");
        Assert.Contains(admin.Replies, reply => reply.Contains("не союзник"));
        rig.Say(admin, "/spawn nonsense");
        Assert.Contains(admin.Replies, reply => reply.StartsWith("Юниты:"));
        rig.Say(admin, "/killunits Никто");
        Assert.Contains(admin.Replies, reply => reply == "Нет игрока «Никто»");
        // Мимо чата: приказ chat.heal от обычного игрока (право Admin у него нет) мод отклоняет.
        var core = rig.Spot("command");
        var building = rig.Sim.PutBuilding("command", core.X, core.Y, other.Player);
        rig.Sim.SetHealth(0.3, building);
        other.End.Send(DeltaCodec.Order("chat.heal", ChatWire.Call([(other.Player.ToString(), other.Player)], other.Player.ToString())));
        rig.Step(2);
        Assert.True(rig.Sim.Num(building, "Health", "value") < rig.Sim.Num(building, "Health", "max"));
        Assert.Contains(other.Replies, reply => reply.StartsWith("Нет команды /heal"));
    }

    [Fact(DisplayName = "commands в mod.json: разбор и проверка")]
    public void ManifestCommandsAreParsedAndChecked()
    {
        ModPackage Load(string commands) => new("m", Encoding.UTF8.GetBytes("{ \"name\": \"m\", \"commands\": " + commands + " }"), new Dictionary<string, byte[]>());
        var parsed = Load("""[{ "name": "give", "args": ["<player>", "<amount>", "[note]"], "admin": true, "order": "chat.give", "choices": [null, ["1", "2"]] }]""").Commands;
        var give = Assert.Single(parsed);
        Assert.Equal(("m", "give", true, "chat.give"), (give.Mod, give.Name, give.Admin, give.Order));
        Assert.Equal(["<player>", "<amount>", "[note]"], give.Args);
        Assert.Equal(["1", "2"], give.Choices![1]);
        Assert.Empty(Load("[]").Commands);
        foreach (var bad in new[]
        {
            """{}""",
            """[{ "name": "Give", "order": "o" }]""",
            """[{ "name": "a" }]""",
            """[{ "name": "a", "order": "o", "args": ["plain"] }]""",
            """[{ "name": "a", "order": "o", "args": ["[x]", "<y>"] }]""",
            """[{ "name": "a", "order": "o", "args": ["<x>"], "choices": [null, null] }]""",
            """[{ "name": "a", "order": "o" }, { "name": "a", "order": "p" }]""",
            """[{ "name": "a", "order": "o", "color": 1 }]""",
        })
            Assert.Throws<ContentException>(() => Load(bad));
    }

}
