using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Base.Shared.Orders;
using MemoryPack;

namespace Kharos.Smoke.Tests;

public sealed class ScenarioTests
{
    private static string ScenarioDir => Path.Combine(AppContext.BaseDirectory, "scenarios");

    [Theory]
    [InlineData("single")]
    [InlineData("server")]
    [InlineData("soak")]
    public void ShippedScenariosParse(string name)
    {
        var scenario = Scenario.Load(Scenario.Find(name));
        Assert.NotEmpty(scenario.Steps);
        Assert.All(scenario.Steps, step => Assert.Contains(step.Do, Scenario.Verbs));
    }

    [Fact]
    public void ClientScriptsOfShippedScenariosHaveKnownVerbs()
    {
        string[] verbs = ["click", "type", "key", "wait", "order", "chat", "save", "leave", "load", "editor", "connect", "shot", "mark", "assert"];
        foreach (var name in new[] { "single", "server" })
        {
            var scenario = Scenario.Load(Scenario.Find(name));
            foreach (var step in scenario.Steps.Where(s => s.Do == "client"))
            {
                var script = step.Raw["script"] is JsonObject obj ? obj["steps"]!.AsArray() : step.Raw["script"]!.AsArray();
                foreach (var item in script)
                {
                    var line = item!["do"]!.GetValue<string>();
                    Assert.Contains(line.Split(' ')[0], verbs);
                }
            }
        }
    }

    [Fact]
    public void UnknownVerbAndMissingFieldsAreRejected()
    {
        Assert.Throws<FormatException>(() => Scenario.Parse("""{ "steps": [ { "do": "dance" } ] }""", "t", "."));
        Assert.Throws<FormatException>(() => Scenario.Parse("""{ "steps": [ { "do": "client" } ] }""", "t", "."));
        Assert.Throws<FormatException>(() => Scenario.Parse("""{ "steps": [ { "do": "client", "name": "a" } ] }""", "t", "."));
        Assert.Throws<FormatException>(() => Scenario.Parse("""{ "nosteps": [] }""", "t", "."));
    }

    [Fact]
    public void CommentsVarsAndModsAreRead()
    {
        var scenario = Scenario.Parse("""
            { // сценарий
              "mods": ["smoke"], "vars": { "x": "1" },
              "steps": [ { "do": "console", "line": "status", "expect": "тик (?<tick>\\d+)", "as": "s" } ], }
            """, "t", ".");
        Assert.Equal(["smoke"], scenario.Mods);
        Assert.Equal("1", scenario.Values["x"]);
        Assert.Equal("console «status»", scenario.Steps[0].Title);
    }

