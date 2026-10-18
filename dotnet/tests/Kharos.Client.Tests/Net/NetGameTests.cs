// Ожидания тестов ограничены своими таймаутами, токен теста им не нужен.
#pragma warning disable xUnit1051
using System.Diagnostics;
using System.Net.WebSockets;
using Hello;
using Kharos.Client.Tests.Game;
using Kharos.Client.Game;
using Kharos.Client.Net;
using Kharos.Client.Ui;
using Kharos.Content;
using Kharos.Engine.World;
using Kharos.Host;
using Kharos.Plugins.Game;
using HostGame = Kharos.Host.Host;

namespace Kharos.Client.Tests.Net;

/// <summary>Клиент игры по настоящему WebSocket: Kestrel перекладывает байты между сокетом и хостом партии hello.</summary>
public sealed class NetGameTests
{
    [Fact]
    public async Task ClientPlaysHelloOverWebSocket()
    {
        Assert.SkipUnless(HelloSession.Built, HelloSession.NotBuilt);
        var content = GameContent.Load([ModPackage.Read(HelloSession.ModDir)]);
        var modules = new GameModuleOptions { CallTimeoutMs = 1000 };
        using var local = LocalGame.Start(() => new HostGame(content, new HostOptions { Modules = modules, Seed = 1 }));
        await using var server = await TestServer.StartAsync((socket, cancel) => Bridge(socket, local.Client, cancel));
        var url = WebSocketTransport.WithQuery(server.Url, [new("id", "token"), new("version", "1")]);
        using var transport = await WebSocketTransport.ConnectAsync(url);
        var log = new List<string>();
        // Конструктор ждёт приветствие опросом: сервер отвечает из пула потоков, не из этого.
        using var game = new ClientGame(content, transport, new ClientGameOptions { ModuleOptions = modules, Log = log.Add });
        Assert.Equal("token", server.Query!["id"]);

        var clock = Stopwatch.StartNew();
        var input = new ClientInput { Width = 1280, Height = 720 };
        void Step(ClientInput? given = null) => game.Step((given ?? input) with { Time = clock.Elapsed.TotalSeconds });
        void Until(Func<bool> done, string what)
        {
            while (!done())
            {
                Assert.True(clock.Elapsed.TotalSeconds < 20, $"не дождались: {what}; журнал: {string.Join(" | ", log.TakeLast(5))}");
                Step();
                Thread.Sleep(15);
            }
        }
        int Units()
        {
            var world = game.Replica.World!;
            var found = new List<uint>();
            world.Query([world.Schema.Id("Drive"), Builtin.Owner], found);
            return found.Count(entity => world.Number(entity, Builtin.Owner, 0) == game.Replica.Player);
        }

        Until(() => Units() >= 3, "три ровера из снимка");
        // Щелчок по кнопке HUD: приказ уходит по сокету, хост создаёт пять юнитов, изменение возвращается.
        Step();
        var rect = game.Gui.RectOf(HelloClient.SpawnButton) ?? throw new Xunit.Sdk.XunitException("нет кнопки HUD");
        var pointer = (rect.X + rect.W / 2, rect.Y + rect.H / 2);
        Step(input with { Pointer = pointer });
        Step(input with { Pointer = pointer, Pressed = PointerButtons.Left, Down = PointerButtons.Left });
        Step(input with { Pointer = pointer, Released = PointerButtons.Left });
        Until(() => Units() >= 8, "восемь роверов после приказа");
        Assert.Empty(game.Errors);
        Assert.Null(local.Failure);
        Assert.False(transport.Closed);
    }

    /// <summary>Сервер игры из одного игрока: что пришло по сокету — хосту, что сказал хост — в сокет.</summary>
    private static async Task Bridge(WebSocket socket, Kharos.Protocol.MemoryTransport host, CancellationToken cancel)
    {
        var toSocket = Task.Run(async () =>
        {
            try
            {
                while (await host.ReceiveAsync(cancel) is { } message)
                    await socket.SendAsync(message, WebSocketMessageType.Binary, true, cancel);
            }
            catch (Exception) when (cancel.IsCancellationRequested || socket.State is not WebSocketState.Open)
            {
                // Клиент ушёл: передавать некому.
            }
        }, CancellationToken.None);
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancel);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;
                host.Send(message.ToArray());
                message.SetLength(0);
            }
        }
        catch (Exception) when (cancel.IsCancellationRequested || socket.State is WebSocketState.Aborted)
        {
        }
    }
}
