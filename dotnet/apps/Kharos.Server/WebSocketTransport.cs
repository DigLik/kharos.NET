using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Threading.Channels;
using Kharos.Protocol;

namespace Kharos.Server;

/// <summary>Пределы одного подключения.</summary>
public sealed record TransportLimits
{
    /// <summary>Длиннее сообщение от клиента не принимается (как maxPayloadLength сервера на TS).</summary>
    public int MaxMessage { get; init; } = 256 * 1024;

    /// <summary>Сообщений в секунду в среднем и разом: что сверх — отбрасывается (packages/host: MESSAGE_RATE, MESSAGE_BURST).</summary>
    public double Rate { get; init; } = 30;
    public double Burst { get; init; } = 120;

    /// <summary>Сколько отброшенных сообщений прощается разом и в секунду: дальше подключение отключается.</summary>
    public double FloodBurst { get; init; } = 300;
    public double FloodRate { get; init; } = 1;

    /// <summary>Сколько сообщений и байт может ждать отправки; больше — клиент не успевает, его отключают.</summary>
    public int MaxQueued { get; init; } = 512;
    public long MaxQueuedBytes { get; init; } = 48L * 1024 * 1024;

    /// <summary>Сколько первых сообщений (приветствие и снимок) не считаются в пределах очереди: снимок большой.</summary>
    public int Free { get; init; } = 2;

    /// <summary>Сколько ждать ответа клиента на закрытие, прежде чем оборвать сокет.</summary>
    public TimeSpan CloseWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Часы, мс (для тестов).</summary>
    public Func<long> Now { get; init; } = () => Environment.TickCount64;
}

/// <summary>
/// Серверный конец <see cref="ITransport"/> поверх WebSocket. Хост (свой поток) шлёт и читает без ожидания; ввод-вывод
/// сокета идёт в <see cref="RunAsync"/>. Очередь отправки ограничена: клиент, который не успевает, отключается (1013).
/// Входящие двоичные сообщения идут через предел частоты; текстовые не принимаются.
/// </summary>
public sealed class WebSocketTransport : ITransport
{
    /// <summary>1013: клиент не успевает (в перечислении .NET кода нет).</summary>
    public const WebSocketCloseStatus TryAgainLater = (WebSocketCloseStatus)1013;

    private readonly WebSocket _socket;
    private readonly TransportLimits _limits;
    private readonly Channel<byte[]> _inbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly TokenBucket _rate, _flood;
    private readonly CancellationTokenSource _abort = new();
    private long _queuedBytes;
    private int _queued, _sent;
    private int _closeStatus;
    private string? _closeReason;

    public WebSocketTransport(WebSocket socket, TransportLimits? limits = null)
    {
        _socket = socket;
        _limits = limits ?? new TransportLimits();
        _rate = new TokenBucket(_limits.Rate, _limits.Burst, _limits.Now);
        _flood = new TokenBucket(_limits.FloodRate, _limits.FloodBurst, _limits.Now);
    }

    /// <summary>Отброшено входящих по пределу частоты.</summary>
    public int Dropped => Volatile.Read(ref _dropped);
    private int _dropped;

    /// <summary>Причина, с которой сервер закрыл подключение; null — закрыл клиент или хост без причины.</summary>
    public string? CloseReason => Volatile.Read(ref _closeReason);

    public bool Closed => _inbox.Reader.Completion.IsCompleted;

    /// <summary>Сообщений ждёт отправки.</summary>
    public int Queued => Volatile.Read(ref _queued);

    public void Send(byte[] message)
    {
        if (_abort.IsCancellationRequested || _outbox.Reader.Completion.IsCompleted)
            return;
        var queued = Interlocked.Increment(ref _queued);
        var bytes = Interlocked.Add(ref _queuedBytes, message.Length);
        if (Interlocked.Increment(ref _sent) > _limits.Free && (queued > _limits.MaxQueued || bytes > _limits.MaxQueuedBytes))
        {
            Unqueue(message);
            Close(TryAgainLater, "slow");
            _abort.Cancel();
            return;
        }
        if (!_outbox.Writer.TryWrite(message))
            Unqueue(message);
    }

