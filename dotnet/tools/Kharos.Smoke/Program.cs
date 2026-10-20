using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kharos.Smoke;

// Оркестратор смоука (docs/dotnet-n7.md, Н7.3). Использование:
//   Kharos.Smoke <single|server|mixed|soak|путь.json> [--package папка] [--kharos путь] [--server путь] [--build [--version v]]
//                [--out папка] [--gpu none] [--soak 15m] [--bots 8] [--wsl-server путь-в-linux] [--wsl-distro имя] [--timeout 10m]
// --package — папка упаковки (архивы package.ps1/package.sh или распакованные папки); --build — собрать из исходников.
// mixed — сценарий server с сервером в WSL (--wsl-server), клиенты на Windows через localhost WSL2.
// --soak с любым сценарием сервера — нагрузка (сценарий soak): сервер и --bots ботов на заданное время.
// Код возврата: 0 — всё прошло, 1 — провал, 2 — ошибка запуска, 130 — прерван.

Console.OutputEncoding = System.Text.Encoding.UTF8;
CliOptions cli;
try
{
    cli = CliOptions.Parse(args);
}
catch (FormatException error)
{
    Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine(CliOptions.Usage);
    return 2;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Ctrl+C: не умирать сразу — сначала убить всё запущенное (finally ниже).
    e.Cancel = true;
    cancel.Cancel();
};

void Log(string text) => Console.WriteLine(text);

using var group = new ProcessGroup();
Runner? runner = null;
var code = 1;
var runDir = cli.Out ?? Path.Combine(Path.GetTempPath(), "kharos-smoke", $"{cli.Scenario}-{DateTime.Now:yyyyMMdd-HHmmss}");
try
{
    Directory.CreateDirectory(runDir);
    var (kharos, server) = Locate(cli, runDir, Log);
    var scenarioName = cli.Soak is not null ? "soak" : cli.Scenario == "mixed" ? "server" : cli.Scenario;
    if (cli.Scenario == "mixed" && cli.WslServer is null)
        throw new FormatException("mixed: нужен --wsl-server (kharos-server linux-x64 внутри WSL)");
    var scenario = Scenario.Load(Scenario.Find(scenarioName));
    var options = new RunOptions
    {
        ScenarioPath = scenario.Dir,
        RunDir = runDir,
        Kharos = kharos,
        Server = server,
        WslServer = cli.WslServer,
        WslDistro = cli.WslDistro,
        NoGpu = cli.NoGpu,
        Soak = cli.Soak ?? TimeSpan.FromMinutes(15),
        Bots = cli.Bots,
        ClientTimeout = cli.Timeout ?? TimeSpan.FromMinutes(10),
    };
    Log($"смоук «{scenario.Name}»{(cli.Scenario != scenario.Name ? $" ({cli.Scenario})" : "")}: {scenario.About}");
    Log($"  папка прогона: {runDir}");
    Log($"  kharos: {kharos ?? "—"}");
    Log($"  kharos-server: {(cli.WslServer is { } wsl ? $"WSL {cli.WslDistro ?? "(по умолчанию)"}: {wsl}" : server ?? "—")}");
    runner = new Runner(options, scenario, group, Log);
    var ok = await runner.RunAsync(cancel.Token);
    code = ok ? 0 : 1;
}
catch (OperationCanceledException) when (cancel.IsCancellationRequested)
{
    Log("прервано (Ctrl+C)");
    code = 130;
}
catch (Exception error) when (error is FormatException or FileNotFoundException or InvalidOperationException or IOException or StepFailure)
{
    Log($"смоук не запустился: {error.Message}");
    code = 2;
}
finally
{
    var killed = group.KillAll();
    if (killed.Count > 0)
        Log($"убиты: {string.Join(", ", killed)}");
}

