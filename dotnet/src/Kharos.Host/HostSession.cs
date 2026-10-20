using Kharos.Content;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Host;

/// <summary>Настройки рассылки, чата и входа.</summary>
public sealed class HostSessionOptions
{
    /// <summary>Сколько байт в секунду уходит одному подключению (sendRate, по умолчанию 256 КБ/с); 0 — без предела.</summary>
    public int SendRate { get; init; } = 256 * 1024;

    /// <summary>Какую долю тика хост может занять тиком и рассылкой вместе; что не успело — в следующих тиках.</summary>
    public double TickShare { get; init; } = 0.8;

    /// <summary>Пароль администратора (/admin); пустой — режима нет (кроме одиночной игры).</summary>
    public string? AdminPassword { get; init; }

    /// <summary>Одиночная игра: без вестей о входе, счёта сообщений и пароля администратора.</summary>
    public bool Local { get; init; }

    /// <summary>Подпись хоста в чате (/say, моды).</summary>
    public string ServerName { get; init; } = "[Сервер]";

    /// <summary>Сколько сообщений в секунду подключение шлёт в среднем и сколько разом (host.ts: MESSAGE_RATE, BURST); 0 — без счёта.</summary>
    public int MessageRate { get; init; } = 30;
    public int MessageBurst { get; init; } = 120;

    /// <summary>Сколько лишних сообщений прощается разом и в секунду; дальше подключение отключают.</summary>
    public int FloodBurst { get; init; } = 300;
    public int FloodRate { get; init; } = 1;

