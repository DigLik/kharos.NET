using System.Net.WebSockets;

namespace Kharos.Server.Tests;

/// <summary>Серверный конец WebSocket: очередь отправки, предел частоты, размер сообщения, закрытие (на поддельном сокете).</summary>
public class TransportTests
{
    private static async Task WaitTrue(Func<bool> probe, string what)
    {
        var limit = Environment.TickCount64 + 10_000;
        while (!probe())
        {
            if (Environment.TickCount64 > limit)
                throw new TimeoutException(what);
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task DeliversInOrderBothWays()
    {
        var socket = new FakeWebSocket();
        var transport = new WebSocketTransport(socket);
        var run = transport.RunAsync(CancellationToken.None);

        transport.Send([1]);
        transport.Send([2, 2]);
        socket.Feed([9]);
        socket.Feed([8, 8]);
        await WaitTrue(() => socket.Sent.Count == 2, "отправка");
        Assert.Equal([[1], [2, 2]], socket.Sent.ToArray());
        byte[]? message = null;
        await WaitTrue(() => transport.TryReceive(out message), "приём");
        Assert.Equal([9], message);
        Assert.Equal([8, 8], await transport.ReceiveAsync());
        Assert.False(transport.Closed);

        socket.ClientClose();
        await run;
        Assert.True(transport.Closed);
        Assert.Null(await transport.ReceiveAsync());
    }

    [Fact]
    public async Task JoinsFragmentedMessages()
    {
        var socket = new FakeWebSocket();
        var transport = new WebSocketTransport(socket);
        var run = transport.RunAsync(CancellationToken.None);
        socket.Feed([1, 2], end: false);
        socket.Feed([3], end: true);
        Assert.Equal([1, 2, 3], await transport.ReceiveAsync());
        socket.ClientClose();
        await run;
    }

    [Fact]
    public async Task DropsMessagesOverRateThenDisconnectsFlooder()
    {
        long now = 0;
        var socket = new FakeWebSocket();
        var transport = new WebSocketTransport(socket, new TransportLimits { Rate = 10, Burst = 5, FloodBurst = 3, FloodRate = 1, Now = () => now, CloseWait = TimeSpan.FromMilliseconds(100) });
        var run = transport.RunAsync(CancellationToken.None);
        for (var i = 0; i < 5; i++)
            socket.Feed([(byte)i]);
        // Пять прошли, следующие три теряются без последствий.
        for (var i = 0; i < 3; i++)
            socket.Feed([100]);
        await WaitTrue(() => transport.Dropped == 3, "сброс");
        var received = new List<byte>();
        while (transport.TryReceive(out var message))
            received.Add(message[0]);
        Assert.Equal([0, 1, 2, 3, 4], received);
        Assert.Null(transport.CloseReason);
        // Не унимается — отключается с причиной flood.
        socket.Feed([100]);
        await run;
        Assert.Equal("flood", transport.CloseReason);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.ClosedWith);
        Assert.Equal("flood", socket.ClosedReason);
        Assert.True(transport.Closed);
    }

    [Fact]
    public async Task RateRefillsOverTime()
    {
        long now = 0;
        var socket = new FakeWebSocket();
        var transport = new WebSocketTransport(socket, new TransportLimits { Rate = 10, Burst = 2, FloodBurst = 100, Now = () => now });
        var run = transport.RunAsync(CancellationToken.None);
        var got = new List<byte>();
        bool Drain(int count)
        {
            while (transport.TryReceive(out var message))
                got.Add(message[0]);
            return got.Count == count;
        }
        for (byte i = 1; i <= 4; i++)
            socket.Feed([i]);
        await WaitTrue(() => transport.Dropped == 2 && Drain(2), "сброс");
        Assert.Equal([1, 2], got);
        now += 1000;
        socket.Feed([5]);
        socket.Feed([6]);
        await WaitTrue(() => Drain(4), "пополнение");
        Assert.Equal([1, 2, 5, 6], got);
        Assert.Equal(2, transport.Dropped);
        socket.ClientClose();
        await run;
    }

    [Fact]
    public async Task OversizedMessageClosesWith1009()
    {
        var socket = new FakeWebSocket();
        var transport = new WebSocketTransport(socket, new TransportLimits { MaxMessage = 100, CloseWait = TimeSpan.FromMilliseconds(100) });
        var run = transport.RunAsync(CancellationToken.None);
        socket.Feed(new byte[60], end: false);
        socket.Feed(new byte[60], end: true);
        await run;
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.ClosedWith);
        Assert.False(transport.TryReceive(out _));
    }

