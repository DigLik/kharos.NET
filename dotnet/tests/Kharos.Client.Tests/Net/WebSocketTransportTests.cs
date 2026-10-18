// Ожидания тестов ограничены своими таймаутами (Wait), токен теста им не нужен.
#pragma warning disable xUnit1051
using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using Kharos.Client.Net;

namespace Kharos.Client.Tests.Net;

/// <summary>Транспорт WebSocket (Н4) против Kestrel на localhost: порядок, большие сообщения, закрытие, запрос рукопожатия.</summary>
public sealed class WebSocketTransportTests
{
    private static Task<WebSocketTransport> Connect(TestServer server, WebSocketTransportOptions? options = null) =>
        WebSocketTransport.ConnectAsync(server.Url, options);

    [Fact]
    public async Task QueryHasAllFieldsEncoded()
    {
        await using var server = await TestServer.StartAsync();
        var url = WebSocketTransport.WithQuery(server.Url, [new("id", "abc123"), new("name", "Игрок & Ко"), new("password", "п@ss=w/ord?"), new("version", "7"), new("empty", ""), new("none", null)]);
        using var transport = await WebSocketTransport.ConnectAsync(url);
        // Рукопожатие прошло: эхо подтверждает связь, а запрос уже записан.
        transport.Send([1]);
        Assert.Equal(new byte[] { 1 }, await transport.ReceiveAsync());
        Assert.Equal("abc123", server.Query!["id"]);
        Assert.Equal("Игрок & Ко", server.Query["name"]);
        Assert.Equal("п@ss=w/ord?", server.Query["password"]);
        Assert.Equal("7", server.Query["version"]);
        Assert.False(server.Query.ContainsKey("empty"));
        Assert.False(server.Query.ContainsKey("none"));
    }

    [Fact]
    public void WithQueryKeepsExistingParameters()
    {
        var url = WebSocketTransport.WithQuery(new Uri("ws://host:81/play?room=a"), [new("id", "x y")]);
        Assert.Equal("ws://host:81/play?room=a&id=x%20y", url.AbsoluteUri);
    }

    [Fact]
    public async Task MessagesKeepOrderBothWays()
    {
        await using var server = await TestServer.StartAsync();
        using var transport = await Connect(server);
        const int count = 2000;
        for (var i = 0; i < count; i++)
        {
            var message = new byte[4 + i % 50];
            BinaryPrimitives.WriteInt32LittleEndian(message, i);
            transport.Send(message);
        }
        for (var i = 0; i < count; i++)
        {
            var message = await Wait.For(transport.ReceiveAsync().AsTask(), $"сообщение {i}");
            Assert.NotNull(message);
            Assert.Equal(i, BinaryPrimitives.ReadInt32LittleEndian(message));
            Assert.Equal(4 + i % 50, message.Length);
        }
        Assert.False(transport.TryReceive(out _));
        Assert.False(transport.Closed);
    }

