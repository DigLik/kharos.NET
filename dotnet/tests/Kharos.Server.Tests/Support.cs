using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Hello;
using Kharos.Engine.World;
using Kharos.Protocol;
using MemoryPack;

namespace Kharos.Server.Tests;

/// <summary>Пути к модам и временные папки тестов.</summary>
public static class Env
{
    /// <summary>Папка dotnet/ (где Kharos.slnx); KHAROS_DOTNET_ROOT — явно.</summary>
    public static string DotnetRoot
    {
        get
        {
            if (Environment.GetEnvironmentVariable("KHAROS_DOTNET_ROOT") is { Length: > 0 } root)
                return root;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Kharos.slnx")))
                    return dir.FullName;
            throw new DirectoryNotFoundException("не найден Kharos.slnx выше " + AppContext.BaseDirectory);
        }
    }

    public static string HelloDir => Path.Combine(DotnetRoot, "mods", "hello");
    public static string BaseDir => Path.Combine(DotnetRoot, "mods", "base");

    public static bool HelloBuilt => File.Exists(Path.Combine(HelloDir, "host.wasm"));
    public static bool BaseBuilt => File.Exists(Path.Combine(BaseDir, "host.wasm"));

    public const string NotBuilt = "моды не собраны: pwsh dotnet/tools/build-mods.ps1 (в WSL — dotnet publish mods/<m>/<P>.csproj -p:KharosWasm=true)";

    /// <summary>Свой временный каталог теста; удаляется вместе с ним.</summary>
    public sealed class Temp : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kharos-server-" + Guid.NewGuid().ToString("N")[..8])).FullName;

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>Запуск настоящего сервера на свободном порту localhost.</summary>
public static class TestServer
{
    /// <summary>Настройки: умолчания с правкой <paramref name="tweak"/> (дерево settings.json); save — в каталоге теста.</summary>
    public static ServerSettings Settings(Env.Temp temp, Action<JsonObject>? tweak = null, Dictionary<string, string>? env = null)
    {
        var defaults = ServerSettings.Defaults(seed: 7);
        var tree = (JsonObject)defaults.DeepClone();
        tree["save"] = temp.File("save.kharos");
        tweak?.Invoke(tree);
        return ServerSettings.From(tree, defaults, name => env is not null && env.TryGetValue(name, out var value) ? value : null);
    }

    public static Task<GameServer> StartAsync(Env.Temp temp, Action<JsonObject>? tweak = null, Action<ServerOptionsBuilder>? options = null, string? modDir = null)
    {
        var builder = new ServerOptionsBuilder { ModRoots = [modDir ?? Env.HelloDir] };
        options?.Invoke(builder);
        return GameServer.StartAsync(new ServerOptions
        {
            Settings = Settings(temp, tweak),
            ModRoots = builder.ModRoots,
            Port = 0,
            Loopback = true,
            Log = builder.Log ?? (_ => { }),
            // Предел вызова мода — секунда, а не 50 мс игры: на медленных машинах холодный первый вызов не успевает.
            Modules = new Kharos.Plugins.Game.GameModuleOptions { CallTimeoutMs = 1000 },
            Loop = builder.Loop ?? new HostLoopOptions { ReportEvery = TimeSpan.Zero },
            Transport = builder.Transport ?? new TransportLimits(),
            CertificateCheck = Timeout.InfiniteTimeSpan,
            Filter = builder.Filter,
            Session = builder.Session,
        });
    }
}

public sealed class ServerOptionsBuilder
{
    public IReadOnlyList<string> ModRoots { get; set; } = [];
    public Action<string>? Log { get; set; }
    public HostLoopOptions? Loop { get; set; }
    public TransportLimits? Transport { get; set; }
    public Func<uint, Kharos.Protocol.IViewFilter>? Filter { get; set; }
    public Func<Kharos.Host.HostSessionOptions, Kharos.Host.HostSessionOptions>? Session { get; set; }
}

/// <summary>Клиент протокола поверх ClientWebSocket: сообщения собираются в фоне, реплика применяется в потоке теста.</summary>
public sealed class TestClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly ConcurrentQueue<byte[]> _inbox = new();
    private readonly Task _reader;