    [Fact]
    public async Task TextMessagesAreRefused()
    {
        var socket = new FakeWebSocket();
        var transport = new WebSocketTransport(socket, new TransportLimits { CloseWait = TimeSpan.FromMilliseconds(100) });
        var run = transport.RunAsync(CancellationToken.None);
        socket.Feed("{}"u8.ToArray(), WebSocketMessageType.Text);
        await run;
        Assert.Equal(WebSocketCloseStatus.InvalidMessageType, socket.ClosedWith);
    }

    [Fact]
    public async Task SlowClientIsKicked()
    {
        // Клиент не читает: SendAsync зависает, очередь растёт до предела — отключение 1013, без ожидания клиента.
        var socket = new FakeWebSocket(sendOpen: false);
        var transport = new WebSocketTransport(socket, new TransportLimits { MaxQueued = 5, Free = 2, CloseWait = TimeSpan.FromMilliseconds(100) });
        var run = transport.RunAsync(CancellationToken.None);
        for (var i = 0; i < 20; i++)
            transport.Send(new byte[100]);
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("slow", transport.CloseReason);
        Assert.True(transport.Closed);
        Assert.True(socket.Sent.IsEmpty);
        // После отключения Send молчит.
        transport.Send([1]);
        Assert.True(transport.Queued <= 6);
    }

    [Fact]
    public async Task QueuedBytesAlsoLimitedButFirstMessagesAreFree()
    {
        var socket = new FakeWebSocket(sendOpen: false);
        var transport = new WebSocketTransport(socket, new TransportLimits { MaxQueuedBytes = 1000, Free = 2, CloseWait = TimeSpan.FromMilliseconds(100) });
        var run = transport.RunAsync(CancellationToken.None);
        // Снимок больше предела байт — но он из первых двух сообщений.
        transport.Send(new byte[5000]);
        transport.Send(new byte[5000]);
        Assert.Null(transport.CloseReason);
        transport.Send(new byte[10]);
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("slow", transport.CloseReason);
    }

    [Fact]
    public async Task CloseFlushesQueueThenClosesWithReason()
    {
        var socket = new FakeWebSocket();
        var transport = new WebSocketTransport(socket, new TransportLimits { CloseWait = TimeSpan.FromMilliseconds(100) });
        var run = transport.RunAsync(CancellationToken.None);
        transport.Send([1]);
        transport.Send([2]);
        transport.Close(WebSocketCloseStatus.PolicyViolation, "kick");
        await run;
        Assert.Equal(2, socket.Sent.Count);
        Assert.Equal((WebSocketCloseStatus.PolicyViolation, "kick"), (socket.ClosedWith!.Value, socket.ClosedReason));
        // Второе закрытие ничего не меняет.
        transport.Close(WebSocketCloseStatus.NormalClosure, "other");
        Assert.Equal("kick", transport.CloseReason);
    }

    [Fact]
    public async Task DisposeClosesNormally()
    {
        var socket = new FakeWebSocket();
        var transport = new WebSocketTransport(socket, new TransportLimits { CloseWait = TimeSpan.FromMilliseconds(100) });
        var run = transport.RunAsync(CancellationToken.None);
        transport.Send([7]);
        transport.Dispose();
        await run;
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.ClosedWith);
        Assert.Single(socket.Sent);
    }
}
