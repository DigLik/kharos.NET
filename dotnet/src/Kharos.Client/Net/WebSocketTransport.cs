using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Kharos.Protocol;

namespace Kharos.Client.Net;

/// <summary>Не удалось подключиться: сервер недоступен или не принял рукопожатие (<see cref="StatusCode"/> — ответ HTTP, если был).</summary>
public sealed class TransportException(string message, HttpStatusCode? statusCode = null, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>Настройки соединения; пределы защищают клиента от сервера, а не наоборот.</summary>
public sealed class WebSocketTransportOptions
{
    /// <summary>Самое большое принимаемое сообщение; больше — соединение рвётся (код 1009).</summary>
    public int MaxMessageSize { get; init; } = 16 * 1024 * 1024;

    /// <summary>Сколько байт может ждать отправки; больше — сервер не успевает, соединение рвётся.</summary>
    public long MaxPendingSendBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Сколько ждать рукопожатия.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>permessage-deflate (RFC 7692); сервер может отказаться, тогда без сжатия.</summary>
    public bool Compression { get; init; } = true;

    /// <summary>Период пингов; 0 — без них.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Сколько ждать ответа на пинг, прежде чем счесть соединение мёртвым.</summary>
    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Сколько Dispose ждёт, пока уйдёт отправленное и закроется сокет.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Транспорт клиента поверх WebSocket (Н4): каждое двоичное сообщение — один кадр, как у <see cref="MemoryTransport"/>.
/// Приём и отправка идут в фоне (по одной задаче), очереди потокобезопасны: <c>Send</c> не блокирует, <c>TryReceive</c>
/// опрашивается из любого потока. Переподключения нет — это решает оболочка: закрытый транспорт заменяют новым.
/// Закрытие любой стороной или сбой завершают приём: после разбора пришедшего <see cref="Closed"/> истинно, причина —
/// в <see cref="CloseReason"/>, <see cref="CloseStatus"/> и <see cref="Error"/>. Текстовые кадры и сообщения больше
/// <see cref="WebSocketTransportOptions.MaxMessageSize"/> — нарушение протокола, соединение закрывается.
/// </summary>
public sealed class WebSocketTransport : ITransport
{
    private const int InitialBuffer = 16 * 1024;
    private const int KeepBuffer = 1024 * 1024;

    private readonly ClientWebSocket _socket;
    private readonly WebSocketTransportOptions _options;
    private readonly Channel<byte[]> _inbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task _receiver, _sender;
    private long _pendingBytes;
    private int _disposed;
    private volatile string? _reason;
    private volatile Exception? _error;
    private int _status = -1;

    private WebSocketTransport(ClientWebSocket socket, WebSocketTransportOptions options)
    {
        _socket = socket;
        _options = options;
        _receiver = Task.Run(ReceiveLoop);
        _sender = Task.Run(SendLoop);
    }

    /// <summary>Адрес с параметрами запроса (значения кодируются; null и пустые пропускаются); прежние параметры сохраняются.</summary>
    public static Uri WithQuery(Uri url, IEnumerable<KeyValuePair<string, string?>> query)
    {
        var builder = new UriBuilder(url);
        var text = new StringBuilder(builder.Query.TrimStart('?'));
        foreach (var (key, value) in query)
        {
            if (string.IsNullOrEmpty(value))
                continue;
            if (text.Length > 0)
                text.Append('&');
            text.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }
        builder.Query = text.ToString();
        return builder.Uri;
    }

    /// <summary>Подключается и возвращает работающий транспорт; ошибка или таймаут — <see cref="TransportException"/>.</summary>
    public static async Task<WebSocketTransport> ConnectAsync(Uri url, WebSocketTransportOptions? options = null, CancellationToken cancel = default)
    {
        options ??= new WebSocketTransportOptions();
        // Адрес в сообщениях — без запроса: в нём пароль.
        var where = url.GetLeftPart(UriPartial.Path);
        if (url.Scheme is not ("ws" or "wss"))
            throw new TransportException($"адрес сервера должен быть ws:// или wss://: {where}");
        var socket = new ClientWebSocket();
        // Без этого код ответа отказа рукопожатия (403 и т. п.) недоступен.
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = options.KeepAlive;
        if (options.KeepAlive > TimeSpan.Zero)
            socket.Options.KeepAliveTimeout = options.KeepAliveTimeout;
        if (options.Compression)
            socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(options.ConnectTimeout);
        try
        {
            await socket.ConnectAsync(url, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            HttpStatusCode? status = socket.HttpStatusCode is 0 ? null : socket.HttpStatusCode;
            socket.Dispose();
            if (error is OperationCanceledException && cancel.IsCancellationRequested)
                throw;
            var why = error is OperationCanceledException ? $"нет ответа за {options.ConnectTimeout.TotalSeconds:0.#} с"
                : status is { } code ? $"сервер ответил {(int)code} {code}" : (error.InnerException ?? error).Message;
            throw new TransportException($"не удалось подключиться к {where}: {why}", status, error);
        }
        return new WebSocketTransport(socket, options);
    }

    /// <summary>Код из Close-кадра сервера; null — Close не приходил (оборвано или закрыто нами).</summary>
    public WebSocketCloseStatus? CloseStatus => _status < 0 ? null : (WebSocketCloseStatus)_status;

    /// <summary>Причина закрытия: текст из Close-кадра сервера (отказ входа — здесь) либо описание сбоя; null, пока открыт.</summary>
    public string? CloseReason => _reason;

    /// <summary>Исключение, оборвавшее соединение (не штатное закрытие).</summary>
    public Exception? Error => _error;

    public bool Closed => _inbox.Reader.Completion.IsCompleted;

    public void Send(byte[] message)
    {
        if (Volatile.Read(ref _disposed) != 0 || _reason is not null)
            return;
        if (Interlocked.Add(ref _pendingBytes, message.Length) > _options.MaxPendingSendBytes)
        {
            Fail(WebSocketCloseStatus.PolicyViolation, "очередь отправки переполнена", null);
            return;
        }
        if (!_outbox.Writer.TryWrite(message))
            Interlocked.Add(ref _pendingBytes, -message.Length);
    }

    public bool TryReceive([NotNullWhen(true)] out byte[]? message) => _inbox.Reader.TryRead(out message);

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancel = default)
    {
        while (await _inbox.Reader.WaitToReadAsync(cancel).ConfigureAwait(false))
            if (_inbox.Reader.TryRead(out var message))
                return message;
        return null;
    }

    /// <summary>Отправляет оставшееся, закрывает сокет (ждёт не дольше CloseTimeout на каждый шаг) и освобождает ресурсы. Повторно — ничего.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        // Отправитель допишет очередь и сам закроет исходящую сторону.
        _outbox.Writer.TryComplete();
        WaitQuietly(_sender);
        // Сервер отвечает на Close — приём кончается сам; отмена до ответа оборвала бы сокет без рукопожатия.
        WaitQuietly(_receiver);
        _cancel.Cancel();
        WaitQuietly(_receiver);
        _reason ??= "закрыто клиентом";
        _inbox.Writer.TryComplete();
        _socket.Dispose();
        _cancel.Dispose();
    }

    private void WaitQuietly(Task task)
    {
        try
        {
            task.Wait(_options.CloseTimeout);
        }
        catch (AggregateException)
        {
        }
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[InitialBuffer];
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    // Буфер растёт до предела + 1 байт: лишний байт отличает сообщение ровно в предел от длиннее.
                    Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, (long)_options.MaxMessageSize + 1));
                }
                var result = await _socket.ReceiveAsync(buffer.AsMemory(length), _cancel.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _status = (int?)_socket.CloseStatus ?? -1;
                    _reason ??= string.IsNullOrEmpty(_socket.CloseStatusDescription)
                        ? $"сервер закрыл соединение ({_socket.CloseStatus?.ToString() ?? "без кода"})"
                        : _socket.CloseStatusDescription;
                    // Ответный Close: закрытие — рукопожатие. Приём на этом кончен.
                    _inbox.Writer.TryComplete();
                    await CloseOutput(WebSocketCloseStatus.NormalClosure, null).ConfigureAwait(false);
                    return;
                }
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    Fail(WebSocketCloseStatus.InvalidMessageType, "текстовый кадр: протокол двоичный", null);
                    return;
                }
                length += result.Count;
                if (length > _options.MaxMessageSize)
                {
                    Fail(WebSocketCloseStatus.MessageTooBig, $"сообщение длиннее {_options.MaxMessageSize} байт", null);
                    return;
                }
                if (!result.EndOfMessage)
                    continue;
                _inbox.Writer.TryWrite(buffer.AsSpan(0, length).ToArray());
                length = 0;
                if (buffer.Length > KeepBuffer)
                    buffer = new byte[InitialBuffer];
            }
        }
        catch (OperationCanceledException)
        {
            _reason ??= "закрыто клиентом";
        }
        catch (Exception error)
        {
            Fail(null, (error.InnerException ?? error).Message, error);
        }
        finally
        {
            _inbox.Writer.TryComplete();
            _outbox.Writer.TryComplete();
        }
    }

    private async Task SendLoop()
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(_cancel.Token).ConfigureAwait(false))
            {
                Interlocked.Add(ref _pendingBytes, -message.Length);
                if (_socket.State != WebSocketState.Open)
                    continue;
                await _socket.SendAsync(message, WebSocketMessageType.Binary, true, _cancel.Token).ConfigureAwait(false);
            }
            // Очередь кончилась: Dispose (закрываем исходящую сторону) или приём уже завершён.
            if (Volatile.Read(ref _disposed) != 0)
                await CloseOutput(WebSocketCloseStatus.NormalClosure, null).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Fail(null, (error.InnerException ?? error).Message, error);
        }
    }

    /// <summary>Рвёт соединение из-за нарушения или сбоя: запоминает причину, завершает очереди, шлёт Close и отменяет приём.</summary>
    private void Fail(WebSocketCloseStatus? status, string reason, Exception? error)
    {
        _reason ??= reason;
        _error ??= error;
        _outbox.Writer.TryComplete();
        _inbox.Writer.TryComplete();
        _ = Abort(status, reason);
    }

    private async Task Abort(WebSocketCloseStatus? status, string reason)
    {
        try
        {
            if (status is { } code)
                await CloseOutput(code, reason).ConfigureAwait(false);
            await _cancel.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task CloseOutput(WebSocketCloseStatus status, string? description)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
            limit.CancelAfter(_options.CloseTimeout);
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _socket.CloseOutputAsync(status, description, limit.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Сокет уже закрыт или оборван.
        }
    }
}
