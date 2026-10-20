using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kharos.Smoke;

/// <summary>Настройки прогона.</summary>
internal sealed record RunOptions
{
    public required string ScenarioPath { get; init; }
    public required string RunDir { get; init; }
    public string? Kharos { get; init; }
    public string? Server { get; init; }

    /// <summary>Сервер в WSL: путь к kharos-server внутри Linux и дистрибутив (null — по умолчанию).</summary>
    public string? WslServer { get; init; }
    public string? WslDistro { get; init; }

    /// <summary>KHAROS_GPU=none у клиентов (без окна и рендера; снимки и запись пропускаются).</summary>
    public bool NoGpu { get; init; }

    public TimeSpan Soak { get; init; } = TimeSpan.FromMinutes(15);
    public int Bots { get; init; } = 8;

    /// <summary>Срок клиента по умолчанию, если у шага нет timeout.</summary>
    public TimeSpan ClientTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>Итог шага сценария.</summary>
internal sealed record StepResult(int Index, string Title, bool Ok, double Seconds, string Note);

/// <summary>Исполнитель сценария оркестратора: шаги по очереди, первый провал — стоп; все процессы — в <see cref="ProcessGroup"/>.</summary>
internal sealed class Runner(RunOptions options, Scenario scenario, ProcessGroup group, Action<string> log)
{
    private readonly Vars _vars = new();
    private readonly Dictionary<string, Proc> _clients = new(StringComparer.Ordinal);
    private readonly List<StepResult> _results = [];
    private readonly Dictionary<string, int> _logFrom = new(StringComparer.Ordinal);
    private Proc? _server;
    private int _serverStarts;
    private string? _modsRoot;

    public IReadOnlyList<StepResult> Results => _results;

    /// <summary>Итоги нагрузки (если был шаг soak).</summary>
    public SoakResult? SoakResult { get; private set; }

    private string Run => options.RunDir;

    public async Task<bool> RunAsync(CancellationToken cancel)
    {
        Directory.CreateDirectory(Run);
        _vars["run"] = Run;
        foreach (var (key, value) in scenario.Values)
            _vars[key] = value;
        PrepareMods();
        foreach (var step in scenario.Steps)
        {
            cancel.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            string note;
            bool ok;
            try
            {
                note = await StepAsync(step, cancel).ConfigureAwait(false);
                ok = true;
            }
            catch (StepFailure failure)
            {
                (ok, note) = (false, failure.Message);
            }
            catch (Exception error) when (error is FormatException or KeyNotFoundException or IOException or InvalidOperationException or JsonException or UnauthorizedAccessException)
            {
                (ok, note) = (false, $"{error.GetType().Name}: {error.Message}");
            }
            var result = new StepResult(step.Index, step.Title, ok, watch.Elapsed.TotalSeconds, note);
            _results.Add(result);
            log($"{(ok ? "  ok " : "FAIL ")} [{step.Index}] {step.Title} — {result.Seconds:0.0} с{(note.Length > 0 ? ": " + note : "")}");
            if (!ok)
                return false;
        }
        return true;
    }

    /// <summary>Моды смоука (mods сценария) — копией в папку прогона: её получают сервер (--mods) и одиночная игра (KHAROS_MODS).</summary>
    private void PrepareMods()
    {
        if (scenario.Mods.Count == 0)
            return;
        _modsRoot = Path.Combine(Run, "mods");
        foreach (var mod in scenario.Mods)
        {
            var source = Path.Combine(AppContext.BaseDirectory, "mods", mod);
            if (!Directory.Exists(source))
                throw new StepFailure($"нет мода смоука {mod} ({source})");
            Copy(source, Path.Combine(_modsRoot, mod));
        }
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(from))
            Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    private Task<string> StepAsync(ScenarioStep step, CancellationToken cancel) => step.Do switch
    {
        "server" => StartServerAsync(step, cancel),
        "client" => ClientAsync(step, cancel),
        "wait" => WaitClientAsync(Client(step.Need("name")), step, cancel),
        "kill" => Task.FromResult(Kill(step)),
        "console" => ConsoleAsync(step, cancel),
        "expect" => ExpectAsync(step, step.Need("pattern"), 0, cancel),
        "stop" => StopAsync(step, cancel),
        "file" => Task.FromResult(FileExists(step)),
        "assert" => Task.FromResult(Assert(step)),
        "record" => RecordAsync(step, cancel),
        "soak" => SoakAsync(step, cancel),
        _ => throw new StepFailure($"нет действия {step.Do}"),
    };