    private TestClient(ClientWebSocket socket)
    {
        _socket = socket;
        _reader = Task.Run(ReadAsync);
    }

    public Replica Replica { get; } = new();

    /// <summary>Код закрытия, присланный сервером, и его причина; пока null — не закрыто.</summary>
    public WebSocketCloseStatus? CloseStatus { get; private set; }
    public string? CloseReason { get; private set; }

    public bool Finished => _reader.IsCompleted;
    public WebSocketState State => _socket.State;

    public static async Task<TestClient> ConnectAsync(GameServer server, string? id = "a", string? name = null, string? password = null, int? version = null,
        string? forwarded = null, Func<X509Certificate2?, bool>? certificate = null)
    {
        var query = new List<string>();
        // Жетон игрока - от 8 до 64 знаков [0-9A-Za-z_-]: короткие имена тестов дополняются.
        if (id is not null)
            query.Add("id=" + Uri.EscapeDataString(id.PadRight(8, '_')));
        if (name is not null)
            query.Add("name=" + Uri.EscapeDataString(name));
        if (password is not null)
            query.Add("password=" + Uri.EscapeDataString(password));
        query.Add("version=" + (version ?? Welcome.ProtocolVersion));
        var socket = new ClientWebSocket();
        socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
        if (forwarded is not null)
            socket.Options.SetRequestHeader("X-Forwarded-For", forwarded);
        if (certificate is not null)
            socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => certificate(cert is null ? null : new X509Certificate2(cert));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await socket.ConnectAsync(new Uri($"{(server.Secure ? "wss" : "ws")}://127.0.0.1:{server.Port}/?{string.Join('&', query)}"), timeout.Token);
        return new TestClient(socket);
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            var message = new MemoryStream();
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    CloseStatus = result.CloseStatus;
                    CloseReason = result.CloseStatusDescription;
                    try
                    {
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    }
                    catch (Exception error) when (error is WebSocketException or ObjectDisposedException)
                    {
                    }
                    return;
                }
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    _inbox.Enqueue(message.ToArray());
                    message.SetLength(0);
                }
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            CloseStatus ??= WebSocketCloseStatus.ProtocolError;
            CloseReason ??= "обрыв";
        }
    }

    /// <summary>Применяет пришедшее к реплике; сколько сообщений.</summary>
    public int Pump()
    {
        var count = 0;
        while (_inbox.TryDequeue(out var message))
        {
            Replica.Apply(message);
            count++;
        }
        return count;
    }

    /// <summary>Ждёт условия, применяя сообщения по мере прихода.</summary>
    public async Task WaitAsync(Func<bool> condition, string what, int milliseconds = 20000)
    {
        var limit = Environment.TickCount64 + milliseconds;
        while (true)
        {
            Pump();
            if (condition())
                return;
            if (Environment.TickCount64 > limit)
                throw new TimeoutException($"не дождались: {what} (закрыто: {CloseStatus} {CloseReason})");
            await Task.Delay(10);
        }
    }

    /// <summary>Ждёт закрытия сервером; возвращает код и причину.</summary>
    public async Task<(WebSocketCloseStatus Status, string Reason)> ClosedAsync(int milliseconds = 20000)
    {
        await _reader.WaitAsync(TimeSpan.FromMilliseconds(milliseconds));
        return (CloseStatus ?? WebSocketCloseStatus.Empty, CloseReason ?? "");
    }

    /// <summary>Тексты отказа, пришедшие сообщением Refused.</summary>
    public List<string> Refusals()
    {
        Pump();
        return [.. Replica.Inbox.Where(m => m[0] == (byte)MessageType.Refused).Select(m => Messages.ReadRefused(m).Reason)];
    }

    public Task SendAsync(byte[] message) => _socket.SendAsync(message, WebSocketMessageType.Binary, true, CancellationToken.None);

    public Task SendOrderAsync(string name, byte[] payload) => SendAsync(DeltaCodec.Order(name, payload));

    /// <summary>Юниты мода hello у игрока в реплике.</summary>
    public List<uint> Units(uint? player = null)
    {
        var world = Replica.World!;
        var drive = world.Schema.Id("Drive");
        var units = new List<uint>();
        world.Query([drive, Builtin.Owner], units);
        if (player is { } owner)
            units.RemoveAll(entity => world.Number(entity, Builtin.Owner, 0) != owner);
        units.Sort();
        return units;
    }

    public (double X, double Y) Position(uint entity) =>
        (Replica.World!.Number(entity, Builtin.Position, 0), Replica.World.Number(entity, Builtin.Position, 1));

    public static byte[] Spawn(int count, double x, double y, double spread = 1) =>
        MemoryPackSerializer.Serialize(new SpawnOrder { Count = count, X = x, Y = y, Spread = spread });

    public static byte[] Move(double x, double y, params uint[] entities) =>
        MemoryPackSerializer.Serialize(new MoveOrder { Entities = entities, X = x, Y = y });

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        _socket.Abort();
        try
        {
            await _reader.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
        }
        _socket.Dispose();
    }
}

