using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Kharos.Content;
using Kharos.Host;
using Kharos.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using KHost = Kharos.Host.Host;
using KHostOptions = Kharos.Host.HostOptions;

namespace Kharos.Server;

/// <summary>Как запускать сервер: настройки и то, что в тестах подменяется.</summary>
public sealed class ServerOptions
{
    public required ServerSettings Settings { get; init; }

    /// <summary>Папки с модами: каждая — каталог модов (подпапки с mod.json) или сам мод. Идут до модов из settings.json.</summary>
    public IReadOnlyList<string> ModRoots { get; init; } = [];

    /// <summary>Журнал; по умолчанию — консоль.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Пределы вызовов мода; по умолчанию — как у хоста.</summary>
    public Kharos.Plugins.Game.GameModuleOptions? Modules { get; init; }

    /// <summary>Партия (запись data/games.json); null — по умолчанию из mod.json.</summary>
    public string? Game { get; init; }

    public HostLoopOptions Loop { get; init; } = new();
    public TransportLimits Transport { get; init; } = new();

    /// <summary>Порт вместо <c>Settings.Port</c> (0 — любой свободный: в тестах).</summary>
    public int? Port { get; init; }

    /// <summary>Только петля (127.0.0.1) — в тестах; по умолчанию слушаются все адреса.</summary>
    public bool Loopback { get; init; }

    /// <summary>Как часто перечитывается сертификат tls.files; по умолчанию раз в 12 часов.</summary>
    public TimeSpan? CertificateCheck { get; init; }

    /// <summary>Свой фильтр обзора (в тестах - полный); по умолчанию хост даёт обзор игрока.</summary>
    public Func<uint, IViewFilter>? Filter { get; init; }

    /// <summary>Правка настроек сессии хоста (чат, поток сообщений) поверх собранных из settings.json.</summary>
    public Func<HostSessionOptions, HostSessionOptions>? Session { get; init; }
}