    private TimeSpan Timeout(ScenarioStep step, TimeSpan fallback) => step.Num("timeout") is { } seconds ? TimeSpan.FromSeconds(seconds) : fallback;

    // ------------------------------------------------------------------ сервер

    private Proc Server => _server is { Exited: false } server ? server : throw new StepFailure("сервер не идёт");

    private async Task<string> StartServerAsync(ScenarioStep step, CancellationToken cancel)
    {
        if (_server is { Exited: false })
            throw new StepFailure("сервер уже идёт");
        var dir = Path.Combine(Run, "server");
        Directory.CreateDirectory(dir);
        var settingsPath = Path.Combine(dir, "settings.json");
        int port;
        if (_vars.Has("port"))
            port = int.Parse(_vars["port"], CultureInfo.InvariantCulture); // перезапуск — тот же порт: клиенты помнят адрес
        else
            port = Ports.Free();
        var settings = new JsonObject
        {
            ["port"] = port,
            ["admin"] = "smoke",
            ["save"] = "save.kharos",
            ["limits"] = new JsonObject { ["connections"] = 64, ["connectsPerMinute"] = 600, ["newPlayersPerHour"] = 1000 },
        };
        Merge(settings, scenario.Settings);
        Merge(settings, step.Raw["settings"] as JsonObject);
        settings["port"] = port;
        File.WriteAllText(settingsPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        _serverStarts++;
        var label = _serverStarts == 1 ? "server" : $"server{_serverStarts}";
        var logPath = Path.Combine(Run, label + ".log");
        Launch launch;
        if (options.WslServer is { } linux)
        {
            var distro = options.WslDistro is { Length: > 0 } d ? new[] { "-d", d } : [];
            var pidFile = Path.Combine(dir, "server.pid");
            var args = new List<string>(distro) { "--cd", Packages.ToWsl(dir), "--exec", "sh", "-c", "echo $$ > server.pid; exec \"$0\" \"$@\"", linux, "--settings", "settings.json" };
            if (_modsRoot is not null)
                args.AddRange(["--mods", Packages.ToWsl(_modsRoot)]);
            launch = new Launch("wsl.exe", args, dir, new Dictionary<string, string?>(), logPath, Stdin: true)
            {
                // Убить wsl.exe мало: сервер в Linux живёт дальше. Сначала kill по PID из server.pid.
                OnKill = () => KillInWsl(distro, pidFile),
            };
        }
        else
        {
            var exe = options.Server ?? throw new StepFailure("нет kharos-server: --server, --package или --build");
            var args = new List<string> { "--settings", settingsPath };
            if (_modsRoot is not null)
                args.AddRange(["--mods", _modsRoot]);
            launch = new Launch(exe, args, dir, new Dictionary<string, string?> { ["PORT"] = null, ["SAVE"] = null, ["SETTINGS"] = null }, logPath, Stdin: true);
        }
        _server = group.Start(label, launch);
        var listening = await _server.ExpectAsync(line => line.Contains("слушает", StringComparison.Ordinal), 0, Timeout(step, TimeSpan.FromMinutes(2)), cancel).ConfigureAwait(false);
        if (listening is null)
            throw new StepFailure($"сервер не поднялся{(_server.ExitCode is { } code ? $" (код {code})" : "")}: {Tail(_server.Lines(), 5)}");
        _vars["port"] = port.ToString(CultureInfo.InvariantCulture);
        _vars["url"] = $"ws://127.0.0.1:{port}";
        _vars["server"] = dir;
        var world = _server.Lines().FirstOrDefault(l => l.StartsWith("мир загружен", StringComparison.Ordinal) || l.StartsWith("новый мир", StringComparison.Ordinal));
        return $"PID {_server.Pid}, порт {port}{(world is null ? "" : ", " + world)}";
    }

    private static void KillInWsl(string[] distro, string pidFile)
    {
        if (!File.Exists(pidFile))
            return;
        var pid = File.ReadAllText(pidFile).Trim();
        if (!int.TryParse(pid, out _))
            return;
        var info = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in distro.Concat(["--exec", "kill", "-9", pid]))
            info.ArgumentList.Add(arg);
        using var kill = Process.Start(info);
        kill?.WaitForExit(10_000);
    }

