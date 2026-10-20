using System.Text.Json;
using Kharos.Client.Shell;
using Kharos.Desktop;
using Kharos.Gpu;

// Окно GLFW и устройство WebGPU — по одному на процесс за раз, KHAROS_HOME — переменная процесса: тесты десктопа идут подряд.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Kharos.Desktop.Tests;

/// <summary>Смоук в kharos (Н7.3, П1): сценарий в скрытом окне или без GPU, код выхода, отчёт и профиль в KHAROS_HOME.</summary>
public sealed class SmokeScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kharos-smoke-test-" + Guid.NewGuid().ToString("N"));
    private readonly string? _home = Environment.GetEnvironmentVariable(FileStorage.HomeVariable);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FileStorage.HomeVariable, _home);
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private string Home => Path.Combine(_root, "home");

    private static bool HasGpu()
    {
        var gpu = GpuContext.TryCreateHeadless(out _);
        gpu?.Dispose();
        return gpu is not null;
    }

    /// <summary>
    /// Моды из репозитория (dotnet/mods), а не их копия рядом с тестами: копию в bin делает сборка, и wasm, собранный после
    /// неё (CI: dotnet build, потом build-mods), туда не попадает — hello шёл бы без кода, и роверов бы не было.
    /// </summary>
    private static List<string> ModRoots()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Kharos.slnx")))
                return [Path.Combine(dir.FullName, "mods")];
        return DesktopOptions.FindModRoots();
    }

    /// <summary>Сценарий на hello (партия сразу, мимо меню); код выхода и отчёт.</summary>
    private (int Code, JsonElement Report) Smoke(string steps, bool noGpu)
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(FileStorage.HomeVariable, Home);
        var script = Path.Combine(_root, "script.json");
        var report = Path.Combine(_root, "report.json");
        File.WriteAllText(script, steps);
        var options = new DesktopOptions(ModRoots(), "hello", false, 1f, null, Smoke: new SmokeOptions(script, report), NoGpu: noGpu);
        Assert.False(options.Visible);
        var code = Shell.Run(options);
        using var document = JsonDocument.Parse(File.ReadAllText(report));
        return (code, document.RootElement.Clone());
    }

    private static string Steps(JsonElement report) =>
        string.Join("\n", report.GetProperty("steps").EnumerateArray().Select(step =>
            $"{step.GetProperty("index")} {step.GetProperty("do")}: {(step.GetProperty("ok").GetBoolean() ? "ок" : step.GetProperty("error").GetString())}"));

    [Fact(DisplayName = "смоук: одиночная партия hello в скрытом окне — приказ, отметки, сохранение и загрузка; профиль только в KHAROS_HOME")]
    public void SinglePlayerScriptOnHello()
    {
        // Роверы ставит код мода: без host.wasm партия hello идёт пустой, и ждать их бессмысленно.
        var hello = Path.Combine(ModRoots()[0], "hello");
        Assert.SkipUnless(File.Exists(Path.Combine(hello, "host.wasm")) && File.Exists(Path.Combine(hello, "client.wasm")),
            "mods/hello/host.wasm и client.wasm не собраны: pwsh dotnet/tools/build-mods.ps1 -Mod hello");
        var gpu = HasGpu();
        var slot = "smoke-" + Guid.NewGuid().ToString("N")[..8];
        var shot = Path.Combine(_root, "game.png").Replace('\\', '/');
        var (code, report) = Smoke($$"""
            { "timeout": 120, "steps": [
              { "do": "wait game" },
              { "do": "wait count rover >= 1" },
              { "do": "mark a" },
              { "do": "order", "name": "hello.spawn", "args": [{ "i32": 4 }, { "f64": "@rover.x" }, { "f64": "@rover.y" }, { "f64": 1 }] },
              { "do": "wait count rover >= 7" },
              { "do": "wait tick>=+10" },
              { "do": "mark b" },
              { "do": "assert b.tick > a.tick" },
              { "do": "assert b.entities >= a.entities + 4" },
              { "do": "shot {{shot}}" },
              { "do": "save {{slot}}" },
              { "do": "wait count rover >= 7" },
              { "do": "mark saved" },
              { "do": "leave" },
              { "do": "wait ui menu:new" },
              { "do": "load {{slot}}" },
              { "do": "wait count rover >= 7" },
              { "do": "assert tick >= saved.tick" }
            ] }
            """, noGpu: !gpu);
        Assert.True(code == 0, Steps(report));
        Assert.True(report.GetProperty("ok").GetBoolean());
        Assert.Equal(18, report.GetProperty("steps").GetArrayLength());
        Assert.True(report.GetProperty("tick").GetDouble() > 0);
        Assert.True(report.GetProperty("entities").GetInt32() >= 7);
        Assert.Equal(gpu, File.Exists(shot));

        // Профиль — только в KHAROS_HOME: слот, журнал; в настоящем профиле этого прогона нет.
        Assert.Equal(Path.GetFullPath(Home), report.GetProperty("home").GetString());
        Assert.Equal(Path.GetFullPath(Home), FileStorage.ProfileDirectory);
        Assert.StartsWith(Path.GetFullPath(Home), DesktopLog.FilePath);
        Assert.StartsWith(Path.GetFullPath(Home), NetGame.CacheDirectory);
        Assert.StartsWith(Path.GetFullPath(Home), PlayerTokens.FilePath);
        var index = File.ReadAllText(new FileStorage(Path.Combine(Home, "storage")).PathOf(SaveSlots.IndexKey));
        Assert.Contains(slot, index);
        Assert.Contains("script.json", File.ReadAllText(Path.Combine(Home, "kharos.log")));
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kharos");
        if (File.Exists(Path.Combine(real, "storage", "slots", "index")))
            Assert.DoesNotContain(slot, File.ReadAllText(Path.Combine(real, "storage", "slots", "index")));
        if (File.Exists(Path.Combine(real, "kharos.log")))
            Assert.DoesNotContain(_root, File.ReadAllText(Path.Combine(real, "kharos.log")));
    }

    [Fact(DisplayName = "смоук: KHAROS_GPU=none — ни окна, ни рендера, партия и мод идут, снимок пропускается")]
    public void RunsWithoutGpu()
    {
        var (code, report) = Smoke("""
            [
              { "do": "wait game", "timeout": 120 },
              { "do": "mark a" },
              { "do": "wait tick>=+5" },
              { "do": "assert tick >= a.tick + 5" },
              { "do": "shot none.png" }
            ]
            """, noGpu: true);
        Assert.True(code == 0, Steps(report));
        Assert.Equal("none", report.GetProperty("gpu").GetString());
        var shot = report.GetProperty("steps")[4];
        Assert.Contains("без GPU", shot.GetProperty("note").GetString());
        Assert.False(File.Exists("none.png"));
    }

    [Fact(DisplayName = "смоук: провал шага — код 2, в отчёте номер шага и причина")]
    public void FailedStepExitsWithTwo()
    {
        var (code, report) = Smoke("""
            [
              { "do": "wait game", "timeout": 120 },
              { "do": "wait ui no:such:node 0.3" },
              { "do": "mark never" }
            ]
            """, noGpu: true);
        Assert.Equal(2, code);
        Assert.False(report.GetProperty("ok").GetBoolean());
        Assert.Equal(1, report.GetProperty("failed").GetInt32());
        var steps = report.GetProperty("steps");
        Assert.Equal(2, steps.GetArrayLength());
        Assert.Contains("срок", steps[1].GetProperty("error").GetString());
    }

    [Fact(DisplayName = "смоук: ложное утверждение и битый сценарий — код 2")]
    public void AssertAndBadScriptFail()
    {
        var (code, report) = Smoke("""[{ "do": "mark a" }, { "do": "assert a.tick > 1000000" }]""", noGpu: true);
        Assert.Equal(2, code);
        Assert.Contains("ложно", report.GetProperty("steps")[1].GetProperty("error").GetString());

        (code, report) = Smoke("""[{ "do": "jump high" }]""", noGpu: true);
        Assert.Equal(2, code);
        Assert.Contains("jump", report.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "смоук: разбор шагов, выражения и payload приказа MemoryPack")]
    public void ParsesStepsExpressionsAndOrders()
    {
        var (steps, timeout) = SmokeScript.Parse("""{ "timeout": 5, "steps": ["click menu:new", { "do": "wait tick>=+20", "timeout": 3 }] }""");
        Assert.Equal(5, timeout);
        Assert.Equal(["click", "wait"], steps.Select(s => s.Verb));
        Assert.Equal(3, steps[1].Timeout);
        Assert.Throws<FormatException>(() => SmokeScript.Parse("""[{ "do": "fly" }]"""));

        double? Marks(string word) => word switch { "a.tick" => 100, "a.entities" => 7, _ => null };
        Assert.True(SmokeExpr.Check("a.tick + 5 == 105", Marks));
        Assert.True(SmokeExpr.Check("a.entities>=7", Marks));
        Assert.False(SmokeExpr.Check("a.tick - 1 > 99", Marks));
        Assert.Throws<FormatException>(() => SmokeExpr.Check("b.tick > 1", Marks));

        // Тот же байтовый вид, что у MemoryPack для [MemoryPackable] класса { int Count; double X; bool Q; uint[] U; string S }.
        using var args = JsonDocument.Parse("""[{ "i32": 3 }, { "f64": "@x" }, { "bool": true }, { "u32[]": [1, 2] }, { "str": "hi" }]""");
        var payload = SmokeOrder.Pack([.. args.RootElement.EnumerateArray()], name => name == "x" ? [2.5] : []);
        var expected = MemoryPack.MemoryPackSerializer.Serialize(new Packed { Count = 3, X = 2.5, Q = true, U = [1, 2], S = "hi" });
        Assert.Equal(expected, payload);
    }
}

[MemoryPack.MemoryPackable]
public sealed partial class Packed
{
    public int Count { get; set; }
    public double X { get; set; }
    public bool Q { get; set; }
    public uint[] U { get; set; } = [];
    public string S { get; set; } = "";
}
