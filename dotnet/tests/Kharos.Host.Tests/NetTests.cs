using Kharos.Engine.Terrain;
using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Host.Tests;

/// <summary>Сеть Н4: интерес по обзору, бюджет отправки, квантование, игроки и токены, сохранение, чат и команды, доставка модов.</summary>
public class NetTests
{
    private static Kharos.Plugins.Game.GameModuleOptions Modules => new() { CallTimeoutMs = 1000 };

    /// <summary>Клиент на конце транспорта: реплика и всё прочее, что пришло.</summary>
    private sealed class Client
    {
        public Client(HostSession session, uint? player = null, string? token = null, string? name = null)
        {
            (var hostEnd, End) = MemoryTransport.Pair();
            Connection = player is { } id ? session.Connect(hostEnd, id) : session.Join(hostEnd, token, name);
            Pump();
        }

        public MemoryTransport End { get; }
        public Connection Connection { get; }
        public Replica Replica { get; } = new();
        public List<ChatLine> Chat { get; } = [];
        public List<string> Refused { get; } = [];
        public bool Admin { get; private set; }
        public List<ContentChunk> Chunks { get; } = [];

        public void Pump()
        {
            while (End.TryReceive(out var message))
            {
                switch ((MessageType)message[0])
                {
                    case MessageType.Chunk:
                        Chunks.Add(Messages.ReadChunk(message));
                        continue;
                    case MessageType.Chat:
                        Chat.AddRange(Messages.ReadChat(message));
                        break;
                    case MessageType.Refused:
                        Refused.Add(Messages.ReadRefused(message).Reason);
                        break;
                    case MessageType.Commands:
                        Admin = Messages.ReadCommands(message).Admin;
                        break;
                }
                Replica.Apply(message);
            }
            Replica.Inbox.Clear();
        }

        public List<uint> Units(uint player) => HelloGame.Units(Replica.World!, player);
    }

    private static void Step(HostSession session, params Client[] clients)
    {
        session.Step();
        foreach (var client in clients)
            client.Pump();
    }

