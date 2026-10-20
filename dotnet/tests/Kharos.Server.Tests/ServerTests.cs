using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Kharos.Protocol;

namespace Kharos.Server.Tests;

/// <summary>
/// Настоящий Kestrel на свободном порту localhost и клиент на ClientWebSocket с протоколом: приветствие, снимок, приказ,
/// отказы, пределы, сохранение. Без собранных модов (wasm) пропускаются.
/// </summary>
public class ServerTests
{
    private static double Distance((double X, double Y) a, double x, double y) => Math.Sqrt((a.X - x) * (a.X - x) + (a.Y - y) * (a.Y - y));

    [Fact]
    public async Task WelcomeAndSnapshotArrive()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        await using var server = await TestServer.StartAsync(temp);
        await using var client = await TestClient.ConnectAsync(server, "a", "Альфа");

        await client.WaitAsync(() => client.Replica.Ready, "приветствие и снимок");
        var welcome = client.Replica.Welcome!;
        Assert.Equal(1u, welcome.Player);
        Assert.Equal("hello", welcome.Game);
        Assert.Equal(Welcome.ProtocolVersion, welcome.Version);
        Assert.Equal(3, client.Units(1).Count);
        Assert.Equal(1, server.Clients);
        Assert.Null(server.Loop.Failure);
    }

    [Fact]
    public async Task OrderPassesAndUnitsMove()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        await using var server = await TestServer.StartAsync(temp);
        await using var client = await TestClient.ConnectAsync(server, "a");
        await client.WaitAsync(() => client.Replica.Ready && client.Units(1).Count == 3, "юниты");

        var unit = client.Units(1)[0];
        var start = client.Position(unit);
        var (tx, ty) = (start.X + 25, start.Y - 15);
        var before = Distance(start, tx, ty);
        await client.SendOrderAsync("hello.move", TestClient.Move(tx, ty, unit));
        await client.WaitAsync(() => Distance(client.Position(unit), tx, ty) < before - 2, "юнит поехал к цели");

        // Приказ на новых юнитов тоже проходит: реплика получает их дельтой.
        await client.SendOrderAsync("hello.spawn", TestClient.Spawn(10, 0, 0, spread: 5));
        await client.WaitAsync(() => client.Units(1).Count == 13, "новые юниты в реплике");
    }

    [Fact]
    public async Task TwoPlayersSeeEachOther()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        // Дома игроков в hello далеко друг от друга, а обзор по умолчанию показывает только своё: здесь - полный фильтр.
        await using var server = await TestServer.StartAsync(temp, options: o => o.Filter = player => new FullView(player));
        await using var first = await TestClient.ConnectAsync(server, "a");
        await first.WaitAsync(() => first.Replica.Ready, "первый вошёл");
        await using var second = await TestClient.ConnectAsync(server, "b");
        await second.WaitAsync(() => second.Replica.Ready && second.Units().Count == 6, "второй видит шесть юнитов");
        Assert.Equal(2u, second.Replica.Player);
        await first.WaitAsync(() => first.Units().Count == 6, "первый видит юниты второго");
        Assert.Equal(3, first.Units(2).Count);
    }

    [Fact]
    public async Task BaseGameStarts()
    {
        Assert.SkipUnless(Env.BaseBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        await using var server = await TestServer.StartAsync(temp, modDir: Env.BaseDir);
        await using var client = await TestClient.ConnectAsync(server, "a");
        await client.WaitAsync(() => client.Replica.Ready, "мир base в реплике");
        await Task.Delay(500);
        client.Pump();
        Assert.True(client.Replica.World!.Count > 3, $"сущностей в реплике: {client.Replica.World.Count}");
        Assert.Equal(1u, client.Replica.Player);
        Assert.NotEqual("hello", client.Replica.Welcome!.Game);
    }

    [Fact]
    public async Task WrongPasswordIsRefused()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        await using var server = await TestServer.StartAsync(temp, tree => tree["password"] = "секрет");

        await using (var none = await TestClient.ConnectAsync(server, "a"))
        {
            var (status, reason) = await none.ClosedAsync();
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
            Assert.Equal("password-needed", reason);
            Assert.False(none.Replica.Ready);
        }
        await using (var wrong = await TestClient.ConnectAsync(server, "a", password: "секрет!"))
        {
            var (status, reason) = await wrong.ClosedAsync();
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
            Assert.Equal("wrong-password", reason);
        }
        await using var right = await TestClient.ConnectAsync(server, "a", password: "секрет");
        await right.WaitAsync(() => right.Replica.Ready, "верный пароль пускает");
        Assert.Equal(1, server.Clients);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public async Task VersionMismatchIsRefused(int version)
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        await using var server = await TestServer.StartAsync(temp);
        await using var client = await TestClient.ConnectAsync(server, "a", version: version);
        var (status, reason) = await client.ClosedAsync();
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
        Assert.StartsWith("version", reason);
        Assert.Contains($"client {version}", reason);
        Assert.False(client.Replica.Ready);
        Assert.Contains(version.ToString(), Assert.Single(client.Refusals()));
    }

    [Fact]
    public async Task PermessageDeflateIsNegotiated()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        await using var server = await TestServer.StartAsync(temp);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = tcp.GetStream();
        var request = $"GET /?id=z&version={Welcome.ProtocolVersion} HTTP/1.1\r\nHost: localhost\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n" +
                      "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Extensions: permessage-deflate\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var buffer = new byte[2048];
        var read = await stream.ReadAsync(buffer);
        var head = Encoding.ASCII.GetString(buffer, 0, read);
        Assert.StartsWith("HTTP/1.1 101", head);
        Assert.Contains("permessage-deflate", head, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlainHttpIsToldToUpgrade()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        await using var server = await TestServer.StartAsync(temp);
        using var http = new HttpClient();
        var response = await http.GetAsync($"http://127.0.0.1:{server.Port}/");
        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    [Fact]
    public async Task ConnectAttemptsPerAddressAreLimitedAndForwardedForIsHonoured()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        await using var server = await TestServer.StartAsync(temp, tree => ((JsonObject)tree["limits"]!)["connectsPerMinute"] = 3);
        using var http = new HttpClient();
        async Task<HttpStatusCode> Try(string address)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/");
            request.Headers.Add("X-Forwarded-For", $"6.6.6.6, {address}");
            return (await http.SendAsync(request)).StatusCode;
        }
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.UpgradeRequired, await Try("10.0.0.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Try("10.0.0.1"));
        // Другой адрес за прокси — свои три попытки.
        Assert.Equal(HttpStatusCode.UpgradeRequired, await Try("10.0.0.2"));
    }

    [Fact]
    public async Task ConnectionsPerAddressAreLimited()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        await using var server = await TestServer.StartAsync(temp, tree => ((JsonObject)tree["limits"]!)["connections"] = 2);
        await using var a = await TestClient.ConnectAsync(server, "a", forwarded: "10.1.1.1");
        await using var b = await TestClient.ConnectAsync(server, "b", forwarded: "10.1.1.1");
        await a.WaitAsync(() => a.Replica.Ready, "первый");
        await b.WaitAsync(() => b.Replica.Ready, "второй");
        var refused = await Assert.ThrowsAsync<WebSocketException>(() => TestClient.ConnectAsync(server, "c", forwarded: "10.1.1.1"));
        Assert.Contains("429", refused.Message);
        // Другой адрес не затронут; после выхода одного с первого освобождается место.
        await using var other = await TestClient.ConnectAsync(server, "d", forwarded: "10.2.2.2");
        await other.WaitAsync(() => other.Replica.Ready, "чужой адрес");
        await a.DisposeAsync();
        var freed = Environment.TickCount64 + 10_000;
        while (server.Clients > 2 && Environment.TickCount64 < freed)
            await Task.Delay(20);
        await using var again = await TestClient.ConnectAsync(server, "c", forwarded: "10.1.1.1");
        await again.WaitAsync(() => again.Replica.Ready, "место освободилось");
    }

    [Fact]
    public async Task NewPlayersPerAddressAreLimitedButReturningOnesAreNot()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        await using var server = await TestServer.StartAsync(temp, tree => ((JsonObject)tree["limits"]!)["newPlayersPerHour"] = 2);
        await using (var a = await TestClient.ConnectAsync(server, "a", forwarded: "10.3.3.3"))
            await a.WaitAsync(() => a.Replica.Ready, "первый новый");
        await using (var b = await TestClient.ConnectAsync(server, "b", forwarded: "10.3.3.3"))
            await b.WaitAsync(() => b.Replica.Ready, "второй новый");
        await using (var c = await TestClient.ConnectAsync(server, "c", forwarded: "10.3.3.3"))
        {
            var (status, reason) = await c.ClosedAsync();
            Assert.Equal((WebSocketCloseStatus.PolicyViolation, "newcomers"), (status, reason));
        }
        // Знакомого пускают, хоть новых и не пускают.
        await using var again = await TestClient.ConnectAsync(server, "a", forwarded: "10.3.3.3");
        await again.WaitAsync(() => again.Replica.Ready && again.Replica.Player == 1, "знакомый игрок");
        await using var elsewhere = await TestClient.ConnectAsync(server, "e", forwarded: "10.4.4.4");
        await elsewhere.WaitAsync(() => elsewhere.Replica.Ready, "новый с другого адреса");
    }

    [Fact]
    public async Task SecondConnectionOfSamePlayerReplacesFirst()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        await using var server = await TestServer.StartAsync(temp);
        await using var first = await TestClient.ConnectAsync(server, "a");
        await first.WaitAsync(() => first.Replica.Ready, "первое подключение");
        await using var second = await TestClient.ConnectAsync(server, "a");
        await second.WaitAsync(() => second.Replica.Ready && second.Replica.Player == 1, "второе подключение");
        var (status, reason) = await first.ClosedAsync();
        Assert.Equal((WebSocketCloseStatus.NormalClosure, "replaced"), (status, reason));
        Assert.Equal(3, second.Units(1).Count);
    }

    [Fact(DisplayName = "поток сообщений: лишнее отбрасывается, а кто не унимается — отключается; другим игрокам это не мешает")]
    public async Task MessageFloodDisconnectsClient()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        await using var server = await TestServer.StartAsync(temp);
        await using var client = await TestClient.ConnectAsync(server, "a");
        await client.WaitAsync(() => client.Replica.Ready, "вошёл");
        await using var quiet = await TestClient.ConnectAsync(server, "quiet");
        await quiet.WaitAsync(() => quiet.Replica.Ready, "тихий вошёл");
        var junk = DeltaCodec.Order("x", []);
        // Burst 120 проходит, 300 прощается, дальше — отключение.
        try
        {
            for (var i = 0; i < 2000; i++)
                await client.SendAsync(junk);
        }
        catch (WebSocketException)
        {
        }
        // Поток сообщений считает сессия хоста: отказ сообщением и штатное закрытие.
        var (status, _) = await client.ClosedAsync();
        Assert.Equal(WebSocketCloseStatus.NormalClosure, status);
        Assert.Contains("Слишком много сообщений", client.Refusals());
        // Другим игрокам это не мешает: тихий на месте и слышит сервер.
        await quiet.SendAsync(DeltaCodec.Order("x", []));
        Assert.Equal(WebSocketState.Open, quiet.State);
        Assert.False(quiet.Finished);
    }

    [Fact]
    public async Task OversizedMessageIsRefused()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        await using var server = await TestServer.StartAsync(temp);
        await using var client = await TestClient.ConnectAsync(server, "a");
        await client.WaitAsync(() => client.Replica.Ready, "вошёл");
        await client.SendAsync(new byte[300 * 1024]);
        var (status, _) = await client.ClosedAsync();
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, status);
    }

    [Fact]
    public async Task SaveIsWrittenAtomicallyAndWorldReturnsAfterRestart()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        var saved = temp.File("save.kharos");
        var log = new List<string>();
        List<(double X, double Y)> positions;
        uint moved;
        await using (var server = await TestServer.StartAsync(temp, options: o => o.Log = log.Add))
        {
            await using var client = await TestClient.ConnectAsync(server, "alpha", "Альфа");
            await client.WaitAsync(() => client.Replica.Ready, "вошёл");
            await client.SendOrderAsync("hello.spawn", TestClient.Spawn(20, 10, 10, spread: 8));
            await client.WaitAsync(() => client.Units(1).Count == 23, "двадцать юнитов");
            moved = client.Units(1)[0];
            await client.SendOrderAsync("hello.move", TestClient.Move(40, 40, moved));
            await client.WaitAsync(() => client.Position(moved).X > 0, "поехал");
            await server.Loop.SaveAsync();

            Assert.True(File.Exists(saved));
            Assert.False(File.Exists(saved + ".tmp"));
            await client.WaitAsync(() => true, "тик");
            positions = client.Units(1).Select(client.Position).ToList();
        }
        // Остановка сохраняет ещё раз (мир успел уйти дальше), и временного файла нет.
        Assert.True(File.Exists(saved));
        Assert.False(File.Exists(saved + ".tmp"));
        Assert.Contains(log, line => line.StartsWith("мир сохранён"));

        await using var restarted = await TestServer.StartAsync(temp, options: o => o.Log = log.Add);
        Assert.Contains(log, line => line.StartsWith("мир загружен"));
        await using var back = await TestClient.ConnectAsync(restarted, "alpha");
        await back.WaitAsync(() => back.Replica.Ready, "вошёл после перезапуска");
        // Тот же игрок с теми же юнитами: не 3 стартовых, а все 23 из сохранения.
        Assert.Equal(1u, back.Replica.Player);
        await back.WaitAsync(() => back.Units(1).Count == 23, "юниты вернулись");
        Assert.Equal(23, back.Units().Count);
        Assert.Contains(back.Units(1), unit => unit == moved);
        Assert.Equal(positions.Count, back.Units(1).Count);
        // Новый игрок получает следующий номер и свои три юнита.
        await using var newcomer = await TestClient.ConnectAsync(restarted, "beta");
        await newcomer.WaitAsync(() => newcomer.Replica.Ready && newcomer.Units(2).Count == 3, "новичок");
        Assert.Equal(2u, newcomer.Replica.Player);
    }

    [Fact]
    public async Task UnreadableSaveIsMovedAsideAndWorldStartsAnew()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        File.WriteAllText(temp.File("save.kharos"), "это не сохранение");
        await using var server = await TestServer.StartAsync(temp);
        Assert.Single(Directory.GetFiles(temp.Path, "save.kharos.*.old"));
        await using var client = await TestClient.ConnectAsync(server, "a");
        await client.WaitAsync(() => client.Replica.Ready && client.Units(1).Count == 3, "новый мир");
    }

    [Fact]
    public async Task SaveOfOtherVersionIsMovedAside()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        File.WriteAllBytes(temp.File("save.kharos"), SaveFile.Pack([("META", [1, 2, 3])], SaveFile.Version + 1));
        await using var server = await TestServer.StartAsync(temp);
        Assert.True(File.Exists(temp.File($"save.kharos.v{SaveFile.Version + 1}")));
        await using var client = await TestClient.ConnectAsync(server, "a");
        await client.WaitAsync(() => client.Replica.Ready && client.Units(1).Count == 3, "новый мир");
    }

    [Fact]
    public async Task StopClosesClientsGracefullyAndSaves()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        var server = await TestServer.StartAsync(temp);
        await using var client = await TestClient.ConnectAsync(server, "a");
        await client.WaitAsync(() => client.Replica.Ready, "вошёл");
        await server.DisposeAsync();
        var (status, _) = await client.ClosedAsync();
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, status);
        Assert.True(File.Exists(temp.File("save.kharos")));
    }

    [Fact]
    public async Task ConsoleCommandsWork()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        await using var server = await TestServer.StartAsync(temp);
        Assert.Contains("В игре никого", await server.CommandAsync("list"));
        await using var client = await TestClient.ConnectAsync(server, "a", "Альфа");
        await client.WaitAsync(() => client.Replica.Ready, "вошёл");

        Assert.Contains(await server.CommandAsync("list"), line => line.Contains("Альфа"));
        Assert.Contains(await server.CommandAsync("players"), line => line.StartsWith("#1 Альфа — в сети"));
        Assert.Contains(await server.CommandAsync("status"), line => line.Contains("клиентов 1"));
        Assert.Contains(await server.CommandAsync("help"), line => line.Contains("save"));
        Assert.Contains(await server.CommandAsync("/save"), line => line.StartsWith("мир сохранён"));
        Assert.True(File.Exists(temp.File("save.kharos")));
        // say от имени сервера доходит до игроков чатом.
        Assert.Empty(await server.CommandAsync("say Привет всем"));
        await client.WaitAsync(() => client.Replica.Inbox.Any(m => m[0] == (byte)MessageType.Chat && Messages.ReadChat(m).Any(line => line.Text.Contains("Привет всем"))), "say дошёл");
        Assert.Contains(await server.CommandAsync("kick Никто"), line => line.Contains("Нет игрока"));
        Assert.Empty(await server.CommandAsync("kick Альфа"));
        var (status, _) = await client.ClosedAsync();
        Assert.Equal(WebSocketCloseStatus.NormalClosure, status);
        Assert.Contains("Вас отключил администратор", client.Refusals());
        Assert.Contains("Нет команды /fly", Assert.Single(await server.CommandAsync("fly")));
    }

    [Fact]
    public async Task TlsAutoIsRefusedClearly()
    {
        using var temp = new Env.Temp();
        var error = await Assert.ThrowsAsync<SettingsException>(() => TestServer.StartAsync(temp, tree => ((JsonObject)tree["tls"]!)["mode"] = "auto"));
        Assert.Contains("auto", error.Message);
        Assert.Contains("files", error.Message);
    }

    [Fact]
    public async Task MissingModsAreAnError()
    {
        using var temp = new Env.Temp();
        var error = await Assert.ThrowsAsync<SettingsException>(() => TestServer.StartAsync(temp, modDir: temp.File("нет-такой-папки")));
        Assert.Contains("нет ни одного мода", error.Message);
    }

    [Fact]
    public async Task ModsFromSettingsAreLoaded()
    {
        Assert.SkipUnless(Env.HelloBuilt, Env.NotBuilt);
        using var temp = new Env.Temp();
        await using var server = await TestServer.StartAsync(temp, tree => tree["mods"] = new JsonArray(Env.HelloDir), options: o => o.ModRoots = []);
        await using var client = await TestClient.ConnectAsync(server, "a");
        await client.WaitAsync(() => client.Replica.Ready && client.Units(1).Count == 3, "мод из settings.mods");
    }
}
