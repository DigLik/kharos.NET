using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Kharos.Client.Tests.Net;

/// <summary>Сервер WebSocket на Kestrel (localhost, свободный порт): что делать с соединением — решает обработчик теста.</summary>
internal sealed class TestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestServer(WebApplication app, Uri url)
    {
        _app = app;
        Url = url;
    }

    public Uri Url { get; }

    /// <summary>Запрос последнего рукопожатия.</summary>
    public IQueryCollection? Query { get; private set; }

    /// <summary>Ответ HTTP вместо рукопожатия (отказ до WebSocket); null — принимать.</summary>
    public int? RejectWith { get; set; }

    /// <summary>Закрытие, которое увидел сервер от клиента.</summary>
    public TaskCompletionSource<WebSocketCloseStatus?> ClientClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Обработчик соединения; по умолчанию — эхо.</summary>
    public Func<WebSocket, CancellationToken, Task> Handler { get; set; } = Echo;

    public static async Task<TestServer> StartAsync(Func<WebSocket, CancellationToken, Task>? handler = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        TestServer? server = null;
        app.Map("/play", async (HttpContext context) =>
        {
            server!.Query = context.Request.Query;
            if (server.RejectWith is { } status)
            {
                context.Response.StatusCode = status;
                return;
            }
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { DangerousEnableCompression = true });
            await server.Handler(socket, context.RequestAborted);
        });
        await app.StartAsync();
        var address = app.Urls.First().Replace("http://", "ws://", StringComparison.Ordinal);
        server = new TestServer(app, new Uri(address + "/play"));
        if (handler is not null)
            server.Handler = handler;
        return server;
    }

    /// <summary>Эхо: каждое сообщение — обратно, Close — ответным Close; о закрытии клиентом сообщается в <see cref="ClientClosed"/>.</summary>
    public static Task Echo(WebSocket socket, CancellationToken cancel) => Relay(socket, cancel, null);

    private static async Task Relay(WebSocket socket, CancellationToken cancel, TaskCompletionSource<WebSocketCloseStatus?>? closed)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancel);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    closed?.TrySetResult(result.CloseStatus);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancel);
                    return;
                }
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;
                await socket.SendAsync(message.ToArray(), result.MessageType, true, cancel);
                message.SetLength(0);
            }
        }
        catch (Exception) when (cancel.IsCancellationRequested || socket.State is WebSocketState.Aborted)
        {
            closed?.TrySetResult(null);
        }
        catch (WebSocketException)
        {
            closed?.TrySetResult(null);
        }
    }

    /// <summary>Эхо, замечающее закрытие клиентом.</summary>
    public Task EchoAndWatch(WebSocket socket, CancellationToken cancel) => Relay(socket, cancel, ClientClosed);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>Ожидание условия из тестов транспорта.</summary>
internal static class Wait
{
    public static async Task Until(Func<bool> condition, string what, double seconds = 10)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > limit)
                throw new TimeoutException($"не дождались: {what}");
            await Task.Delay(5);
        }
    }

    public static async Task<T> For<T>(Task<T> task, string what, double seconds = 10)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))) != task)
            throw new TimeoutException($"не дождались: {what}");
        return await task;
    }
}