    [Fact]
    public void ForeignUnitsAppearOnlyWhileSeen()
    {
        using var host = HelloGame.Create();
        host.Start();
        var session = new HostSession(host, new HostSessionOptions { SendRate = 0 });
        var one = new Client(session, 1);
        var two = new Client(session, 2);
        Assert.Equal(3, one.Units(1).Count);
        Assert.Empty(one.Units(2));
        Assert.Empty(two.Units(1));

        // Доехавший юнит hello уезжает в случайную точку: приказ — каждый тик. Первый стоит дома (0, 4), второй едет к
        // нему, потом прочь.
        var ones = HelloGame.Units(host.World, 1);
        var units = HelloGame.Units(host.World, 2);
        void Drive(double x)
        {
            host.Send(1, "hello.move", HelloGame.Move(0, 4, [.. ones]));
            host.Send(2, "hello.move", HelloGame.Move(x, 4, [.. units]));
            Step(session, one, two);
            NetAssert.Same(host, one.Replica, one.Connection.Filter);
            NetAssert.Same(host, two.Replica, two.Connection.Filter);
        }
        for (var tick = 0; tick < 300 && one.Units(2).Count < 3; tick++)
            Drive(3);
        Assert.Equal(3, one.Units(2).Count);
        Assert.Equal(3, two.Units(1).Count);
        for (var tick = 0; tick < 400 && one.Units(2).Count + two.Units(1).Count > 0; tick++)
            Drive(80);
        Assert.Empty(one.Units(2));
        Assert.Empty(two.Units(1));
        // Вернулись — снова видны: реплика оживляет прежние номера.
        for (var tick = 0; tick < 400 && one.Units(2).Count < 3; tick++)
            Drive(3);
        Assert.Equal(units, one.Units(2));
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void TinyBudgetServesScreenFirstAndConverges()
    {
        using var host = HelloGame.Create();
        host.Start();
        // 4 КБ/с — двести байт на тик: десятки юнитов из трёх сотен.
        var session = new HostSession(host, new HostSessionOptions { SendRate = 4096 });
        var client = new Client(session, 1);
        host.Send(1, "hello.spawn", HelloGame.Spawn(150, -40, 0, spread: 12));
        host.Send(1, "hello.spawn", HelloGame.Spawn(150, 40, 0, spread: 12));
        Step(session, client);
        Assert.Equal(303, client.Units(1).Count);
        // Экран — над левой группой.
        client.End.Send(Messages.View(new ViewBox(-60, -45, -20, 15)));

        var left = HelloGame.Units(host.World, 1).Where(e => HelloGame.Position(host.World, e).X < -20).ToArray();
        var right = HelloGame.Units(host.World, 1).Where(e => HelloGame.Position(host.World, e).X > 20).ToArray();
        host.Send(1, "hello.move", HelloGame.Move(-40, -30, left));
        host.Send(1, "hello.move", HelloGame.Move(40, -30, right));
        var spans = 0;
        for (var tick = 0; tick < 40; tick++)
        {
            Step(session, client);
            spans += client.Replica.Moves.Count(move => move.Span > 1);
            Assert.True(client.Connection.LastDeltaBytes < 4096, $"дельта {client.Connection.LastDeltaBytes} байт при бюджете 205 байт на тик");
        }
        double Error(uint[] group) => group.Average(e =>
        {
            var (hx, hy) = HelloGame.Position(host.World, e);
            var (rx, ry) = HelloGame.Position(client.Replica.World!, e);
            return Math.Sqrt((hx - rx) * (hx - rx) + (hy - ry) * (hy - ry));
        });
        var (onScreen, offScreen) = (Error(left), Error(right));
        Assert.True(onScreen < offScreen, $"на экране отстают на {onScreen:0.00}, за экраном — на {offScreen:0.00}");
        Assert.True(spans > 0, "отложенные приходят со сроком сглаживания");
        Assert.True(session.Deferred > 0);

        // Мир замер (хост не тикает, рассылка идёт): бюджета хватает догнать всех.
        for (var tick = 0; tick < 2000 && client.Connection.Waiting > 0; tick++)
        {
            session.Broadcast();
            client.Pump();
        }
        Assert.Equal(0, client.Connection.Waiting);
        NetAssert.Same(host, client.Replica, client.Connection.Filter);
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void QuantisationStaysWithinHalfStep()
    {
        var schema = new Schema();
        schema.DeclareNumber("Drive", "speed", 0);
        var format = NetFormat.Of(schema, overrides: new Dictionary<string, double> { ["Drive.speed"] = 0 });
        var random = new Random(5);
        var w = new WireWriter();
        var values = new List<(int Component, int Field, double Value)>();
        for (var k = 0; k < 2000; k++)
        {
            values.Add((Builtin.Position, k % 2, (random.NextDouble() - 0.5) * 4096));
            values.Add((Builtin.Facing, 0, (random.NextDouble() - 0.5) * 20));
            values.Add((Builtin.Owner, 0, random.Next(0, 1 << 30)));
            values.Add((Builtin.Proto, 0, (random.NextDouble() - 0.5) * 1e6));
        }
        values.AddRange([(Builtin.Proto, 0, double.NaN), (Builtin.Proto, 0, double.PositiveInfinity), (Builtin.Proto, 0, 1e300), (Builtin.Position, 0, -0.0), (schema.Id("Drive"), 0, Math.PI)]);
        foreach (var (c, f, v) in values)
            format.Write(w, c, f, v);
        var r = new WireReader(w.Written);
        foreach (var (c, f, v) in values)
        {
            var back = format.Read(ref r, c, f);
            Assert.Equal(format.Quantize(c, f, v), back);
            if (double.IsFinite(v))
                Assert.True(Math.Abs(back - v) <= format.Error(c, f) * (1 + 1e-12), $"{c}.{f}: {v} → {back}");
            else
                Assert.Equal(v, back);
        }
        Assert.True(r.End);
        Assert.Equal(0.5 / 256, format.Error(Builtin.Position, 0));
        Assert.Equal(0.5 / 10000, format.Error(Builtin.Facing, 0));
        Assert.Equal(0.5 / 1000, format.Error(Builtin.Owner, 0));
        Assert.Equal(0, format.Error(schema.Id("Drive"), 0));
        // Целые проходят точно: номера сущностей и игроков в числовых полях.
        Assert.Equal(123456789.0, format.Quantize(Builtin.Owner, 0, 123456789));
        // Место в карте 512² — три байта на ось вместо восьми.
        var place = new WireWriter();
        format.Write(place, Builtin.Position, 0, -255.123);
        Assert.Equal(3, place.Length);
    }

    [Fact]
    public void TokenBringsPlayerBackWithHisUnits()
    {
        using var host = HelloGame.Create();
        host.Start();
        var session = new HostSession(host, new HostSessionOptions { SendRate = 0 });
        var alice = new Client(session, name: "Алиса");
        var id = alice.Connection.Player;
        var token = alice.Replica.Welcome!.Token;
        Assert.Equal(alice.Connection.Token, token);
        Assert.Equal("Алиса", alice.Replica.Welcome.Name);
        Assert.Equal(3, HelloGame.Units(host.World, id).Count);
        Assert.Contains(id, host.OnlinePlayers);

        session.Disconnect(alice.Connection);
        Assert.DoesNotContain(id, host.OnlinePlayers);
        for (var tick = 0; tick < 5; tick++)
            session.Step();
        var back = new Client(session, token: token);
        Assert.Equal(id, back.Connection.Player);
        Assert.Equal("Алиса", host.Roster.NameOf(id));
        Assert.Equal(3, HelloGame.Units(host.World, id).Count);
        Assert.Equal(3, back.Units(id).Count);

        // Свой токен клиента — новый игрок с этим токеном; пустой — новый с выданным хостом.
        var bob = new Client(session, token: "client-made-token-1", name: "  Боб \u0007 ");
        Assert.NotEqual(id, bob.Connection.Player);
        Assert.Equal("client-made-token-1", bob.Replica.Welcome!.Token);
        Assert.Equal("Боб", host.Roster.NameOf(bob.Connection.Player));
        var anon = new Client(session, token: "");
        Assert.Equal(32, anon.Replica.Welcome!.Token.Length);
        Assert.Equal($"Игрок {anon.Connection.Player}", anon.Replica.Welcome.Name);
        Assert.True(session.Knows(token) && session.Knows("client-made-token-1") && !session.Knows("unknown-token-xyz"));
        Assert.Equal(9, Enumerable.Range(1, 3).Sum(p => HelloGame.Units(host.World, (uint)p).Count));
    }

    [Fact]
    public void SaveLoadContinuesIdentically()
    {
        Assert.SkipUnless(HelloGame.Built, HelloGame.NotBuilt);
        using var a = new Host(HelloGame.Content(), new HostOptions { Modules = Modules, Seed = 7 });
        a.Start();
        var session = new HostSession(a, new HostSessionOptions { SendRate = 0 });
        var alice = new Client(session, name: "Алиса");
        a.Send(alice.Connection.Player, "hello.spawn", HelloGame.Spawn(60, 10, 10, spread: 20));
        for (var tick = 0; tick < 25; tick++)
            session.Step();
        a.Send(1, "hello.move", HelloGame.Move(-30, 20, [.. HelloGame.Units(a.World, 1)]));
        for (var tick = 0; tick < 7; tick++)
            session.Step();
        static void Flip(Land land, int x, int y) =>
            land.SetTile(x, y, new TileEdit(land.TerrainAt(x, y) == TerrainType.Swamp ? TerrainType.Sand : TerrainType.Swamp, 1, 0, false));
        Flip(a.Land!, 5, 5);
        var file = a.Save();
        Flip(a.Land!, 6, 6); // после сохранения — не в нём

        using var b = Host.Load(HelloGame.Content(), file, new HostOptions { Modules = Modules });
        Assert.Equal((a.Tick, a.Seed, a.Game.Name), (b.Tick, b.Seed, b.Game.Name));
        Assert.True(b.Started);
        Assert.Equal(WorldSave.Save(a.World), WorldSave.Save(b.World));
        Assert.Equal(a.Scheduler.RandomState, b.Scheduler.RandomState);
        Assert.Equal(a.Land!.Stored(5, 5), b.Land!.Stored(5, 5));
        Assert.NotEqual(a.Land.Stored(6, 6), b.Land.Stored(6, 6));
        Assert.True(b.LandEdited);
        Assert.Equal(a.Players, b.Players);
        Assert.Equal(1u, b.Roster.Find(alice.Connection.Token));
        // Разведанное — из слоя, видимое пересчитается.
        Assert.True(b.Vision!.Explored(1, 0.5, 4.5));

        for (var tick = 0; tick < 40; tick++)
        {
            if (tick == 10)
                foreach (var host in new[] { a, b })
                    host.Send(1, "hello.move", HelloGame.Move(15, -15, [.. HelloGame.Units(host.World, 1).Take(30)]));
            a.Step();
            b.Step();
        }
        Assert.Equal(WorldSave.Save(a.World), WorldSave.Save(b.World));
        Assert.Equal(a.Scheduler.RandomState, b.Scheduler.RandomState);
        Assert.Empty(b.Errors);

        // Вернувшийся игрок — тот же, стартовых юнитов не прибавилось; карта — байтами.
        var units = HelloGame.Units(b.World, 1).Count;
        var resumed = new HostSession(b, new HostSessionOptions { SendRate = 0 });
        var again = new Client(resumed, token: alice.Connection.Token);
        Assert.Equal(1u, again.Connection.Player);
        Assert.Equal(units, HelloGame.Units(b.World, 1).Count);
        // Карта правлена: байтами идёт карта до правок (Д3), а то, что игрок о них знает, — поверх.
        Assert.NotNull(again.Replica.Land);
        var pristine = Land.Unpack(b.Land.Config, again.Replica.Land);
        Assert.Equal(b.Recall!.Land!.Original[new Kharos.Engine.Recall.Tile(5, 5)], Kharos.Engine.Recall.LandMemory.Pack(pristine.Stored(5, 5)));
        Assert.Equal(b.Recall.Land.KnownTile(1, new Kharos.Engine.Recall.Tile(5, 5)), again.Replica.Terrain!.Stored(5, 5));
    }

    [Fact]
    public void OtherSaveVersionIsRefused()
    {
        using var host = HelloGame.Create();
        host.Start();
        var file = host.Save();
        Assert.Equal(SaveFile.Version, SaveFile.VersionOf(file));
        var future = (byte[])file.Clone();
        future[4] = 99;
        future[5] = 0;
        var error = Assert.Throws<SaveVersionException>(() => Host.Load(HelloGame.Content(), future, new HostOptions { Modules = Modules }));
        Assert.Equal((99, SaveFile.Version), (error.Found, error.Expected));
        Assert.Contains("99", error.Message);
        Assert.Throws<HostException>(() => Host.Load(HelloGame.Content(), "KHRS\u0017\0garbage"u8, new HostOptions { Modules = Modules }));
        Assert.Throws<HostException>(() => Host.Load(HelloGame.Content(), file.AsSpan(0, file.Length / 2), new HostOptions { Modules = Modules }));

        var path = Path.Combine(Path.GetTempPath(), $"kharos-save-{Guid.NewGuid():n}", "world.khsv");
        try
        {
            SaveFile.WriteAtomic(path, file);
            SaveFile.WriteAtomic(path, file);
            Assert.Equal(file, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public void ChatAndCommands()
    {
        using var host = HelloGame.Create();
        host.Start();
        var log = new List<string>();
        var session = new HostSession(host, new HostSessionOptions { SendRate = 0, AdminPassword = "секрет", ChatGap = TimeSpan.Zero, Log = log.Add });
        var alice = new Client(session, name: "Алиса");
        var bob = new Client(session, name: "Боб");
        Assert.Contains(alice.Chat.Concat(bob.Chat), line => line.Text == "входит в игру" && line.Name == "Боб");

        void Say(Client client, string text)
        {
            client.End.Send(Messages.Say(text));
            Step(session, alice, bob);
        }
        // Управляющие знаки (и табуляция) выбрасываются, пробелы схлопываются — как cleanChat в TS.
        Say(alice, "  привет,\t  всем ");
        Assert.Contains(bob.Chat, line => line is { Player: 1, Name: "Алиса", Text: "привет, всем", Kind: ChatKind.Player });

        Say(bob, "/msg Алиса тайна двоих");
        Assert.Contains(alice.Chat, line => line is { Kind: ChatKind.Whisper, Name: "Боб", Text: "тайна двоих", To: "Алиса" });
        Assert.Contains(bob.Chat, line => line.Kind == ChatKind.Whisper);

        Say(alice, "/kick Боб");
        Assert.Contains(alice.Chat, line => line.Text.StartsWith("Нет команды /kick"));
        Say(alice, "/admin неверно");
        Assert.Contains(alice.Chat, line => line.Text == "Неверный пароль");
        Assert.Contains(log, line => line.Contains("неверный пароль"));
        Assert.DoesNotContain(bob.Chat, line => line.Text.Contains("неверно"));
        Say(bob, "/admin секрет");
        Assert.True(bob.Admin);
        Assert.Contains(session.Commands.Values, c => c.Name == "kick");

        Say(bob, "/players");
        Assert.Contains(bob.Chat, line => line.Text.StartsWith("#1 Алиса: в сети, сущностей 3"));
        Say(bob, "/say  Сервер  уходит на обед");
        Assert.Contains(alice.Chat, line => line is { Player: 0, Name: "[Сервер]", Text: "Сервер уходит на обед" });
        Say(bob, "/help");
        Assert.Contains(bob.Chat, line => line.Text.StartsWith("/kick <player>"));
        Say(bob, "/reveal Алиса");
        Assert.True(host.Vision!.Explored(1, 200.5, 200.5));

        Say(bob, "/kick Алиса");
        Assert.Contains(alice.Refused, reason => reason == "Вас отключили");
        Assert.True(alice.End.Closed);
        Assert.Single(session.Connections);
        Assert.Contains(bob.Chat, line => line is { Name: "Алиса", Text: "отключён" });

        var parsed = ChatCommands.Parse("/MSG \"Игрок 3\" привет   мир")!;
        Assert.Equal("msg", parsed.Name);
        Assert.Equal(["Игрок 3", "привет", "мир"], parsed.Args);
        Assert.Equal("привет   мир", parsed.Rest(1));
        Assert.Null(ChatCommands.Parse("просто текст"));

        // Консоль сервера — администратор без пароля.
        var replies = new List<string>();
        session.Command("list", new Caller(null, true, replies.Add));
        Assert.Equal(["В игре 1: Боб"], replies);
        session.Command("me танцует", new Caller(null, true, replies.Add));
        Assert.StartsWith("Нет команды /me", replies[^1]);
    }

    [Fact]
    public void ModsStreamInChunksWithinBudget()
    {
        using var host = HelloGame.Create();
        host.Start();
        var session = new HostSession(host, new HostSessionOptions { SendRate = 512 * 1024 });
        var client = new Client(session, 1);
        var mod = Assert.Single(client.Replica.Welcome!.Mods);
        Assert.Equal(("hello", "0.2.0"), (mod.Name, mod.Version));
        client.End.Send(Messages.Fetch([mod.Hash]));
        var ticks = 0;
        while (client.Chunks.Sum(chunk => chunk.Data.Length) < mod.Size && ticks < 500)
        {
            Step(session, client);
            ticks++;
        }
        var packed = new byte[mod.Size];
        foreach (var chunk in client.Chunks)
        {
            Assert.True(chunk.Data.Length <= Messages.MaxChunk);
            chunk.Data.CopyTo(packed, chunk.Offset);
        }
        var bytes = ContentPack.Inflate(packed);
        Assert.Equal(mod.Hash, ContentPack.Hash(bytes));
        var pack = ContentPack.Decode(bytes);
        Assert.Equal(File.ReadAllBytes(Path.Combine(HelloGame.ModDir, "client.wasm")), pack.ClientWasm);
        Assert.Contains(pack.Data, file => file.Name == "units");
        // Код хоста клиенту не уходит.
        var hostWasm = File.ReadAllBytes(Path.Combine(HelloGame.ModDir, "host.wasm"));
        Assert.True(bytes.Length < hostWasm.Length + pack.ClientWasm!.Length);
        // 512 КБ/с при 20 тиках — около 26 КБ на тик: пакет идёт не одним тиком.
        var expected = mod.Size / (512.0 * 1024 * host.Dt);
        Assert.InRange(ticks, expected * 0.5, expected * 2 + 4);
        Assert.Equal(0, client.Connection.Downloading);
    }
}
