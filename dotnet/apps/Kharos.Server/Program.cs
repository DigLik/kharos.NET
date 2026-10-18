using System.Runtime.InteropServices;
using Kharos.Server;

// Выделенный сервер Kharos. Использование: kharos-server [--settings путь] [--mods папка]... [--game партия]
// Настройки — settings.json рядом (создаётся со всеми умолчаниями); PORT, SAVE, SETTINGS сильнее файла.
// Моды: папка mods рядом с программой, затем --mods и settings.mods. Консоль: строки stdin (help, players, status, save, kick, stop).

Console.OutputEncoding = System.Text.Encoding.UTF8;

string? settingsPath = null, game = null;
var modRoots = new List<string> { Path.Combine(AppContext.BaseDirectory, "mods") };
for (var at = 0; at < args.Length; at++)
{
    switch (args[at])
    {
        case "--settings" when at + 1 < args.Length:
            settingsPath = args[++at];
            break;
        case "--game" when at + 1 < args.Length:
            game = args[++at];
            break;
        case "--mods" when at + 1 < args.Length:
            modRoots.Add(args[++at]);
            break;
        default:
            Console.Error.WriteLine("Использование: kharos-server [--settings путь] [--mods папка]... [--game партия]");
            return 2;
    }
}

GameServer server;
try
{
    var settings = ServerSettings.Load(settingsPath, name => Environment.GetEnvironmentVariable(name), Console.WriteLine);
    server = await GameServer.StartAsync(new ServerOptions { Settings = settings, ModRoots = modRoots, Game = game });
}
catch (Exception error) when (error is SettingsException or Kharos.Host.HostException or IOException or System.Net.Sockets.SocketException or InvalidOperationException)
{
    Console.Error.WriteLine($"Kharos не запустился: {error.Message}");
    return 1;
}

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    stop.TrySetResult();
});

// Консоль сервера: строка stdin — команда. Под systemd или docker без терминала stdin пуст, и цикл просто кончается.
_ = Task.Run(async () =>
{
    while (await Console.In.ReadLineAsync() is { } line)
    {
        if (line.Trim().TrimStart('/').Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            stop.TrySetResult();
            return;
        }
        try
        {
            foreach (var text in await server.CommandAsync(line))
                Console.WriteLine(text);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        {
            Console.WriteLine($"команда не выполнена: {error.Message}");
        }
    }
});

await Task.WhenAny(stop.Task, server.HostFinished);
var failed = server.Loop.Failure is not null;
await server.DisposeAsync();
return failed ? 1 : 0;