    [Fact]
    public async Task EmptyMessageIsDelivered()
    {
        await using var server = await TestServer.StartAsync();
        using var transport = await Connect(server);
        transport.Send([]);
        transport.Send([9]);
        Assert.Empty((await Wait.For(transport.ReceiveAsync().AsTask(), "пустое"))!);
        Assert.Equal(new byte[] { 9 }, await Wait.For(transport.ReceiveAsync().AsTask(), "следующее"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LargeMessageUpToLimitRoundTrips(bool compression)
    {
        await using var server = await TestServer.StartAsync();
        var options = new WebSocketTransportOptions { MaxMessageSize = 1 << 20, Compression = compression };
        using var transport = await Connect(server, options);
        // Ровно предел: несжимаемые байты (худший случай для сжатия) и обычные.
        var random = new byte[options.MaxMessageSize];
        new Random(5).NextBytes(random);
        var patterned = new byte[options.MaxMessageSize];
        for (var i = 0; i < patterned.Length; i++)
            patterned[i] = (byte)(i / 7);
        transport.Send(random);
        transport.Send(patterned);
        var backRandom = await Wait.For(transport.ReceiveAsync().AsTask(), "случайные");
        var backPatterned = await Wait.For(transport.ReceiveAsync().AsTask(), "узор");
        Assert.True(random.AsSpan().SequenceEqual(backRandom));
        Assert.True(patterned.AsSpan().SequenceEqual(backPatterned));
        Assert.False(transport.Closed);
    }

    [Fact]
    public async Task MessageOverLimitClosesWith1009()
    {
        await using var server = await TestServer.StartAsync();
        server.Handler = async (socket, cancel) =>
        {
            await socket.SendAsync(new byte[] { 1 }, WebSocketMessageType.Binary, true, cancel);
            await socket.SendAsync(new byte[300_000], WebSocketMessageType.Binary, true, cancel);
            // Остаёмся читать, пока клиент не закроет.
            await TestServer.Echo(socket, cancel);
        };
        using var transport = await Connect(server, new WebSocketTransportOptions { MaxMessageSize = 256 * 1024 });
        Assert.Equal(new byte[] { 1 }, await Wait.For(transport.ReceiveAsync().AsTask(), "первое"));
        Assert.Null(await Wait.For(transport.ReceiveAsync().AsTask(), "закрытие"));
        Assert.True(transport.Closed);
        Assert.Contains("длиннее", transport.CloseReason);
        transport.Send([1]);
    }

    [Fact]
    public async Task ServerCloseDeliversQueuedMessagesThenReason()
    {
        await using var server = await TestServer.StartAsync();
        server.Handler = async (socket, cancel) =>
        {
            await socket.SendAsync(new byte[] { 1 }, WebSocketMessageType.Binary, true, cancel);
            await socket.SendAsync(new byte[] { 2 }, WebSocketMessageType.Binary, true, cancel);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "неверный пароль", cancel);
        };
        using var transport = await Connect(server);
        await Wait.Until(() => transport.CloseReason is not null, "причина закрытия");
        // Закрыт лишь после разбора пришедшего.
        Assert.False(transport.Closed);
        Assert.True(transport.TryReceive(out var first));
        Assert.Equal(new byte[] { 1 }, first);
        Assert.Equal(new byte[] { 2 }, await transport.ReceiveAsync());
        Assert.Null(await Wait.For(transport.ReceiveAsync().AsTask(), "конец"));
        Assert.True(transport.Closed);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, transport.CloseStatus);
        Assert.Equal("неверный пароль", transport.CloseReason);
        Assert.Null(transport.Error);
    }

    [Fact]
    public async Task DisposeFlushesSendsAndClosesNormally()
    {
        await using var server = await TestServer.StartAsync();
        var received = new List<byte[]>();
        server.Handler = async (socket, cancel) =>
        {
            var buffer = new byte[1024];
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancel);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    server.ClientClosed.TrySetResult(result.CloseStatus);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancel);
                    return;
                }
                lock (received)
                    received.Add(buffer[..result.Count]);
            }
        };
        var transport = await Connect(server);
        transport.Send([1, 2]);
        transport.Send([3]);
        transport.Dispose();
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await Wait.For(server.ClientClosed.Task, "закрытие на сервере"));
        lock (received)
            Assert.Equal([[1, 2], [3]], received);
        Assert.True(transport.Closed);
        transport.Send([4]);
        transport.Dispose();
    }

    [Fact]
    public async Task TextFrameBreaksProtocol()
    {
        await using var server = await TestServer.StartAsync();
        server.Handler = async (socket, cancel) =>
        {
            await socket.SendAsync("hi"u8.ToArray(), WebSocketMessageType.Text, true, cancel);
            await TestServer.Echo(socket, cancel);
        };
        using var transport = await Connect(server);
        Assert.Null(await Wait.For(transport.ReceiveAsync().AsTask(), "закрытие"));
        Assert.True(transport.Closed);
        Assert.Contains("текстовый", transport.CloseReason);
    }

    [Fact]
    public async Task ServerDropWithoutCloseFrameIsError()
    {
        await using var server = await TestServer.StartAsync();
        server.Handler = (socket, _) =>
        {
            socket.Abort();
            return Task.CompletedTask;
        };
        using var transport = await Connect(server);
        Assert.Null(await Wait.For(transport.ReceiveAsync().AsTask(), "обрыв"));
        Assert.True(transport.Closed);
        Assert.NotNull(transport.CloseReason);
        Assert.Null(transport.CloseStatus);
    }

    [Fact]
    public async Task SendQueueOverflowClosesTransport()
    {
        await using var server = await TestServer.StartAsync();
        using var transport = await Connect(server, new WebSocketTransportOptions { MaxPendingSendBytes = 1000 });
        transport.Send(new byte[5000]);
        await Wait.Until(() => transport.Closed, "закрытие по переполнению");
        Assert.Contains("переполнена", transport.CloseReason);
    }

    [Fact]
    public async Task RefusedHandshakeReportsStatus()
    {
        await using var server = await TestServer.StartAsync();
        server.RejectWith = 403;
        var error = await Assert.ThrowsAsync<TransportException>(() => Connect(server));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("403", error.Message);
    }

    [Fact]
    public async Task UnreachableServerThrows()
    {
        var port = Free();
        var error = await Assert.ThrowsAsync<TransportException>(() => WebSocketTransport.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/play?password=secret")));
        Assert.Null(error.StatusCode);
        // Пароля из запроса в сообщении нет.
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task WrongSchemeThrows()
    {
        await Assert.ThrowsAsync<TransportException>(() => WebSocketTransport.ConnectAsync(new Uri("http://127.0.0.1:1/")));
    }

    [Fact]
    public async Task ConnectTimeoutThrows()
    {
        // Слушаем, но рукопожатия не отвечаем.
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var options = new WebSocketTransportOptions { ConnectTimeout = TimeSpan.FromMilliseconds(300) };
        var error = await Assert.ThrowsAsync<TransportException>(() => WebSocketTransport.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), options));
        Assert.Contains("нет ответа", error.Message);
    }

    [Fact]
    public async Task ReceiveAsyncCanBeCancelled()
    {
        await using var server = await TestServer.StartAsync();
        using var transport = await Connect(server);
        using var cancel = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await transport.ReceiveAsync(cancel.Token));
        Assert.False(transport.Closed);
    }

    [Fact]
    public async Task SendFromManyThreadsLosesNothing()
    {
        await using var server = await TestServer.StartAsync();
        using var transport = await Connect(server);
        const int threads = 4, each = 500;
        await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < each; i++)
                transport.Send([(byte)t, (byte)(i & 0xff), (byte)(i >> 8)]);
        })));
        var next = new int[threads];
        for (var n = 0; n < threads * each; n++)
        {
            var message = await Wait.For(transport.ReceiveAsync().AsTask(), $"сообщение {n}");
            Assert.NotNull(message);
            // Внутри одного потока порядок сохраняется.
            Assert.Equal(next[message[0]]++, message[1] | message[2] << 8);
        }
    }

    private static int Free()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