/// <summary>Сокет, которым управляет тест: приём из очереди, отправка с задержкой по желанию.</summary>
public sealed class FakeWebSocket : WebSocket
{
    private readonly System.Threading.Channels.Channel<(WebSocketMessageType Type, byte[] Data, bool End)> _incoming = System.Threading.Channels.Channel.CreateUnbounded<(WebSocketMessageType, byte[], bool)>();
    private WebSocketState _state = WebSocketState.Open;

    /// <summary>Пока не открыт, SendAsync зависает (клиент не читает).</summary>
    public TaskCompletionSource SendGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<byte[]> Sent { get; } = new();
    public WebSocketCloseStatus? ClosedWith { get; private set; }
    public string? ClosedReason { get; private set; }
    public bool Aborted { get; private set; }

    public FakeWebSocket(bool sendOpen = true)
    {
        if (sendOpen)
            SendGate.SetResult();
    }

    public void Feed(byte[] data, WebSocketMessageType type = WebSocketMessageType.Binary, bool end = true) => _incoming.Writer.TryWrite((type, data, end));

    /// <summary>Клиент закрыл сокет.</summary>
    public void ClientClose() => _incoming.Writer.TryWrite((WebSocketMessageType.Close, [], true));

    public override WebSocketCloseStatus? CloseStatus => ClosedWith;
    public override string? CloseStatusDescription => ClosedReason;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;

    public override void Abort()
    {
        Aborted = true;
        _state = WebSocketState.Aborted;
        _incoming.Writer.TryComplete();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        ClosedWith = closeStatus;
        ClosedReason = statusDescription;
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        ClosedWith = closeStatus;
        ClosedReason = statusDescription;
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    public override void Dispose() => _state = WebSocketState.Closed;

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        var (type, data, end) = await Next(cancellationToken);
        data.CopyTo(buffer.Array!, buffer.Offset);
        return new WebSocketReceiveResult(data.Length, type, end);
    }

    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var (type, data, end) = await Next(cancellationToken);
        data.CopyTo(buffer);
        return new ValueWebSocketReceiveResult(data.Length, type, end);
    }

    private async Task<(WebSocketMessageType Type, byte[] Data, bool End)> Next(CancellationToken cancellationToken)
    {
        if (!await _incoming.Reader.WaitToReadAsync(cancellationToken))
            throw new WebSocketException("сокет оборван");
        var item = await _incoming.Reader.ReadAsync(cancellationToken);
        if (item.Type == WebSocketMessageType.Close)
            _state = WebSocketState.CloseReceived;
        return item;
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
        SendAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken).AsTask();

    public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        await SendGate.Task.WaitAsync(cancellationToken);
        Sent.Enqueue(buffer.ToArray());
    }
}