    private void Unqueue(byte[] message)
    {
        Interlocked.Decrement(ref _queued);
        Interlocked.Add(ref _queuedBytes, -message.Length);
    }

    public bool TryReceive([NotNullWhen(true)] out byte[]? message) => _inbox.Reader.TryRead(out message);

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancel = default)
    {
        while (await _inbox.Reader.WaitToReadAsync(cancel).ConfigureAwait(false))
            if (_inbox.Reader.TryRead(out var message))
                return message;
        return null;
    }

    /// <summary>
    /// Закрывает подключение с кодом и причиной для клиента: уже отправленное доходит, дальше — закрытие. Не блокирует;
    /// повторный вызов ничего не меняет.
    /// </summary>
    public void Close(WebSocketCloseStatus status, string reason)
    {
        if (Interlocked.CompareExchange(ref _closeStatus, (int)status, 0) != 0)
            return;
        Volatile.Write(ref _closeReason, reason);
        _outbox.Writer.TryComplete();
    }

    /// <summary>Хост отпускает транспорт: очередь досылается, сокет закрывается штатно.</summary>
    public void Dispose() => _outbox.Writer.TryComplete();

    /// <summary>Ввод-вывод сокета; возвращается, когда подключение кончилось (клиент закрыл, предел, ошибка, отмена).</summary>
    public async Task RunAsync(CancellationToken cancel)
    {
        using var sendCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel, _abort.Token);
        var receiving = ReceiveLoopAsync(cancel);
        var sending = SendLoopAsync(sendCancel.Token);
        await sending.ConfigureAwait(false);
        // Своё отправлено и закрытие послано: клиент отвечает закрытием — ждём его, но не дольше.
        if (!receiving.IsCompleted)
            await Task.WhenAny(receiving, Task.Delay(_limits.CloseWait, CancellationToken.None)).ConfigureAwait(false);
        if (!receiving.IsCompleted)
            _socket.Abort();
        await receiving.ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancel)
    {
        try
        {
            var buffer = new byte[16 * 1024];
            var message = new MemoryStream();
            while (_socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var result = await _socket.ReceiveAsync(buffer, cancel).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    Close(WebSocketCloseStatus.InvalidMessageType, "binary only");
                    continue;
                }
                if (_closeStatus != 0)
                    continue;
                if (message.Length + result.Count > _limits.MaxMessage)
                {
                    Close(WebSocketCloseStatus.MessageTooBig, "too big");
                    continue;
                }
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;
                var bytes = message.ToArray();
                message.SetLength(0);
                if (_rate.Take())
                {
                    _inbox.Writer.TryWrite(bytes);
                    continue;
                }
                // Сообщений больше предела: лишние теряются; кто не унимается — отключается.
                Interlocked.Increment(ref _dropped);
                if (!_flood.Take())
                    Close(WebSocketCloseStatus.PolicyViolation, "flood");
            }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException)
        {
        }
        finally
        {
            // Входящий конец закрыт: хост, дочитав, увидит Closed. Исходящий — досылается и закрывается.
            _inbox.Writer.TryComplete();
            _outbox.Writer.TryComplete();
        }
    }

    private async Task SendLoopAsync(CancellationToken cancel)
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(cancel).ConfigureAwait(false))
            {
                await _socket.SendAsync(message, WebSocketMessageType.Binary, true, cancel).ConfigureAwait(false);
                Unqueue(message);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException)
        {
            // Сокет не принимает (клиент не читает, оборвался): приём тоже кончится.
            _inbox.Writer.TryComplete();
            return;
        }
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        var status = Volatile.Read(ref _closeStatus) is var code and not 0 ? (WebSocketCloseStatus)code : WebSocketCloseStatus.NormalClosure;
        using var timeout = new CancellationTokenSource(_limits.CloseWait);
        try
        {
            if (_socket.State == WebSocketState.CloseReceived)
                await _socket.CloseAsync(status, CloseReason ?? "", timeout.Token).ConfigureAwait(false);
            else
                await _socket.CloseOutputAsync(status, CloseReason ?? "", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException)
        {
        }
    }
}