    private static void Merge(JsonObject into, JsonObject? patch)
    {
        if (patch is null)
            return;
        foreach (var (key, value) in patch)
        {
            if (value is JsonObject obj && into[key] is JsonObject target)
                Merge(target, obj);
            else
                into[key] = value?.DeepClone();
        }
    }

    private async Task<string> ConsoleAsync(ScenarioStep step, CancellationToken cancel)
    {
        var server = Server;
        var from = server.LineCount;
        server.Send(_vars.Expand(step.Need("line")));
        return step.Str("expect") is { } pattern ? await ExpectAsync(step, pattern, from, cancel).ConfigureAwait(false) : "";
    }

    /// <summary>
    /// Строка журнала сервера или, с полем name, журнала клиента (kharos.log в его KHAROS_HOME: «шаг N готов (тик …)»;
    /// stderr клиента на Windows идёт в кодировке консоли, файл журнала — в UTF-8).
    /// </summary>
    private async Task<string> ExpectAsync(ScenarioStep step, string pattern, int from, CancellationToken cancel)
    {
        if (step.Str("name") is { } client)
            return await ExpectClientAsync(step, Client(client), new Regex(_vars.Expand(pattern)), cancel).ConfigureAwait(false);
        var server = _server ?? throw new StepFailure("сервера не было");
        var regex = new Regex(_vars.Expand(pattern));
        var line = await server.ExpectAsync(regex.IsMatch, from, Timeout(step, TimeSpan.FromSeconds(60)), cancel).ConfigureAwait(false)
            ?? throw new StepFailure($"нет строки /{regex}/ в выводе {server.Label}; хвост: {Tail(server.Lines(), 4)}");
        if (step.Str("as") is { } label)
            _vars.AddGroups(label, regex.Match(line), regex);
        return line;
    }

    private async Task<string> ExpectClientAsync(ScenarioStep step, Proc proc, Regex regex, CancellationToken cancel)
    {
        var log = Path.Combine(proc.Launch.Env["KHAROS_HOME"]!, "kharos.log");
        var timeout = Timeout(step, TimeSpan.FromSeconds(60));
        var watch = Stopwatch.StartNew();
        // Журнал клиента — файл: читается заново раз в 200 мс (он невелик), пока не найдётся строка.
        while (true)
        {
            var exited = proc.Exited;
            var lines = File.Exists(log) ? [.. ReadShared(log).Skip(_logFrom.GetValueOrDefault(proc.Label))] : Array.Empty<string>();
            if (lines.FirstOrDefault(regex.IsMatch) is { } line)
            {
                if (step.Str("as") is { } label)
                    _vars.AddGroups(label, regex.Match(line), regex);
                return line;
            }
            if (exited || watch.Elapsed > timeout)
                throw new StepFailure($"нет строки /{regex}/ в журнале {proc.Label}{(exited ? $" (вышел с кодом {proc.ExitCode})" : "")}; хвост: {Tail(lines, 4)}");
            await Task.Delay(200, cancel).ConfigureAwait(false);
        }
    }

    private static string[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
    }