    [Fact]
    public void VarsExpandReportsAndGroups()
    {
        var vars = new Vars();
        vars["url"] = "ws://127.0.0.1:5";
        vars.AddReport("b1", JsonNode.Parse("""{ "ok": true, "tick": 120, "marks": { "before": { "entities": 42 } }, "steps": [1] }"""));
        var regex = new System.Text.RegularExpressions.Regex(@"тик (?<tick>\d+), сущностей (?<entities>\d+)");
        vars.AddGroups("status", regex.Match("тик 900, сущностей 77, клиентов 2"), regex);
        Assert.Equal("ws://127.0.0.1:5/x 42 1 120", vars.Expand("${url}/x ${b1.marks.before.entities} ${b1.ok} ${b1.tick}"));
        Assert.Equal("900 77", vars.Expand("${status.tick} ${status.entities}"));
        Assert.Throws<KeyNotFoundException>(() => vars.Expand("${nope}"));
        var node = vars.Expand(JsonNode.Parse("""{ "steps": [ { "do": "connect ${url}" } ] }"""));
        Assert.Equal("connect ws://127.0.0.1:5", node!["steps"]![0]!["do"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("10 >= 9", true)]
    [InlineData("10 >= 9 + 2", false)]
    [InlineData("42 == 40 + 2", true)]
    [InlineData("5 - 3 < 1", false)]
    [InlineData("1.5 != 1.5", false)]
    public void ExpressionsCompareSums(string text, bool expected) => Assert.Equal(expected, Expr.Check(text));

    [Fact]
    public void ExpressionWithUnexpandedWordIsAnError() => Assert.Throws<FormatException>(() => Expr.Check("tick >= 1"));

    [Theory]
    [InlineData("15m", 900)]
    [InlineData("90s", 90)]
    [InlineData("2h", 7200)]
    [InlineData("1.5m", 90)]
    [InlineData("30", 30)]
    public void DurationsParse(string text, double seconds) => Assert.Equal(seconds, Duration.Parse(text).TotalSeconds);

    [Fact]
    public void PaintPayloadInSingleScenarioIsAnEditPaintOrder()
    {
        // Кисть земли редактора — вложенный объект MemoryPack: в сценарии он готовыми байтами (vars.paint).
        var scenario = Scenario.Load(Scenario.Find("single"));
        var expected = Convert.ToBase64String(MemoryPackSerializer.Serialize(new EditPaintOrder { X = 0, Y = 0, Size = 9, Brush = new Brush { Terrain = 2 } }));
        Assert.Equal(expected, scenario.Values["paint"]);
        var order = MemoryPackSerializer.Deserialize<EditPaintOrder>(Convert.FromBase64String(scenario.Values["paint"]))!;
        Assert.Equal((0, 0, 9, (int?)2), (order.X, order.Y, order.Size, order.Brush.Terrain));
    }

    [Fact]
    public void CliParsesWorkflowCalls()
    {
        var cli = CliOptions.Parse(["server", "--package", "pkg", "--soak", "2m", "--bots", "4"]);
        Assert.Equal(("server", "pkg", 120.0, 4), (cli.Scenario, cli.Package, cli.Soak!.Value.TotalSeconds, cli.Bots));
        Assert.Equal(900, CliOptions.Parse(["soak"]).Soak!.Value.TotalSeconds);
        Assert.Throws<FormatException>(() => CliOptions.Parse(["single", "--what"]));
        Assert.Throws<FormatException>(() => CliOptions.Parse([]));
    }
}

public sealed class PortTests
{
    [Fact]
    public void FreePortCanBeBound()
    {
        var port = Ports.Free();
        Assert.InRange(port, 1, 65535);
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
    }

    [Fact]
    public void FreePortsAreNotAllTheSame()
    {
        var ports = Enumerable.Range(0, 5).Select(_ => Ports.Free()).ToHashSet();
        Assert.True(ports.Count > 1);
    }
}

public sealed class ProcessTests
{
    private static Launch Sleeper(string log) => OperatingSystem.IsWindows()
        ? new Launch("ping.exe", ["-n", "120", "127.0.0.1"], Path.GetTempPath(), new Dictionary<string, string?>(), log)
        : new Launch("sleep", ["120"], Path.GetTempPath(), new Dictionary<string, string?>(), log);

    private static string TempLog() => Path.Combine(Path.GetTempPath(), $"kharos-smoke-test-{Guid.NewGuid():N}.log");

    [Fact]
    public void DisposeKillsEverythingStarted()
    {
        var (logA, logB) = (TempLog(), TempLog());
        Process first, second;
        using (var group = new ProcessGroup())
        {
            first = group.Start("a", Sleeper(logA)).Process;
            second = group.Start("b", Sleeper(logB)).Process;
            Assert.False(first.HasExited);
        }
        Assert.True(first.WaitForExit(10_000));
        Assert.True(second.WaitForExit(10_000));
        File.Delete(logA);
        File.Delete(logB);
    }

    /// <summary>
    /// Группу закрывают, пока процесс сыплет выводом: строки, дочитанные из трубы после закрытия журнала, не пишутся в
    /// закрытый StreamWriter — исключение в обработчике вывода (пул потоков) роняло весь процесс тестов.
    /// </summary>
    [Fact]
    public void DisposeWhileOutputStreamsKeepsProcessAlive()
    {
        var logs = new List<string>();
        for (var round = 0; round < 3; round++)
        {
            var log = TempLog();
            logs.Add(log);
            var flood = OperatingSystem.IsWindows()
                ? new Launch("cmd.exe", ["/c", "for /L %i in (1,1,200000) do @echo line %i"], Path.GetTempPath(), new Dictionary<string, string?>(), log)
                : new Launch("sh", ["-c", "i=0; while [ $i -lt 200000 ]; do echo line $i; i=$((i+1)); done"], Path.GetTempPath(), new Dictionary<string, string?>(), log);
            Proc proc;
            using (var group = new ProcessGroup())
            {
                proc = group.Start("flood", flood);
                while (proc.LineCount < 100 && !proc.Exited)
                    Thread.Sleep(1);
            }
            // Дочитывание трубы после Dispose идёт своим чередом; процесс тестов при этом жив.
            Thread.Sleep(200);
            Assert.True(proc.LineCount >= 100);
        }
        foreach (var log in logs)
            File.Delete(log);
    }

    [Fact]
    public void KillAllNamesOnlyTheLiving()
    {
        var log = TempLog();
        using var group = new ProcessGroup();
        var proc = group.Start("sleeper", Sleeper(log));
        var killed = group.KillAll();
        Assert.Single(killed);
        Assert.Contains("sleeper", killed[0]);
        Assert.Empty(group.KillAll());
        Assert.True(proc.Exited);
    }

    [Fact]
    public async Task OutputLinesAreCollectedAndKharosEnvIsNotInherited()
    {
        var log = TempLog();
        Environment.SetEnvironmentVariable("KHAROS_SMOKE_TEST_LEAK", "1");
        try
        {
            var launch = OperatingSystem.IsWindows()
                ? new Launch("cmd.exe", ["/c", "echo hello& echo [%KHAROS_SMOKE_TEST_LEAK%][%MINE%]"], Path.GetTempPath(), new Dictionary<string, string?> { ["MINE"] = "own" }, log)
                : new Launch("sh", ["-c", "echo hello; echo \"[$KHAROS_SMOKE_TEST_LEAK][$MINE]\""], Path.GetTempPath(), new Dictionary<string, string?> { ["MINE"] = "own" }, log);
            using var group = new ProcessGroup();
            var proc = group.Start("echo", launch);
            Assert.True(await proc.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            Assert.Equal(0, proc.ExitCode);
            var lines = proc.Lines();
            Assert.Contains("hello", lines);
            Assert.Contains(lines, l => l.Contains("[own]") && !l.Contains("[1]"));
            Assert.Equal("hello", await proc.ExpectAsync(l => l == "hello", 0, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KHAROS_SMOKE_TEST_LEAK", null);
            File.Delete(log);
        }
    }
}

public sealed class WavTests
{
    [Fact]
    public void LengthComesFromHeader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kharos-smoke-{Guid.NewGuid():N}.wav");
        const int rate = 48000, channels = 2, seconds = 3;
        var data = rate * channels * 2 * seconds / 2; // 1,5 с
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + data);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)channels);
            writer.Write(rate);
            writer.Write(rate * channels * 2);
            writer.Write((short)(channels * 2));
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(data);
            writer.Write(new byte[data]);
        }
        Assert.Equal(1.5, Wav.Seconds(path)!.Value, 6);
        File.Delete(path);
        Assert.Null(Wav.Seconds(path));
    }
}
