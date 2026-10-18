using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Kharos.Protocol;

/// <summary>
/// Транспорт сообщений: каждое сообщение — массив байт целиком. Сейчас — в памяти (одиночная игра, Р10), с Н4 —
/// WebSocket. Отправка не блокирует; приём — опросом (<see cref="TryReceive"/>) или ожиданием.
/// </summary>
public interface ITransport : IDisposable
{
    /// <summary>Отправить; массив переходит транспорту — менять его после нельзя. Закрыт — молча.</summary>
    void Send(byte[] message);

    /// <summary>Следующее пришедшее, если есть.</summary>
    bool TryReceive([NotNullWhen(true)] out byte[]? message);

    /// <summary>Ждать сообщения; null — та сторона закрыла транспорт.</summary>
    ValueTask<byte[]?> ReceiveAsync(CancellationToken cancel = default);

    /// <summary>Та сторона закрыла транспорт, и пришедшее разобрано.</summary>
    bool Closed { get; }
}

/// <summary>Транспорт в памяти: пара концов на двух каналах. Потокобезопасен: концы живут в разных потоках.</summary>
public sealed class MemoryTransport : ITransport
{
    private readonly ChannelReader<byte[]> _inbox;
    private readonly ChannelWriter<byte[]> _outbox;

    private MemoryTransport(ChannelReader<byte[]> inbox, ChannelWriter<byte[]> outbox)
    {
        _inbox = inbox;
        _outbox = outbox;
    }

    /// <summary>Два связанных конца: что шлёт один, получает другой.</summary>
    public static (MemoryTransport Host, MemoryTransport Client) Pair()
    {
        var options = new UnboundedChannelOptions { SingleReader = true, SingleWriter = true };
        var toClient = Channel.CreateUnbounded<byte[]>(options);
        var toHost = Channel.CreateUnbounded<byte[]>(options);
        return (new MemoryTransport(toHost.Reader, toClient.Writer), new MemoryTransport(toClient.Reader, toHost.Writer));
    }

    /// <summary>Сколько сообщений ждёт разбора.</summary>
    public int Pending => _inbox.CanCount ? _inbox.Count : 0;

    public bool Closed => _inbox.Completion.IsCompleted;

    public void Send(byte[] message) => _outbox.TryWrite(message);

    public bool TryReceive([NotNullWhen(true)] out byte[]? message) => _inbox.TryRead(out message);

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancel = default)
    {
        while (await _inbox.WaitToReadAsync(cancel).ConfigureAwait(false))
            if (_inbox.TryRead(out var message))
                return message;
        return null;
    }

    /// <summary>Закрывает свою сторону: та, дочитав, увидит <see cref="Closed"/>.</summary>
    public void Dispose() => _outbox.TryComplete();
}