    private async Task<string> StopAsync(ScenarioStep step, CancellationToken cancel)
    {
        var server = Server;
        server.Send("stop");
        if (!await server.WaitAsync(Timeout(step, TimeSpan.FromMinutes(2)), cancel).ConfigureAwait(false))
        {
            server.Kill();
            throw new StepFailure("сервер не вышел по stop");
        }
        var expected = (int)(step.Num("code") ?? 0);
        // wsl.exe возвращает код процесса Linux.
        if (server.ExitCode != expected)
            throw new StepFailure($"сервер вышел с кодом {server.ExitCode}, ждал {expected}");
        var saved = server.Lines().LastOrDefault(l => l.StartsWith("мир сохранён", StringComparison.Ordinal));
        return $"код {server.ExitCode}{(saved is null ? "" : ", " + saved)}";
    }

    // ------------------------------------------------------------------ клиенты

    private Proc Client(string label) => _clients.TryGetValue(label, out var proc) ? proc : throw new StepFailure($"нет клиента {label}");

    private string KharosExe => options.Kharos ?? throw new StepFailure("нет kharos: --kharos, --package или --build");

    private string Home(string name)
    {
        var home = Path.Combine(Run, "home-" + name);
        Directory.CreateDirectory(home);
        _vars["home." + name] = home;
        return home;
    }