if (runner is not null)
{
    var results = runner.Results;
    Log("");
    Log($"итог: {(code == 0 ? "ПРОШЁЛ" : "ПРОВАЛ")}, шагов {results.Count(r => r.Ok)}/{results.Count}, {results.Sum(r => r.Seconds):0} с; отчёты — {runDir}");
    var summary = new JsonObject
    {
        ["ok"] = code == 0,
        ["scenario"] = cli.Scenario,
        ["run"] = runDir,
        ["steps"] = new JsonArray([.. results.Select(r => (JsonNode)new JsonObject
        {
            ["index"] = r.Index, ["step"] = r.Title, ["ok"] = r.Ok, ["seconds"] = Math.Round(r.Seconds, 2), ["note"] = r.Note,
        })]),
    };
    File.WriteAllText(Path.Combine(runDir, "summary.json"), summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
}
return code;

static (string? Kharos, string? Server) Locate(CliOptions cli, string runDir, Action<string> log)
{
    string? root = null;
    if (cli.Build)
    {
        var dotnet = Packages.DotnetRoot() ?? throw new InvalidOperationException("--build: не нашёл dotnet/Kharos.slnx");
        root = Packages.Build(dotnet, cli.Version, runDir, log);
    }
    else if (cli.Package is { } package)
    {
        if (!Directory.Exists(package))
            throw new FileNotFoundException($"--package: нет папки {package}");
        root = Packages.Unpack(Path.GetFullPath(package), Path.Combine(runDir, "unpacked"), log);
    }
    string? Find(string? explicitPath, string name)
    {
        if (explicitPath is not null)
            return Packages.FindExe(explicitPath, name) ?? throw new FileNotFoundException($"нет {Packages.Exe(name)} в {explicitPath}");
        return root is null ? null : Packages.FindExe(root, name);
    }
    return (Find(cli.Kharos, "kharos"), Find(cli.Server, "kharos-server"));
}

/// <summary>Аргументы командной строки.</summary>
internal sealed record CliOptions
{
    public const string Usage =
        "Kharos.Smoke <single|server|mixed|soak|путь.json> [--package папка] [--kharos путь] [--server путь] [--build [--version v]] " +
        "[--out папка] [--gpu none] [--soak 15m] [--bots 8] [--wsl-server путь] [--wsl-distro имя] [--timeout 10m]";

    public string Scenario { get; init; } = "";
    public string? Package { get; init; }
    public string? Kharos { get; init; }
    public string? Server { get; init; }
    public bool Build { get; init; }
    public string Version { get; init; } = "0.2.0";
    public string? Out { get; init; }
    public bool NoGpu { get; init; }
    public TimeSpan? Soak { get; init; }
    public int Bots { get; init; } = 8;
    public string? WslServer { get; init; }
    public string? WslDistro { get; init; }
    public TimeSpan? Timeout { get; init; }

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CliOptions();
        string Value(ref int at) => at + 1 < args.Count ? args[++at] : throw new FormatException($"{args[at]}: нет значения");
        for (var at = 0; at < args.Count; at++)
        {
            var arg = args[at];
            options = arg switch
            {
                "--package" => options with { Package = Value(ref at) },
                "--kharos" => options with { Kharos = Value(ref at) },
                "--server" => options with { Server = Value(ref at) },
                "--build" => options with { Build = true },
                "--version" => options with { Version = Value(ref at) },
                "--out" => options with { Out = Path.GetFullPath(Value(ref at)) },
                "--gpu" => options with { NoGpu = Value(ref at) is "none" ? true : throw new FormatException("--gpu: только none") },
                "--soak" => options with { Soak = Duration.Parse(Value(ref at)) },
                "--bots" => options with { Bots = int.TryParse(Value(ref at), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bots) && bots > 0 ? bots : throw new FormatException("--bots: число больше нуля") },
                "--wsl-server" => options with { WslServer = Value(ref at) },
                "--wsl-distro" => options with { WslDistro = Value(ref at) },
                "--timeout" => options with { Timeout = Duration.Parse(Value(ref at)) },
                _ when arg.StartsWith("--", StringComparison.Ordinal) => throw new FormatException($"нет ключа {arg}"),
                _ when options.Scenario.Length == 0 => options with { Scenario = arg },
                _ => throw new FormatException($"лишний аргумент {arg}"),
            };
        }
        if (options.Scenario.Length == 0)
            throw new FormatException("нет сценария");
        if (options.Scenario == "soak" && options.Soak is null)
            options = options with { Soak = TimeSpan.FromMinutes(15) };
        return options;
    }
}
