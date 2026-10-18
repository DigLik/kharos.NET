using System.Diagnostics;
using Base.Client.Sounds;
using Kharos.Audio;
using Kharos.Client.Game;
using Kharos.Content;
using Kharos.Engine.World;
using Kharos.Host;
using MemoryPack;
using KHost = Kharos.Host.Host;
using KHostOptions = Kharos.Host.HostOptions;
using RenderFrame = Kharos.Gpu.Frame;

namespace Kharos.Base.Tests.AudioTests;

/// <summary>Банк звуков base, посчитанный модом один раз на все тесты (всё, что мод кладёт в kh_bank).</summary>
internal static class BankFixture
{
    internal sealed record Rendered(Bus Bus, float[][][] Variants);

    private static readonly Lazy<Dictionary<string, Rendered>> Everything = new(() =>
    {
        var all = new Dictionary<string, Rendered>();
        foreach (var entry in SoundBank.All)
            all[entry.Name] = new Rendered(Enum.Parse<Bus>(entry.Bus.ToString()), [.. Enumerable.Range(0, entry.Variants).Select(variant => entry.Render(AudioFormat.SampleRate, variant))]);
        return all;
    });

    public static IReadOnlyDictionary<string, Rendered> All => Everything.Value;

    /// <summary>Записи и голоса мода — в движок, как это делает ClientGame при загрузке.</summary>
    public static void Fill(AudioEngine engine)
    {
        foreach (var (name, rendered) in All)
            engine.Bank.Add(name, rendered.Bus, [.. rendered.Variants.Select(channels => new SampleBuffer(channels))]);
        engine.RegisterVoice(global::Base.Client.Audio.FlightAudio.Voice, Kharos.Audio.Graph.VoiceGraph.Decode(global::Base.Client.Audio.FlightAudio.Build().Graph.Bytes));
    }
}

/// <summary>Партия base целиком: хост в своём потоке, клиент с client.wasm, звук — офлайн-движок без устройства.</summary>
internal sealed class BaseSession : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly bool _synthetic;
    private double _last, _time;

    public const string NotBuilt = "mods/base/host.wasm и client.wasm не собраны: pwsh dotnet/tools/build-mods.ps1 -Mod base";

    public static bool Built => BaseGame.Built && File.Exists(Path.Combine(BaseGame.ModDir, "client.wasm"));

    /// <param name="audio">Есть ли звук у клиента.</param>
    /// <param name="buggies">Сколько багги в начале партии (0 — как в игре: MCV, две багги и три пехотинца).</param>
    /// <param name="synthetic">Время кадров — шаг 1/60 с, а не часы: быстрый прогон фона.</param>
    public BaseSession(bool audio = true, int buggies = 0, bool synthetic = false)
    {
        Assert.SkipUnless(Built, NotBuilt);
        _synthetic = synthetic;
        List<ModPackage> mods = [ModPackage.Read(BaseGame.ModDir)];
        if (buggies > 0)
        {
            // Стартовый набор партии — список видов: двадцать багги вместо MCV с отрядом.
            var kit = string.Join(", ", Enumerable.Repeat("\"buggy\"", buggies));
            mods.Add(new ModPackage("tweak", """{ "name": "tweak", "version": "1", "requires": ["base"] }"""u8,
                new Dictionary<string, byte[]> { ["games"] = System.Text.Encoding.UTF8.GetBytes($$"""{ "skirmish": { "units": [{{kit}}] } }""") }));
        }
        var content = GameContent.Load(mods);
        Engine = audio ? new AudioEngine(new AudioEngineOptions { Seed = 3 }) : null;
        Local = LocalGame.Start(() => new KHost(content, new KHostOptions { Modules = new Kharos.Plugins.Game.GameModuleOptions { CallTimeoutMs = 1000 }, Seed = 1 }));
        Game = new ClientGame(content, Local.Client, new ClientGameOptions { ModuleOptions = new Kharos.Plugins.Game.GameModuleOptions { CallTimeoutMs = 1000 }, Audio = Engine, Log = Log.Add });
    }

    public AudioEngine? Engine { get; }
    public LocalGame Local { get; }
    public ClientGame Game { get; }
    public List<string> Log { get; } = [];

    /// <summary>Левый канал всего, что отрендерил движок.</summary>
    public List<float> Output { get; } = [];

    /// <summary>Кадр клиента и столько звука, сколько прошло времени.</summary>
    public RenderFrame Step()
    {
        _time = _synthetic ? _time + 1 / 60.0 : _clock.Elapsed.TotalSeconds;
        var frame = Game.Step(new ClientInput { Time = _time });
        if (Engine is not null)
        {
            var frames = (int)Math.Round((_time - _last) * AudioFormat.SampleRate);
            if (_last > 0 && frames > 0)
                Output.AddRange(Engine.Render(frames).Left);
        }
        _last = _time;
        return frame;
    }

    /// <summary>Кадры, пока не пройдёт seconds времени кадров; для живых часов между кадрами — пауза.</summary>
    public void Run(double seconds)
    {
        var until = _time + seconds;
        while (_time < until)
        {
            Step();
            if (!_synthetic)
                Thread.Sleep(10);
        }
    }

    /// <summary>Свои юниты (с Mover) в реплике.</summary>
    public List<uint> Units()
    {
        var world = Game.Replica.World!;
        var units = new List<uint>();
        world.Query([world.Schema.Id("Mover"), Builtin.Owner], units);
        units.RemoveAll(entity => world.Number(entity, Builtin.Owner, 0) != Game.Replica.Player);
        units.Sort();
        return units;
    }

    /// <summary>Сколько из юнитов ездят на моторе: пехота шагает, мотора у неё нет.</summary>
    public int Motorized(IEnumerable<uint> units)
    {
        var world = Game.Replica.World!;
        var infantry = Game.Content.FindProto("units/infantry")!.Id;
        return units.Count(entity => (int)world.Number(entity, Builtin.Proto, 0) != infantry);
    }

    public (double X, double Y) Centre(IReadOnlyList<uint> units)
    {
        var world = Game.Replica.World!;
        return (units.Average(e => world.Number(e, Builtin.Position, 0)), units.Average(e => world.Number(e, Builtin.Position, 1)));
    }

    /// <summary>Камера на точку: звук слышит то, что на экране.</summary>
    public void Look(double x, double y, double zoom = 32) => Game.Scene.Camera = new CameraState(x, y, zoom);

    public void Move(IReadOnlyList<uint> units, double x, double y) =>
        Game.Send("move", MemoryPackSerializer.Serialize(new global::Base.Shared.Orders.MoveOrder { Units = [.. units], X = (int)Math.Floor(x), Y = (int)Math.Floor(y) }));

    public void Dispose()
    {
        Game.Dispose();
        Local.Dispose();
    }

    public static double Rms(IReadOnlyList<float> samples, int from, int count)
    {
        double sum = 0;
        for (var i = from; i < Math.Min(from + count, samples.Count); i++)
            sum += samples[i] * (double)samples[i];
        return Math.Sqrt(sum / Math.Max(1, count));
    }
}