/// <summary>
/// Выделенный сервер: Kestrel с WebSocket (permessage-deflate), привратник по адресам, пароль, версия протокола и
/// <see cref="HostLoop"/>. Подключение: <c>ws://хост:порт/?id=…&amp;name=…&amp;password=…&amp;version=…</c>. Отказ — закрытие
/// кода 1008 с причиной в тексте закрытия: <c>version</c>, <c>password-needed</c>, <c>wrong-password</c>,
/// <c>newcomers</c>; ещё 1013 <c>slow</c> (клиент не успевает), 1008 <c>flood</c>, 1009 (сообщение больше 256 КБ).
/// Пока в протоколе нет сообщения «отказано», причина едет только так. ШОВ: заменится сообщением нового хоста.
/// </summary>
public sealed class GameServer : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly ServerSettings _settings;
    private readonly Action<string> _log;
    private readonly ConnectGate _gate;
    private readonly byte[] _passwordDigest;
    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<WebSocketTransport, string> _links = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly FileCertificate? _certificate;
    private HostLoop _loop = null!;
    private WebApplication _app = null!;
    private int _stopped;

    private GameServer(ServerOptions options)
    {
        _options = options;
        _settings = options.Settings;
        _log = options.Log ?? (text => Console.WriteLine(text));
        _gate = new ConnectGate(_settings.Limits);
        _passwordDigest = SHA256.HashData(Encoding.UTF8.GetBytes(_settings.Password));
        if (_settings.Tls.Mode == "files")
            _certificate = new FileCertificate(_settings.Tls.Cert, _settings.Tls.Key, _log, options.CertificateCheck);
    }

    /// <summary>Файл сохранения мира.</summary>
    public string SavePath => _settings.Save;

    public HostLoop Loop => _loop;

    /// <summary>Оператор попросил остановиться (консольная команда stop).</summary>
    public Task StopRequested => _stopRequested.Task;

    /// <summary>Порт, на котором слушает сервер.</summary>
    public int Port { get; private set; }

    public bool Secure => _certificate is not null;

    /// <summary>Сертификат режима files; в других режимах null.</summary>
    public FileCertificate? Certificate => _certificate;

    /// <summary>Подключения сейчас.</summary>
    public int Clients => _links.Count;

    /// <summary>Остановился ли хост (штатно или ошибкой): тогда пора выходить.</summary>
    public Task HostFinished => _loop.Finished;

    /// <summary>Сервер слушает и хост идёт.</summary>
    public static async Task<GameServer> StartAsync(ServerOptions options)
    {
        var server = new GameServer(options);
        try
        {
            await server.StartCoreAsync().ConfigureAwait(false);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return server;
    }

    private async Task StartCoreAsync()
    {
        if (_settings.Tls.Mode == "auto")
            throw new SettingsException("tls.mode: auto (Let's Encrypt) пока не поддерживается — используйте files (свои cert и key) или off; запуск без шифрования отменён");
        foreach (var note in _settings.Notes)
            _log($"{note} — пропущено");

        var content = LoadContent();
        var seed = _settings.Seed == 0 ? 1 : _settings.Seed;
        var modules = _options.Modules ?? new Kharos.Plugins.Game.GameModuleOptions();
        // Хост создаётся в своём потоке (Wasmtime): сюда уходит только то, что нужно для создания.
        var session = new HostSessionOptions
        {
            // sendRate в настройках - КБ/с на игрока, 0 снимает предел.
            SendRate = _settings.SendRate * 1024,
            AdminPassword = _settings.Admin.Length > 0 ? _settings.Admin : null,
            Log = _log,
        };
        if (_options.Session is { } tweak)
            session = tweak(session);
        _loop = HostLoop.Start(content, new KHostOptions { Game = _options.Game, Seed = seed, Modules = modules, Log = _log }, session, _settings.Save, Setup, _options.Loop, _log);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
            var port = _options.Port ?? _settings.Port;
            void Configure(ListenOptions listen)
            {
                if (_certificate is { } certificate)
                    // Сертификат берётся на каждое рукопожатие: перечитанный подхватывается без переоткрытия порта.
                    listen.UseHttps(new HttpsConnectionAdapterOptions { ServerCertificateSelector = (_, _) => certificate.Current });
            }
            if (_options.Loopback)
                kestrel.Listen(IPAddress.Loopback, port, Configure);
            else
                kestrel.ListenAnyIP(port, Configure);
        });
        _app = builder.Build();
        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(60) });
        _app.Run(HandleAsync);
        await _app.StartAsync().ConfigureAwait(false);
        Port = new Uri(_app.Urls.First().Replace("[::]", "localhost").Replace("0.0.0.0", "localhost")).Port;
        _log($"Kharos слушает {(Secure ? "wss" : "ws")}://localhost:{Port}");
    }

    /// <summary>Настройка сессии в потоке хоста: фильтр обзора и команды консоли сервера.</summary>
    private void Setup(HostSession session)
    {
        if (_options.Filter is { } filter)
            session.Filter = filter;
        session.AddCommand(new HostCommand
        {
            Name = "save", Help = "сохранить мир", Console = true,
            Run = call =>
            {
                call.Caller.Reply("сохраняю...");
                _ = _loop.SaveFromHost(session).ContinueWith(task => call.Caller.Reply(task.IsCompletedSuccessfully ? $"мир сохранён в {SavePath}" : "мир не сохранён"), TaskScheduler.Default);
            },
        });
        session.AddCommand(new HostCommand
        {
            Name = "stop", Help = "сохранить мир и выйти", Console = true,
            Run = call =>
            {
                call.Caller.Reply("останавливаюсь...");
                _stopRequested.TrySetResult();
            },
        });
        session.AddCommand(new HostCommand
        {
            Name = "status", Help = "состояние сервера", Console = true,
            Run = call => call.Caller.Reply($"тик {session.Host.Tick}, сущностей {session.Host.World.Count}, клиентов {session.Connections.Count}, тик хоста {_loop.LastTickMs:0.00} мс, ошибок мода {session.Host.Errors.Count}"),
        });
    }

    /// <summary>Базовые моды рядом с программой и моды из настроек.</summary>
    private GameContent LoadContent()
    {
        var packages = new List<ModPackage>();
        foreach (var root in _options.ModRoots.Concat(_settings.Mods))
        {
            if (!Directory.Exists(root))
            {
                _log($"мод {root}: нет такой папки — пропущен");
                continue;
            }
            // Папка — сам мод (есть mod.json) или каталог модов.
            packages.AddRange(File.Exists(Path.Combine(root, "mod.json")) ? [ModPackage.Read(root)] : ModPackage.Discover(root));
        }
        if (packages.Count == 0)
            throw new SettingsException("нет ни одного мода: положите моды в папку mods рядом с программой или укажите папки в settings.json (mods)");
        try
        {
            return GameContent.Load(packages);
        }
        catch (ContentException error)
        {
            throw new SettingsException($"моды не загрузились: {error.Message}");
        }
    }

    private bool PasswordOk(string password) =>
        _settings.Password.Length == 0 || CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(password)), _passwordDigest);

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        // Перед сервером может стоять nginx: тогда настоящий адрес — в заголовке, который он дописал.
        var address = ClientAddress.Of(context.Connection.RemoteIpAddress, request.Headers["X-Forwarded-For"].LastOrDefault());
        if (!_gate.Admit(address))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsync("Kharos: слишком много подключений с вашего адреса\n").ConfigureAwait(false);
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            await context.Response.WriteAsync("Kharos: сюда подключаются по WebSocket\n").ConfigureAwait(false);
            return;
        }
        var query = request.Query;
        var id = query["id"].FirstOrDefault();
        var name = query["name"].FirstOrDefault();
        var password = query["password"].FirstOrDefault() ?? "";
        var version = int.TryParse(query["version"].FirstOrDefault(), out var parsed) ? parsed : 0;

        // Снимки мира — двоичные, но почти не меняются от тика к тику: deflate ужимает их в разы.
        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { DangerousEnableCompression = true }).ConfigureAwait(false);
        _gate.Opened(address);
        try
        {
            // Клиент другой версии собрал бы мир не так, как сервер: его не пускают, но говорят почему.
            if (version != Welcome.ProtocolVersion)
            {
                _log($"× клиент версии {version}");
                var text = version < Welcome.ProtocolVersion
                    ? $"Игра устарела: сервер версии {Welcome.ProtocolVersion}, у вас {version}. Обновите игру."
                    : $"Сервер устарел: у него версия {Welcome.ProtocolVersion}, у вас {version}.";
                await RefuseAsync(socket, $"version: server {Welcome.ProtocolVersion}, client {version}", text, false).ConfigureAwait(false);
            }
            else if (!PasswordOk(password))
            {
                _log(password.Length == 0 ? "× нужен пароль" : "× неверный пароль");
                await RefuseAsync(socket, password.Length == 0 ? "password-needed" : "wrong-password",
                    password.Length == 0 ? "Нужен пароль" : "Неверный пароль", true).ConfigureAwait(false);
            }
            // Новый игрок занимает место на карте: с одного адреса их заводится немного.
            else if (!await KnowsAsync(id).ConfigureAwait(false) && !_gate.Newcomer(address))
            {
                _log($"× слишком много новых игроков с {address}");
                await RefuseAsync(socket, "newcomers", "Слишком много новых игроков с вашего адреса, попробуйте позже", false).ConfigureAwait(false);
            }
            else
            {
                await ServeAsync(socket, id, name, address).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Closed(address);
        }
    }

    /// <summary>Знает ли хост игрока с таким жетоном (спрашивается в потоке хоста: список игроков не для других потоков).</summary>
    private Task<bool> KnowsAsync(string? token) => string.IsNullOrEmpty(token) ? Task.FromResult(false) : _loop.Invoke(session => session.Knows(token));

    /// <summary>Отказ: сообщение протокола с причиной для игрока, затем закрытие 1008 с кодом причины (для старых клиентов и логов).</summary>
    private static async Task RefuseAsync(WebSocket socket, string code, string text, bool password)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await socket.SendAsync(Messages.Refused(text, password), WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(false);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, code, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException)
        {
        }
    }

    private async Task ServeAsync(WebSocket socket, string? id, string? name, string address)
    {
        var transport = new WebSocketTransport(socket, _options.Transport);
        _links[transport] = address;
        var player = new StrongBox<uint>();
        _loop.Post(session => Volatile.Write(ref player.Value, Connect(session, transport, id, name, address)));
        try
        {
            await transport.RunAsync(_stopping.Token).ConfigureAwait(false);
        }
        finally
        {
            _links.TryRemove(transport, out _);
            transport.Dispose();
            // Закрытый транспорт хост снимает сам при разборе приказов; здесь — запись в журнал.
            _loop.Post(session =>
            {
                if (session.Connections.FirstOrDefault(c => ReferenceEquals(c.Transport, transport)) is { } connection)
                    session.Disconnect(connection);
                _log($"- игрок {(Volatile.Read(ref player.Value) is var number and not 0 ? number : "?")}{(transport.CloseReason is { } reason ? $" ({reason})" : "")}");
            });
        }
    }

    /// <summary>Вход игрока в потоке хоста. Тот же игрок с другого подключения вытесняет прежнее.</summary>
    private uint Connect(HostSession session, WebSocketTransport transport, string? id, string? name, string address)
    {
        if (session.Host.Roster.Find(id) is { } known)
            foreach (var old in session.Connections.Where(c => c.Player == known).ToList())
            {
                if (old.Transport is WebSocketTransport previous)
                    previous.Close(WebSocketCloseStatus.NormalClosure, "replaced");
                session.Disconnect(old);
            }
        if (transport.Closed)
            return 0;
        var connection = session.Join(transport, id, name);
        _log($"+ игрок {connection.Player}{(string.IsNullOrEmpty(name) ? "" : $" «{name}»")} ({address})");
        return connection.Player;
    }

    /// <summary>Выполняет строку консоли как команду администратора (help, list, players, say, kick, status, save, stop...); ответ - строки.</summary>
    public async Task<string[]> CommandAsync(string line)
    {
        line = line.Trim();
        if (line.Length == 0)
            return [];
        if (line.TrimStart('/').Equals("save", StringComparison.OrdinalIgnoreCase))
        {
            await _loop.SaveAsync().ConfigureAwait(false);
            return [$"мир сохранён в {SavePath}"];
        }
        return await _loop.Invoke(session =>
        {
            var replies = new List<string>();
            session.Command(line, new Caller(null, true, replies.Add));
            return replies.ToArray();
        }).ConfigureAwait(false);
    }

    /// <summary>Остановка: подключения закрываются, мир сохраняется, порт закрывается. Повторный вызов ничего не делает.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;
        // Сперва клиентам — штатное закрытие, чтобы они не увидели обрыв.
        foreach (var link in _links.Keys)
            link.Close(WebSocketCloseStatus.EndpointUnavailable, "stop");
        for (var wait = 0; wait < 30 && !_links.IsEmpty; wait++)
            await Task.Delay(100).ConfigureAwait(false);
        // Хост — раньше порта: последнее сохранение, пока подключения ещё закрываются штатно.
        if (_loop is not null)
            await Task.Run(_loop.Dispose).ConfigureAwait(false);
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_app is not null)
        {
            try
            {
                await _app.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        _certificate?.Dispose();
        _stopping.Dispose();
    }
}