    private async Task<string> ClientAsync(ScenarioStep step, CancellationToken cancel)
    {
        var name = step.Need("name");
        var label = step.Str("as") ?? name;
        if (_clients.TryGetValue(label, out var old) && !old.Exited)
            throw new StepFailure($"клиент {label} уже идёт");
        var home = Home(name);
        var script = step.Raw["script"] is JsonValue file && file.GetValueKind() == JsonValueKind.String
            ? JsonNode.Parse(File.ReadAllText(Path.Combine(scenario.Dir, file.GetValue<string>())), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            : step.Raw["script"];
        var scriptPath = Path.Combine(Run, label + ".smoke.json");
        File.WriteAllText(scriptPath, _vars.Expand(script)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var report = Path.Combine(Run, label + ".report.json");
        if (File.Exists(report))
            File.Delete(report);
        var connect = step.Bool("connect") ?? _server is { Exited: false };
        var env = new Dictionary<string, string?>
        {
            ["KHAROS_HOME"] = home,
            ["KHAROS_AUDIO"] = "off",
            ["KHAROS_HIDDEN"] = "1",
            ["KHAROS_SMOKE"] = scriptPath,
            ["KHAROS_SMOKE_OUT"] = report,
            ["KHAROS_NAME"] = name,
        };
        if (connect)
            env["KHAROS_SERVER"] = _vars["url"];
        else if (_modsRoot is not null)
            env["KHAROS_MODS"] = Path.Combine(Path.GetDirectoryName(KharosExe)!, "mods") + Path.PathSeparator + _modsRoot;
        if (options.NoGpu)
            env["KHAROS_GPU"] = "none";
        if (step.Raw["env"] is JsonObject extra)
            foreach (var (key, value) in extra)
                env[key] = value is null ? null : _vars.Expand(value.ToString());
        // Журнал kharos.log общий у запусков с одним KHAROS_HOME: expect смотрит только строки этого запуска.
        var homeLog = Path.Combine(home, "kharos.log");
        _logFrom[label] = File.Exists(homeLog) ? ReadShared(homeLog).Length : 0;
        var proc = group.Start(label, new Launch(KharosExe, [], Run, env, Path.Combine(Run, label + ".log")));
        _clients[label] = proc;
        if (step.Bool("wait") == false)
            return $"PID {proc.Pid}, запущен{(connect ? $" к {_vars["url"]}" : "")}";
        return await WaitClientAsync(proc, step, cancel).ConfigureAwait(false);
    }

    private async Task<string> WaitClientAsync(Proc proc, ScenarioStep step, CancellationToken cancel)
    {
        var timeout = Timeout(step, options.ClientTimeout);
        if (!await proc.WaitAsync(timeout, cancel).ConfigureAwait(false))
        {
            proc.Kill();
            var partial = Path.Combine(Run, proc.Label + ".report.json");
            throw new StepFailure($"клиент {proc.Label} не закончил за {timeout.TotalSeconds:0} с (убит); {LogTail(proc)}{(File.Exists(partial) ? "" : "")}");
        }
        var expected = (int)(step.Num("code") ?? 0);
        var (summary, ok) = ReadReport(proc.Label);
        if (proc.ExitCode != expected || !ok)
            throw new StepFailure($"клиент {proc.Label}: код {proc.ExitCode} (ждал {expected}); {summary}; {LogTail(proc)}");
        return summary;
    }

    /// <summary>Отчёт клиента: подстановки под меткой и строка сводки.</summary>
    private (string Summary, bool Ok) ReadReport(string label)
    {
        var path = Path.Combine(Run, label + ".report.json");
        if (!File.Exists(path))
            return ("отчёта нет", false);
        var report = JsonNode.Parse(File.ReadAllText(path))!;
        _vars.AddReport(label, report);
        var steps = report["steps"] as JsonArray ?? [];
        var passed = steps.Count(s => s?["ok"]?.GetValue<bool>() == true);
        var ok = report["ok"]?.GetValue<bool>() == true;
        var text = new StringBuilder($"шагов {passed}/{steps.Count}, {report["seconds"]} с, тик {report["tick"]}, сущностей {report["entities"]}");
        if (!ok)
            text.Append($", ошибка: {report["error"]}");
        var notes = steps.Where(s => s?["note"] is not null && (s["do"]?.ToString() ?? "").StartsWith("shot", StringComparison.Ordinal)).Select(s => s!["note"]!.ToString()).ToList();
        if (notes.Count > 0)
            text.Append($"; снимки: {string.Join("; ", notes.Select(n => Path.GetFileName(n)))}");
        return (text.ToString(), ok);
    }

    private string LogTail(Proc proc)
    {
        var home = proc.Launch.Env.TryGetValue("KHAROS_HOME", out var h) ? h : null;
        var log = home is null ? null : Path.Combine(home, "kharos.log");
        var lines = log is not null && File.Exists(log) ? ReadShared(log) : proc.Lines();
        return "журнал: " + Tail(lines, 4);
    }

    private static string Tail(IReadOnlyList<string> lines, int count) => string.Join(" | ", lines.Skip(Math.Max(0, lines.Count - count)));

    private string Kill(ScenarioStep step)
    {
        var proc = Client(step.Need("name"));
        return proc.Kill() ? $"PID {proc.Pid} убит" : $"PID {proc.Pid} уже вышел (код {proc.ExitCode})";
    }

    // ------------------------------------------------------------------ прочее

    private string FileExists(ScenarioStep step)
    {
        var path = _vars.Expand(step.Need("path"));
        if (!Path.IsPathRooted(path))
            path = Path.Combine(Run, path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
            throw new StepFailure($"нет файла или пуст: {path}");
        return $"{info.Length / 1024} КБ";
    }

    private string Assert(ScenarioStep step)
    {
        var expr = _vars.Expand(step.Need("expr"));
        return Expr.Check(expr) ? expr : throw new StepFailure($"ложно: {expr}");
    }

    private async Task<string> RecordAsync(ScenarioStep step, CancellationToken cancel)
    {
        if (options.NoGpu)
            return "без GPU записи нет — пропуск";
        var frames = (int)(step.Num("frames") ?? 90);
        const int fps = 60;
        var dir = Path.Combine(Run, "record");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        var home = Home("record");
        var env = new Dictionary<string, string?>
        {
            ["KHAROS_HOME"] = home,
            // Звук записи рендерит сама запись в WAV, устройства нет: KHAROS_AUDIO не выключается, иначе WAV не будет.
            ["KHAROS_RECORD"] = dir,
            ["KHAROS_FRAMES"] = frames.ToString(CultureInfo.InvariantCulture),
            ["KHAROS_FPS"] = fps.ToString(CultureInfo.InvariantCulture),
            ["KHAROS_FFMPEG"] = "off",
            ["KHAROS_SIZE"] = step.Str("size") ?? "640x360",
            ["KHAROS_RATIO"] = "1",
            ["KHAROS_SKIP"] = "1",
            ["KHAROS_GAME"] = step.Str("game") ?? "skirmish",
        };
        if (_modsRoot is not null)
            env["KHAROS_MODS"] = Path.Combine(Path.GetDirectoryName(KharosExe)!, "mods") + Path.PathSeparator + _modsRoot;
        var proc = group.Start("record", new Launch(KharosExe, [], Run, env, Path.Combine(Run, "record.log")));
        var timeout = Timeout(step, TimeSpan.FromMinutes(5));
        if (!await proc.WaitAsync(timeout, cancel).ConfigureAwait(false))
        {
            proc.Kill();
            throw new StepFailure($"запись не закончилась за {timeout.TotalSeconds:0} с; {LogTail(proc)}");
        }
        if (proc.ExitCode != 0)
            throw new StepFailure($"запись: код {proc.ExitCode}; {LogTail(proc)}");
        var pngs = Directory.Exists(dir) ? Directory.GetFiles(dir, "frame_*.png").Length : 0;
        if (pngs != frames)
            throw new StepFailure($"кадров {pngs}, ждал {frames}");
        var wav = dir.TrimEnd('/', '\\') + ".wav";
        var seconds = Wav.Seconds(wav) ?? throw new StepFailure($"нет WAV или не разобрать: {wav}");
        var expected = (double)frames / fps;
        if (Math.Abs(seconds - expected) > 0.1)
            throw new StepFailure($"WAV {seconds:0.000} с, ждал {expected:0.000} ± 0,1 с");
        return $"кадров {pngs}, WAV {seconds:0.000} с";
    }

    private async Task<string> SoakAsync(ScenarioStep step, CancellationToken cancel)
    {
        var server = Server;
        var duration = step.Str("duration") is { } given ? Duration.Parse(given) : options.Soak;
        var bots = (int)(step.Num("bots") ?? options.Bots);
        // Память сервера: процесс сам (локальный сервер) или строки журнала («память N МБ» — сервер в WSL).
        var local = options.WslServer is null ? server.Process : null;
        var soak = new Soak(_vars["url"], bots, duration, local, server, log, Path.Combine(Run, "bots-cache"));
        var result = await soak.RunAsync(cancel).ConfigureAwait(false);
        SoakResult = result;
        File.WriteAllText(Path.Combine(Run, "soak.json"), JsonSerializer.Serialize(result, SoakJson.Default.SoakResult));
        var text = result.Describe();
        return result.Passed ? text : throw new StepFailure(text);
    }
}

/// <summary>Провал шага с понятной причиной.</summary>
internal sealed class StepFailure(string message) : Exception(message);

/// <summary>Длина WAV по заголовку (fmt и data).</summary>
internal static class Wav
{
    public static double? Seconds(string path)
    {
        if (!File.Exists(path))
            return null;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            return null;
        int channels = 0, rate = 0, bits = 0;
        long data = -1;
        for (var at = 12; at + 8 <= bytes.Length;)
        {
            var id = Encoding.ASCII.GetString(bytes, at, 4);
            var size = BitConverter.ToUInt32(bytes, at + 4);
            if (id == "fmt " && at + 24 <= bytes.Length)
            {
                channels = BitConverter.ToUInt16(bytes, at + 10);
                rate = BitConverter.ToInt32(bytes, at + 12);
                bits = BitConverter.ToUInt16(bytes, at + 22);
            }
            else if (id == "data")
            {
                // Незакрытая запись оставляет размер 0 или -1: тогда — до конца файла.
                data = size is 0 or uint.MaxValue ? bytes.Length - at - 8 : size;
                break;
            }
            at += 8 + (int)size + (int)(size & 1);
        }
        if (channels <= 0 || rate <= 0 || bits <= 0 || data < 0)
            return null;
        return data / (double)(rate * channels * (bits / 8));
    }
}