    /// <summary>
    /// Как часто подключение может писать в чат. Строка (и команда «/…») чаще этого молча отбрасывается, без ответа —
    /// как CHAT_GAP в host.ts: ответ на каждую лишнюю строку сам был бы потоком. Клиенты и боты, шлющие подряд,
    /// держат паузу сами (EditorShell.ChatGap, очередь чата в Kharos.Smoke).
    /// </summary>
    public TimeSpan ChatGap { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Часы сессии для паузы чата (<see cref="ChatGap"/>), счёта сообщений и запрета /admin после неверного пароля;
    /// по умолчанию — системные. Тесты подставляют свои и двигают время сами, не завися от загрузки машины.
    /// </summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Куда писать, что делают администраторы.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>Подключение к хосту: игрок, транспорт, его фильтр, что он знает о мире и счёт отправленного.</summary>
public sealed class Connection
{
    internal Connection(uint player, ITransport transport, IViewFilter filter, string token, int sendRate)
    {
        Player = player;
        Transport = transport;
        Filter = filter;
        Token = token;
        SendRate = sendRate;
    }

    public uint Player { get; }
    public ITransport Transport { get; }
    public IViewFilter Filter { get; }

    /// <summary>Токен игрока (ушёл в приветствии).</summary>
    public string Token { get; }

    /// <summary>Байт в секунду этому подключению; 0 — без предела.</summary>
    public int SendRate { get; set; }

    /// <summary>Экран клиента, тайлы; null — не присылал (тогда всё равноценно).</summary>
    public ViewBox? View { get; internal set; }

    /// <summary>В режиме администратора.</summary>
    public bool Admin { get; internal set; }

    /// <summary>В редакторе карты (/editor; только администратору): его приказы идут с правом Editor.</summary>
    public bool Editor { get; internal set; }

    /// <summary>Права приказов этого подключения: всегда Remote, плюс Admin и Editor по режимам.</summary>
    public OrderRights Rights => OrderRights.Remote | (Admin ? OrderRights.Admin : 0) | (Editor ? OrderRights.Editor : 0);

    /// <summary>Байт отправлено всего и последней дельтой.</summary>
    public long BytesSent { get; internal set; }
    public int LastDeltaBytes { get; internal set; }
    public long Messages { get; internal set; }

    /// <summary>Отброшенные сообщения (не разобраны или не по пределам).</summary>
    public int BadOrders { get; internal set; }

    /// <summary>Сколько сущностей знает и сколько изменённых ждёт очереди после последней рассылки.</summary>
    public int Known => Sync.KnownCount;
    public int Waiting => Sync.Waiting;

    /// <summary>Сколько байт пакетов модов ещё не ушло.</summary>
    public long Downloading => Downloads.Sum(item => (long)item.Pack.Packed.Length - item.Offset);

    /// <summary>Знает ли клиент сущность.</summary>
    public bool Knows(uint entity) => Sync.Knows(entity);

    internal PeerSync Sync { get; } = new();
    internal DeltaWriter Writer { get; } = new();
    internal Queue<(HostPack Pack, int Offset)> Downloads { get; } = new();
    internal double Allowance { get; set; }
    internal double Limit { get; set; }
    internal double LastChat { get; set; } = double.NegativeInfinity;
    internal double AdminLocked { get; set; }
    internal TokenBucket? MessageBucket { get; set; }
    internal TokenBucket? Strikes { get; set; }
}

/// <summary>Счёт по времени: count в секунду, burst разом.</summary>
internal sealed class TokenBucket(double rate, double burst)
{
    private readonly double _burst = burst;
    private double _tokens = burst, _at = double.NaN;

    public bool Take(double now)
    {
        if (!double.IsNaN(_at))
            _tokens = Math.Min(_burst, _tokens + (now - _at) * rate);
        _at = now;
        if (_tokens < 1)
            return false;
        _tokens--;
        return true;
    }
}

/// <summary>
/// Хост и его клиенты (host.ts): вход (приветствие, карта, снимок), сообщения клиентов (приказы, экран, чат и команды,
/// просьба начать заново, запросы модов), рассылка после тика — каждому своё, по обзору и с бюджетом
/// (<see cref="PeerSync"/>), доставка пакетов модов кусками. Одна и та же для одиночной игры (<see cref="LocalGame"/>,
/// транспорт в памяти) и сервера. Вызовы — из потока хоста.
/// </summary>
public sealed class HostSession
{
    /// <summary>Пределы приказа: имя и payload.</summary>
    public const int MaxOrderName = 64, MaxOrderPayload = 64 * 1024;

    /// <summary>Сколько сущностей подключение получает за заход круга рассылки.</summary>
    private const int Batch = 24;

    /// <summary>Очереди — не меньше этой доли тикового бюджета, сколько бы ни заняло обязательное.</summary>
    private const double QueueShare = 0.5;

    /// <summary>Сколько тиковых бюджетов копится про запас.</summary>
    private const double Burst = 4;

    /// <summary>Сколько мс на очередь есть всегда, даже если тик съел всё.</summary>
    private const double SendLeastMs = 4;

    /// <summary>Кусков пакетов за тик без предела байт.</summary>
    private const int ChunksUnlimited = 16;

    /// <summary>Сигнал мода: payload (UTF-8) — в чат от имени хоста.</summary>
    public const string SaySignal = "host.say";

    private readonly List<Connection> _connections = [];
    private readonly Dictionary<string, HostCommand> _commands = new(StringComparer.Ordinal);
    private readonly HashSet<uint> _hidden = [];
    private readonly List<(uint Player, Caller Caller, ulong Tick)> _awaiting = [];
    private string[]? _unitTypes;
    private readonly List<uint> _alive = [];
    private readonly Dictionary<uint, bool[]> _visible = [];
    private readonly long _started;
    private long _stepStarted;

    public HostSession(Host host, HostSessionOptions? options = null)
    {
        Host = host;
        Options = options ?? new HostSessionOptions();
        _started = Options.Time.GetTimestamp();
        Filter = player => new VisionView(Host, player);
        AddBuiltins();
        AddDeclared();
    }

    public Host Host { get; }
    public HostSessionOptions Options { get; }
    public IReadOnlyList<Connection> Connections => _connections;

    /// <summary>Фильтр зрителя по игроку: по умолчанию — обзор (<see cref="VisionView"/>).</summary>
    public Func<uint, IViewFilter> Filter { get; set; }

    /// <summary>Последняя разосланная дельта (для замеров и тестов).</summary>
    public WorldDelta? LastDelta { get; private set; }

    /// <summary>Сколько сверок отложено бюджетом в последней рассылке, по всем подключениям.</summary>
    public int Deferred { get; private set; }

    /// <summary>Команды чата по именам.</summary>
    public IReadOnlyDictionary<string, HostCommand> Commands => _commands;

    /// <summary>Знает ли хост игрока с этим токеном (для предела новых игроков).</summary>
    public bool Knows(string? token) => Host.Roster.Knows(token);

    /// <summary>
    /// Вход по токену (host.ts: join): знакомый — прежний игрок с его юнитами, иначе новый; name — ник (пустой —
    /// прежний или «Игрок N»). Токен уходит клиенту в приветствии.
    /// </summary>
    public Connection Join(ITransport transport, string? token, string? name = null)
    {
        Broadcast();
        var (record, _) = Host.Roster.Resolve(token, name);
        return Enter(transport, record);
    }

    /// <summary>
    /// Подключение игрока по номеру: накопленное уходит прежним клиентам, игрок входит (если ещё не входил), затем
    /// новому — приветствие, карта (если правлена) и снимок того, что он видит. Дальше он получает дельты с прочими.
    /// </summary>
    public Connection Connect(ITransport transport, uint player)
    {
        Broadcast();
        return Enter(transport, Host.Roster.Ensure(player));
    }

    /// <summary>Закрывает подключение (игрок выходит, если оно было последним).</summary>
    public void Disconnect(Connection connection)
    {
        if (!_connections.Contains(connection))
            return;
        connection.Transport.Dispose();
        Leave(connection);
    }

    /// <summary>Выгоняет игрока: отказ с причиной (готовый текст) всем его подключениям, затем они закрываются.</summary>
    public int Kick(uint player, string reason) => Kick(player, null, reason);

    /// <summary>Выгоняет игрока: отказ с причиной-ключом раздела host (клиент напишет её на своём языке).</summary>
    public int Kick(uint player, HostText reason) => Kick(player, reason, null);

    private int Kick(uint player, HostText? key, string? text)
    {
        var count = 0;
        foreach (var connection in _connections.Where(c => c.Player == player).ToList())
        {
            Send(connection, key is null ? Protocol.Messages.Refused(text!) : Protocol.Messages.Refused(key));
            Disconnect(connection);
            count++;
        }
        return count;
    }

    /// <summary>Тик: сообщения клиентов, шаг хоста, рассылка.</summary>
    public void Step()
    {
        _stepStarted = Stopwatch.GetTimestamp();
        PumpMessages();
        Host.Step();
        Broadcast();
        _stepStarted = 0;
    }

    /// <summary>То же, что <see cref="PumpMessages"/> (прежнее имя).</summary>
    public void PumpOrders() => PumpMessages();

    /// <summary>Сообщения клиентов: приказы — хосту (выполнятся в начале тика), прочее — сразу; закрытые — прочь.</summary>
    public void PumpMessages()
    {
        for (var at = _connections.Count - 1; at >= 0; at--)
        {
            if (at >= _connections.Count)
                continue;
            var connection = _connections[at];
            while (_connections.Contains(connection) && connection.Transport.TryReceive(out var message))
            {
                if (!Admit(connection))
                    continue;
                try
                {
                    Receive(connection, message);
                }
                catch (FormatException error)
                {
                    connection.BadOrders++;
                    Host.Report(null, $"игрок {connection.Player}", error.Message, error);
                }
            }
            if (_connections.Contains(connection) && connection.Transport.Closed)
                Leave(connection);
        }
    }

    /// <summary>
    /// Изменения с прошлой рассылки — каждому своё: обязательное сразу, остальное по очереди приоритета, пока есть байты
    /// бюджета и время тика; затем куски пакетов модов.
    /// </summary>
    public void Broadcast()
    {
        var started = _stepStarted != 0 ? _stepStarted : Stopwatch.GetTimestamp();
        var delta = Host.TakeDelta();
        LastDelta = delta;
        foreach (var signal in delta.Signals)
            if (signal.Name == SaySignal && PlayerRoster.CleanChat(Encoding.UTF8.GetString(signal.Payload)) is { Length: > 0 } text)
                Say(text);
            else if (signal.Name == ChatWire.ReplySignal)
                DeliverReply(signal);
            else if (signal.Name == ChatWire.AnnounceSignal)
                DeliverAnnounce(signal);
        _awaiting.RemoveAll(item => item.Tick < Host.Tick);
        Deferred = 0;
        if (_connections.Count == 0)
            return;
        var world = Host.World;
        var tick = Host.Tick;
        _alive.Clear();
        world.Entities(_alive);
        _visible.Clear();
        var tickBytes = new double[_connections.Count];
        foreach (var connection in _connections)
        {
            var w = connection.Writer;
            w.Begin(MessageType.Delta, tick, Host.Format);
            w.Strings(world.Strings, delta.StringsFrom);
            connection.Sync.Merge(world, delta, w);
            connection.Sync.View(world, connection.Filter, CollectionsMarshal.AsSpan(_alive), Visible(connection), tick, w);
            w.Layers(Host.LayersForPlayer(DeltaCodec.LayerChanges(delta.Layers, Host.Layers, connection.Filter), connection.Player, delta.LayersKnown.GetValueOrDefault(connection.Player) ?? []));
            w.Signals(delta.Signals);
            connection.Sync.Begin(world, connection.View, tick);
            if (connection.SendRate > 0)
            {
                var budget = connection.SendRate * Host.Dt;
                connection.Allowance = Math.Min(connection.Allowance + budget, budget * Burst);
                connection.Limit = Math.Max(connection.Allowance, w.Size + budget * QueueShare);
            }
            else
            {
                connection.Limit = double.PositiveInfinity;
            }
        }

        // Очереди — по кругу пачками, пока есть байты и время.
        var deadline = Math.Max(started + Host.Dt * Options.TickShare * Stopwatch.Frequency, Stopwatch.GetTimestamp() + SendLeastMs / 1000 * Stopwatch.Frequency);
        var waiting = _connections.Where(c => c.Sync.Queued > 0 && c.Writer.Size < c.Limit).ToList();
        while (waiting.Count > 0)
        {
            foreach (var connection in waiting)
                for (var taken = 0; taken < Batch && connection.Sync.Queued > 0 && connection.Writer.Size < connection.Limit; taken++)
                    connection.Sync.Take(world, 1, tick, connection.Writer);
            waiting.RemoveAll(c => c.Sync.Queued == 0 || c.Writer.Size >= c.Limit);
            if (Stopwatch.GetTimestamp() > deadline)
                break;
        }

        foreach (var connection in _connections.ToList())
        {
            Deferred += connection.Sync.Queued;
            var bytes = connection.Writer.Finish();
            connection.LastDeltaBytes = bytes.Length;
            Send(connection, bytes);
            if (delta.LandFor(connection.Player) is { Count: > 0 } landEdits)
                Send(connection, Protocol.Messages.LandEdits(landEdits));
            SendChunks(connection);
        }
    }

    // ------------------------------------------------------------------ чат и команды

    /// <summary>Пишет всем в чат от имени хоста (/say, консоль, моды).</summary>
    public void Say(string text) => Chat([new ChatLine(0, Options.ServerName, text, ChatKind.System)]);

    /// <summary>Пишет всем сообщение хоста ключом: каждый читает его на своём языке.</summary>
    public void Say(HostText text) => Chat([KeyLine(0, Options.ServerName, text)]);

    /// <summary>Сообщение хоста только игроку (всем его подключениям).</summary>
    public void Tell(uint player, string text) => Tell(player, new ChatLine(0, "", text, ChatKind.System));

    /// <summary>Сообщение хоста ключом только игроку: он прочтёт его на своём языке.</summary>
    public void Tell(uint player, HostText text) => Tell(player, KeyLine(0, "", text));

    /// <summary>Весть всем о игроке ключом (поражение, ядерный пуск…): «ник текст» на языке каждого.</summary>
    public void Announce(uint player, HostText text) => Chat([KeyLine(player, Host.Roster.NameOf(player), text)]);

    private void Tell(uint player, ChatLine line)
    {
        var message = Protocol.Messages.Chat([line]);
        foreach (var connection in _connections)
            if (connection.Player == player)
                Send(connection, message);
    }

    private static ChatLine KeyLine(uint player, string name, HostText text) => new(player, name, text.Key, ChatKind.System, null, text.Args);

    /// <summary>Выполняет команду (с «/» или без) от имени caller.</summary>
    public void Command(string text, Caller caller)
    {
        if (ChatCommands.Parse(text.StartsWith('/') ? text : "/" + text) is not { Name.Length: > 0 } parsed)
            return;
        var fromGame = caller.Player is not null;
        if (!_commands.TryGetValue(parsed.Name, out var command) || (command.Admin && !caller.Admin) || (fromGame ? command.Console : command.Game))
        {
            // Команды модов: приказ command.<имя>, payload — admin:u8 и текст после имени (UTF-8).
            var order = "command." + parsed.Name;
            if (Host.HasOrder(order))
            {
                var rest = Encoding.UTF8.GetBytes(parsed.Rest(0));
                var rights = caller.Connection?.Rights ?? (caller.Admin ? OrderRights.Admin : OrderRights.None);
                Host.Send(caller.Player ?? 0, order, [caller.Admin ? (byte)1 : (byte)0, .. rest], rights);
                return;
            }
            caller.Reply("noCommand", ("name", parsed.Name));
            return;
        }
        command.Run(new CommandCall(parsed.Name, parsed.Args, parsed.Rest, caller));
    }

    /// <summary>Добавляет команду (сервер: сохранение, остановка) или заменяет одноимённую.</summary>
    public void AddCommand(HostCommand command)
    {
        _commands[command.Name] = command;
        foreach (var connection in _connections)
            SendCommands(connection);
    }

    // ------------------------------------------------------------------ команды модов (Н6-7)

    /// <summary>Виды юнитов партии (ключи data/units.json) — для аргумента «unit» и его дополнения.</summary>
    private string[] UnitTypes()
    {
        if (_unitTypes is not null)
            return _unitTypes;
        try
        {
            if (Host.Content.DataBytes("units") is { } bytes)
            {
                using var document = System.Text.Json.JsonDocument.Parse(bytes);
                return _unitTypes = [.. document.RootElement.EnumerateObject().Select(property => property.Name)];
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return _unitTypes = [];
    }

    /// <summary>Команды из mod.json (commands): встроенные хоста главнее одноимённых, у модов позднейший главнее.</summary>
    private void AddDeclared()
    {
        var builtin = new HashSet<string>(_commands.Keys, StringComparer.Ordinal);
        foreach (var package in Host.Content.Mods)
            foreach (var declaration in package.Commands)
            {
                if (builtin.Contains(declaration.Name))
                    continue;
                var declared = declaration;
                var choices = new IReadOnlyList<string>?[declared.Args.Count];
                var any = false;
                for (var i = 0; i < choices.Length; i++)
                {
                    choices[i] = declared.Choices is { } given && i < given.Count ? given[i] : null;
                    if (choices[i] is null && ArgKind(declared.Args[i]) == ArgKinds.Unit)
                        choices[i] = UnitTypes();
                    any |= choices[i] is not null;
                }
                _commands[declared.Name] = new HostCommand
                {
                    Name = declared.Name, Args = declared.Args, Help = declared.Help, Admin = declared.Admin, Game = declared.Game,
                    Choices = any ? choices : null,
                    Run = call => Dispatch(declared, call),
                };
            }
    }

    private enum ArgKinds { Word, Player, PlayerOrAll, Unit, Text }

    private static ArgKinds ArgKind(string spec) => spec[1..^1].ToLowerInvariant() switch
    {
        "player" => ArgKinds.Player,
        "player|all" or "all|player" => ArgKinds.PlayerOrAll,
        "unit" => ArgKinds.Unit,
        "text" or "action" => ArgKinds.Text,
        _ => ArgKinds.Word,
    };

    /// <summary>
    /// Набранная команда мода → приказ мода с ChatCall: слова и номера найденных игроков. Нет обязательного слова,
    /// игрока или вида юнита — ответ автору, приказа нет.
    /// </summary>
    private void Dispatch(ChatCommandDeclaration declaration, CommandCall call)
    {
        var caller = call.Caller;
        var usage = $"/{string.Join(' ', [declaration.Name, .. declaration.Args])}";
        var words = new List<(string, uint)>();
        for (var i = 0; i < declaration.Args.Count; i++)
        {
            var spec = declaration.Args[i];
            if (i >= call.Args.Count)
            {
                if (spec[0] == '<')
                {
                    caller.Reply("usage", ("command", usage));
                    return;
                }
                break;
            }
            var word = call.Args[i];
            uint player = 0;
            switch (ArgKind(spec))
            {
                case ArgKinds.Text:
                    words.Add((call.Rest(i), 0));
                    i = declaration.Args.Count;
                    continue;
                case ArgKinds.PlayerOrAll when string.Equals(word, "all", StringComparison.OrdinalIgnoreCase):
                    word = "all";
                    break;
                case ArgKinds.Player or ArgKinds.PlayerOrAll:
                    if (Host.Roster.FindPlayer(word) is not { } who || (_hidden.Contains(who) && !caller.Admin))
                    {
                        caller.Reply("noPlayer", ("name", word));
                        return;
                    }
                    player = who;
                    break;
                case ArgKinds.Unit:
                    word = word.ToLowerInvariant();
                    if (Array.IndexOf(UnitTypes(), word) < 0)
                    {
                        caller.Reply("unitTypes", ("list", string.Join(", ", UnitTypes())));
                        return;
                    }
                    break;
            }
            words.Add((word, player));
        }
        if (!Host.HasOrder(declaration.Order))
        {
            caller.Reply("noCommand", ("name", declaration.Name));
            return;
        }
        var rights = caller.Connection?.Rights ?? (caller.Admin ? OrderRights.Admin : OrderRights.None);
        var author = caller.Player ?? 0;
        if (caller.Connection is null)
            _awaiting.Add((author, caller, Host.Tick));
        Host.Send(author, declaration.Order, ChatWire.Call(words, call.Rest(0)), rights);
    }

    /// <summary>Ответ мода автору: консоли и тестам — в их ответ, игроку — сообщением хоста только ему.</summary>
    private void DeliverReply(Signal signal)
    {
        if (signal.To is not [var author, ..] || ChatWire.ReadText(signal.Payload) is not { } text)
            return;
        text = Named(text);
        var waiting = _awaiting.Where(item => item.Player == author).Select(item => item.Caller).Distinct().ToList();
        if (waiting.Count > 0)
            foreach (var caller in waiting)
                caller.Reply(text);
        else if (author != 0)
            Tell(author, text);
    }

    /// <summary>Подстановка «player» (номер) в сообщении мода получает рядом «name» — ник по списку игроков: мод ников не знает.</summary>
    private HostText Named(HostText text)
    {
        var args = text.Named();
        if (args.ContainsKey("name") || !args.TryGetValue("player", out var number) || !uint.TryParse(number, out var player))
            return text;
        return new HostText(text.Key, [.. text.Args, "name", Host.Roster.NameOf(player)]);
    }

    /// <summary>Весть мода об игроке всем (поражение, ядерный пуск): скрытых администраторов не объявляют.</summary>
    private void DeliverAnnounce(Signal signal)
    {
        if (ChatWire.ReadAnnounce(signal.Payload) is not { } item || (item.Flags & ChatWire.RemoteOnly) != 0 && Options.Local || _hidden.Contains(item.Player))
            return;
        Announce(item.Player, Named(item.Text));
    }

    // ------------------------------------------------------------------ вход и выход

    private Connection Enter(ITransport transport, PlayerRecord record)
    {
        var player = record.Id;
        // Что вход сделал с миром (стартовые юниты, память модов), прежние клиенты получают до снимка нового.
        var joined = Host.Join(player);
        var online = Host.Online(player);
        if (joined || online)
            Broadcast();
        var connection = new Connection(player, transport, Filter(player), record.Token, Options.SendRate)
        {
            MessageBucket = Options.Local || Options.MessageRate <= 0 ? null : new TokenBucket(Options.MessageRate, Options.MessageBurst),
            Strikes = Options.Local || Options.MessageRate <= 0 ? null : new TokenBucket(Options.FloodRate, Options.FloodBurst),
        };
        _connections.Add(connection);
        Send(connection, Host.Welcome(player).Encode());
        if (Host.LandForJoin() is { } land)
            Send(connection, Protocol.Messages.Land(land));
        if (Host.KnownLand(player) is { Count: > 0 } knownLand)
            Send(connection, Protocol.Messages.LandEdits(knownLand));
        var w = connection.Writer;
        w.Begin(MessageType.Snapshot, Host.Tick, Host.Format);
        w.Strings(Host.World.Strings, 1);
        _alive.Clear();
        Host.World.Entities(_alive);
        _visible.Clear();
        connection.Sync.View(Host.World, connection.Filter, CollectionsMarshal.AsSpan(_alive), Visible(connection), Host.Tick, w);
        w.Layers(Host.LayersForPlayer(DeltaCodec.LayerBlocks(Host.Layers, connection.Filter), player));
        var snapshot = w.Finish();
        Send(connection, snapshot);
        SendCommands(connection);
        if (online && !Options.Local && !_hidden.Contains(player))
            Notice(player, "joins");
        AnnouncePlayers();
        return connection;
    }

    private void Leave(Connection connection)
    {
        if (!_connections.Remove(connection))
            return;
        var player = connection.Player;
        if (_connections.Any(c => c.Player == player))
            return;
        Host.Offline(player);
        if (!Options.Local && !_hidden.Contains(player))
            Notice(player, "leaves");
        AnnouncePlayers();
    }

    /// <summary>Видимость сущностей мира игроку этого подключения — одна на все его подключения за рассылку.</summary>
    private bool[] Visible(Connection connection)
    {
        if (_visible.TryGetValue(connection.Player, out var visible) && visible.Length == _alive.Count)
            return visible;
        visible = new bool[_alive.Count];
        for (var i = 0; i < _alive.Count; i++)
            visible[i] = connection.Filter.Entity(_alive[i]);
        _visible[connection.Player] = visible;
        return visible;
    }

    // ------------------------------------------------------------------ сообщения клиента

    /// <summary>Секунды с начала сессии по её часам (<see cref="HostSessionOptions.Time"/>).</summary>
    private double Now => Options.Time.GetElapsedTime(_started).TotalSeconds;

    /// <summary>Счёт сообщений: сверх — отбрасывается, а кто не унимается — отключается.</summary>
    private bool Admit(Connection connection)
    {
        if (connection.MessageBucket is not { } messages || connection.Admin)
            return true;
        var now = Now;
        if (messages.Take(now))
            return true;
        if (connection.Strikes!.Take(now))
            return false;
        Options.Log?.Invoke($"! {Host.Roster.NameOf(connection.Player)} отключён: слишком много сообщений");
        Send(connection, Protocol.Messages.Refused(HostText.Of("flood")));
        Disconnect(connection);
        return false;
    }

    private void Receive(Connection connection, byte[] message)
    {
        switch (Protocol.Messages.TypeOf(message))
        {
            case MessageType.Order:
            {
                var (name, payload) = DeltaCodec.ReadOrder(message);
                if (name.Length == 0 || name.Length > MaxOrderName || payload.Length > MaxOrderPayload)
                    throw new FormatException($"приказ {name}: не по пределам");
                Host.Send(connection.Player, name, payload, connection.Rights);
                break;
            }
            case MessageType.View:
            {
                var box = Protocol.Messages.ReadView(message);
                connection.View = box.Right >= box.Left && box.Bottom >= box.Top ? box : throw new FormatException("экран наизнанку");
                break;
            }
            case MessageType.Say:
                Chat(connection, Protocol.Messages.ReadSay(message));
                break;
            case MessageType.Respawn:
                if (message.Length != 1)
                    throw new FormatException("лишние байты в просьбе начать заново");
                Host.Respawn(connection.Player);
                break;
            case MessageType.Fetch:
                foreach (var hash in Protocol.Messages.ReadFetch(message))
                {
                    var pack = Host.Packs.FirstOrDefault(p => p.Ref.Hash.AsSpan().SequenceEqual(hash));
                    if (pack is not null && !connection.Downloads.Any(item => item.Pack == pack))
                        connection.Downloads.Enqueue((pack, 0));
                }
                break;
            default:
                throw new FormatException($"сообщение 0x{message[0]:x2} хосту не шлют");
        }
    }

    private void Chat(Connection connection, string text)
    {
        var clean = PlayerRoster.CleanChat(text);
        var now = Now;
        if (clean.Length == 0 || now - connection.LastChat < Options.ChatGap.TotalSeconds)
            return;
        connection.LastChat = now;
        // Команда никому не уходит: пароль администратора не должен попасть в чат.
        if (clean.StartsWith('/'))
        {
            Command(clean, new Caller(connection.Player, connection.Admin, (HostText reply) => Send(connection, Protocol.Messages.Chat([KeyLine(0, "", reply)])), connection));
            return;
        }
        Chat([new ChatLine(connection.Player, Host.Roster.NameOf(connection.Player), clean)]);
    }

    // ------------------------------------------------------------------ отправка

    private void Chat(IReadOnlyList<ChatLine> lines)
    {
        var message = Protocol.Messages.Chat(lines);
        foreach (var connection in _connections)
            Send(connection, message);
    }

    /// <summary>Весть всем о игроке: «ник текст», текст — ключ раздела host (joins, leaves, kicked…).</summary>
    private void Notice(uint player, string key) => Announce(player, HostText.Of(key));

    /// <summary>Кто играет — каждому своими глазами: скрытых он не видит, кроме себя.</summary>
    private void AnnouncePlayers()
    {
        var online = new HashSet<uint>(_connections.Select(c => c.Player));
        foreach (var connection in _connections)
        {
            var list = Host.Roster.All.Where(record => !_hidden.Contains(record.Id) || record.Id == connection.Player)
                .Select(record => new PlayerInfo(record.Id, record.Name, online.Contains(record.Id))).ToList();
            Send(connection, Protocol.Messages.Players(list));
        }
    }

    private void SendCommands(Connection connection)
    {
        var list = _commands.Values.Where(c => !c.Console && (connection.Admin || !c.Admin)).Select(c => c.Info).ToList();
        Send(connection, Protocol.Messages.Commands(list, connection.Admin, connection.Editor));
    }

    /// <summary>Куски пакетов модов: пока есть бюджет (без предела — до 16 за тик).</summary>
    private void SendChunks(Connection connection)
    {
        var sent = 0;
        while (connection.Downloads.Count > 0 && (connection.SendRate > 0 ? connection.Allowance > 0 : sent < ChunksUnlimited))
        {
            var (pack, offset) = connection.Downloads.Peek();
            var length = Math.Min(Protocol.Messages.MaxChunk, pack.Packed.Length - offset);
            var message = Protocol.Messages.Chunk(new ContentChunk(pack.Ref.Hash, pack.Packed.Length, offset, pack.Packed.AsSpan(offset, length).ToArray()));
            Send(connection, message);
            sent++;
            connection.Downloads.Dequeue();
            if (offset + length < pack.Packed.Length)
            {
                // В голову очереди: пакет идёт до конца, прежде чем следующий.
                var rest = connection.Downloads.ToList();
                connection.Downloads.Clear();
                connection.Downloads.Enqueue((pack, offset + length));
                foreach (var item in rest)
                    connection.Downloads.Enqueue(item);
            }
        }
    }

    private static void Send(Connection connection, byte[] message)
    {
        connection.Transport.Send(message);
        connection.BytesSent += message.Length;
        if (connection.SendRate > 0)
            connection.Allowance -= message.Length;
        connection.Messages++;
    }

    // ------------------------------------------------------------------ команды хоста

    private void AddBuiltins()
    {
        HostCommand Add(HostCommand command) => _commands[command.Name] = command;
        string Name(uint player) => Host.Roster.NameOf(player);
        bool Online(uint player) => _connections.Any(c => c.Player == player);
        uint? Target(CommandCall call, int index)
        {
            var who = index < call.Args.Count ? Host.Roster.FindPlayer(string.Join(' ', call.Args.Skip(index))) : call.Caller.Player;
            if (who is null)
                call.Caller.Reply(index < call.Args.Count ? "noPlayer" : "needPlayer", ("name", string.Join(' ', call.Args.Skip(index))));
            return who;
        }

        Add(new HostCommand
        {
            Name = "help", Help = "команды",
            Run = call =>
            {
                foreach (var command in _commands.Values)
                {
                    if (command.Admin && !call.Caller.Admin)
                        continue;
                    if (call.Caller.Player is null ? command.Game : command.Console)
                        continue;
                    call.Caller.Reply("help", ("command", command.Usage), ("name", command.Name), ("help", command.Help));
                }
            },
        });
        Add(new HostCommand
        {
            Name = "list", Help = "кто в игре",
            Run = call =>
            {
                var present = _connections.Select(c => c.Player).Distinct().ToList();
                var here = present.Where(p => !_hidden.Contains(p)).Select(Name).ToList();
                if (here.Count > 0)
                    call.Caller.Reply("list", ("count", here.Count), ("names", string.Join(", ", here)));
                else
                    call.Caller.Reply("listEmpty");
                var unseen = present.Where(_hidden.Contains).Select(Name).ToList();
                if (call.Caller.Admin && unseen.Count > 0)
                    call.Caller.Reply("listHidden", ("names", string.Join(", ", unseen)));
            },
        });
        Add(new HostCommand
        {
            Name = "me", Args = ["<action>"], Help = "действие от третьего лица", Game = true,
            Run = call =>
            {
                if (call.Rest(0) is { Length: > 0 } text)
                    Chat([new ChatLine(call.Caller.Player!.Value, $"* {Name(call.Caller.Player!.Value)}", text, ChatKind.System)]);
            },
        });
        Add(new HostCommand
        {
            Name = "msg", Args = [ChatCommands.PlayerArg, "<text>"], Help = "шёпот игроку", Game = true,
            Run = call =>
            {
                var from = call.Caller.Player!.Value;
                var to = Host.Roster.FindPlayer(call.Arg(0));
                if (to is null || (_hidden.Contains(to.Value) && !call.Caller.Admin))
                {
                    call.Caller.Reply("noPlayer", ("name", call.Arg(0)));
                    return;
                }
                var text = call.Rest(1);
                if (text.Length == 0)
                {
                    call.Caller.Reply("usage", ("command", $"/{call.Name} {ChatCommands.PlayerArg} <text>"));
                    return;
                }
                var message = Protocol.Messages.Chat([new ChatLine(from, Name(from), text, ChatKind.Whisper, Name(to.Value))]);
                foreach (var connection in _connections)
                    if (connection.Player == to || connection.Player == from)
                        Send(connection, message);
                if (!Online(to.Value))
                    call.Caller.Reply("notOnline", ("name", Name(to.Value)));
            },
        });
        Add(new HostCommand
        {
            Name = "admin", Args = ["<password>"], Help = "режим администратора", Game = true, Secret = true,
            Run = call =>
            {
                var connection = call.Caller.Connection!;
                var local = Options.Local;
                if (!local && string.IsNullOrEmpty(Options.AdminPassword))
                {
                    call.Caller.Reply("noAdminMode");
                    return;
                }
                if (connection.Admin)
                {
                    call.Caller.Reply("alreadyAdmin");
                    return;
                }
                var now = Now;
                if (now < connection.AdminLocked)
                {
                    call.Caller.Reply("wait");
                    return;
                }
                if (!local && !Same(string.Join(' ', call.Args), Options.AdminPassword!))
                {
                    connection.AdminLocked = now + 3;
                    Options.Log?.Invoke($"! неверный пароль администратора от {Name(connection.Player)}");
                    call.Caller.Reply("wrongPassword");
                    return;
                }
                connection.Admin = true;
                SendCommands(connection);
                Options.Log?.Invoke($"! {Name(connection.Player)} — администратор");
                call.Caller.Reply("adminOn");
            },
        });
        Add(new HostCommand
        {
            Name = "logout", Help = "выйти из режима администратора", Admin = true, Game = true,
            Run = call =>
            {
                var connection = call.Caller.Connection!;
                connection.Admin = false;
                connection.Editor = false;
                SendCommands(connection);
                call.Caller.Reply("adminOff");
            },
        });
        Add(new HostCommand
        {
            Name = "editor", Help = "редактор карты: приказы edit.* (повтор — выйти)", Admin = true, Game = true,
            Run = call =>
            {
                var connection = call.Caller.Connection!;
                connection.Editor = !connection.Editor;
                // Право редактора — клиенту (Commands, инструмент Settings): оболочка включает режим редактора.
                SendCommands(connection);
                Options.Log?.Invoke($"! {Name(connection.Player)} {(connection.Editor ? "открыл" : "закрыл")} редактор");
                call.Caller.Reply(connection.Editor ? "editorOn" : "editorOff");
            },
        });
        Add(new HostCommand
        {
            Name = "hide", Help = "скрыться от игроков или показаться", Admin = true, Game = true,
            Run = call =>
            {
                var who = call.Caller.Player!.Value;
                if (_hidden.Remove(who))
                {
                    Notice(who, "joins");
                    call.Caller.Reply("shown");
                }
                else
                {
                    Notice(who, "leaves");
                    _hidden.Add(who);
                    call.Caller.Reply("hidden");
                }
                AnnouncePlayers();
            },
        });
        Add(new HostCommand
        {
            Name = "say", Args = ["<text>"], Help = "сообщение от имени сервера", Admin = true,
            Run = call =>
            {
                var text = PlayerRoster.CleanChat(call.Rest(0));
                if (text.Length == 0)
                {
                    call.Caller.Reply("usage", ("command", "/say <text>"));
                    return;
                }
                Say(text);
                Options.Log?.Invoke($"{Options.ServerName} {text}");
            },
        });
        Add(new HostCommand
        {
            Name = "kick", Args = [ChatCommands.PlayerArg], Help = "отключить игрока", Admin = true,
            Run = call =>
            {
                if (Host.Roster.FindPlayer(string.Join(' ', call.Args)) is not { } who)
                {
                    call.Caller.Reply("noPlayer", ("name", string.Join(' ', call.Args)));
                    return;
                }
                if (Kick(who, HostText.Of("kickedYou")) == 0)
                {
                    call.Caller.Reply("notOnline", ("name", Name(who)));
                    return;
                }
                Notice(who, "kicked");
                Options.Log?.Invoke($"! {Name(who)} отключён");
            },
        });
        Add(new HostCommand
        {
            Name = "players", Help = "все игроки партии", Admin = true,
            Run = call =>
            {
                var owners = Host.World.Store(Builtin.Owner);
                var counts = new Dictionary<uint, int>();
                var column = owners.Column(0);
                for (var row = 0; row < column.Length; row++)
                    if (column[row] >= 1)
                        counts[(uint)column[row]] = counts.GetValueOrDefault((uint)column[row]) + 1;
                foreach (var record in Host.Roster.All)
                    call.Caller.Reply(Online(record.Id) ? "playerOnlineEntities" : "playerOfflineEntities", ("number", record.Id), ("name", record.Name), ("count", counts.GetValueOrDefault(record.Id)));
            },
        });
        Add(new HostCommand
        {
            Name = "reset", Args = [ChatCommands.PlayerArg], Help = "игрок начинает заново (событие player_respawn)", Admin = true,
            Run = call =>
            {
                if (Target(call, 0) is not { } who)
                    return;
                Host.Respawn(who, forced: true);
                call.Caller.Reply("restarted", ("name", Name(who)));
                Options.Log?.Invoke($"! reset {Name(who)}");
            },
        });
        Add(new HostCommand
        {
            Name = "reveal", Args = [ChatCommands.PlayerArg], Help = "открыть игроку всю карту", Admin = true,
            Run = call =>
            {
                if (Target(call, 0) is not { } who)
                    return;
                if (Host.Vision is not { } vision)
                {
                    call.Caller.Reply("noFog");
                    return;
                }
                vision.Reveal(who);
                call.Caller.Reply("revealed", ("name", Name(who)));
            },
        });
    }

    /// <summary>Сравнение паролей за одно и то же время.</summary>
    private static bool Same(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(a)),
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
