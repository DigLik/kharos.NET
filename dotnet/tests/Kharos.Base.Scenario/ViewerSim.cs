using Kharos.Host;
using Kharos.Protocol;

namespace Kharos.Base.Scenario;

// Зрители партии (docs/dotnet-n6.md, Н6.3): клиент игрока на реплике хоста сценария. С первым зрителем у хоста
// появляется сессия (HostSession): изменения тика уходят клиентам настоящим путём протокола (VisionView, дельты по
// MemoryTransport), приказы клиентов — хосту перед тиком. Без зрителей Sim идёт как прежде (Host.TakeDelta).
public sealed partial class Sim
{
    private HostSession? _session;
    private readonly List<Viewer> _viewers = [];

    /// <summary>Сессия хоста для зрителей; null — зрителей ещё не было.</summary>
    public HostSession? Session => _session;

    /// <summary>Живые зрители партии.</summary>
    public IReadOnlyList<Viewer> Viewers => _viewers;

    /// <summary>
    /// Клиент игрока <paramref name="player"/> на реплике этой партии: <c>ClientGame</c> в режиме CLR (или
    /// <c>KHAROS_MODS=wasm</c> — client.wasm), без окна и звука. Игрок входит, если ещё не входил. Клиентская часть не
    /// собрана — <see cref="SimUnavailableException"/> (тесту — <c>Assert.SkipWhen(Viewer.Unavailable(mode) is not null, …)</c>).
    /// </summary>
    public Viewer Viewer(uint player = 1, ViewerOptions? options = null)
    {
        var viewer = new Viewer(this, player, options ?? new ViewerOptions());
        _viewers.Add(viewer);
        return viewer;
    }

    /// <summary>Подключение клиента к сессии: приветствие, карта, снимок — сразу в транспорт.</summary>
    internal Connection Connect(ITransport transport, uint player)
    {
        _session ??= new HostSession(Host, new HostSessionOptions { Local = true, SendRate = 0 });
        var connection = _session.Connect(transport, player);
        // Connect сам разослал накопленное: сигналы этой рассылки — в Signals, как у обычного тика.
        if (_session.LastDelta is { } delta)
            foreach (var signal in delta.Signals)
                Signals.Add(new SimSignal(Host.Tick, signal.Name, signal.Payload, signal.To, signal.At));
        Throw();
        return connection;
    }

    internal void Disconnect(Viewer viewer, Connection connection)
    {
        _viewers.Remove(viewer);
        _session?.Disconnect(connection);
    }

    /// <summary>Сообщения клиентов (приказы, экран) — хосту: приказы выполнятся в начале тика или в Flush.</summary>
    private void PumpViewers() => _session?.PumpMessages();

    /// <summary>Изменения хоста: со зрителями — через рассылку сессии (она и забирает их у хоста).</summary>
    private WorldDelta TakeDelta()
    {
        if (_session is null)
            return Host.TakeDelta();
        _session.Broadcast();
        return _session.LastDelta!;
    }

    private void CloseViewers()
    {
        foreach (var viewer in _viewers.ToList())
            viewer.Dispose();
    }
}
