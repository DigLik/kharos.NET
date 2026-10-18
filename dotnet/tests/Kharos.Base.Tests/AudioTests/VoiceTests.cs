using Base.Client.Audio;
using Base.Client.Sounds;
using Kharos.Audio;
using Kharos.Audio.Graph;
using S = Kharos.Mod.Sdk.Client;

namespace Kharos.Base.Tests.AudioTests;

/// <summary>Голоса base: графы KHVG разбираются кодеком клиента, номера узлов указывают на нужные узлы, голоса звучат.</summary>
public sealed class VoiceTests
{
    private static T Node<T>(VoiceGraph graph, int index) where T : NodeSpec => Assert.IsType<T>(graph.Nodes[index]);

    [Fact]
    public void FlightGraphDecodesAndLayoutPointsAtItsNodes()
    {
        var (built, n) = FlightAudio.Build();
        var graph = VoiceGraph.Decode(built.Bytes);
        Assert.Equal(built.Bytes, graph.Encode());
        Assert.Equal(FlightAudio.Layout, n);
        Assert.Equal(2, graph.Nodes.OfType<SampleSpec>().Count());
        Assert.Equal("noise:pink", Node<SampleSpec>(graph, n.Rush).Name);
        Assert.Equal(1.3f, Node<SampleSpec>(graph, n.Rush).Offset);
        Assert.Equal(450f, Node<FilterSpec>(graph, n.Band).Frequency);
        Assert.Equal(5f, Node<OscSpec>(graph, n.Flutter).Frequency);
        Node<GainSpec>(graph, n.Bus);
        Node<GainSpec>(graph, n.FlutterDepth);
        Node<GainSpec>(graph, n.SwayDepth);
        // Трепет бьёт по усилению свиста, раскачка — по усилению всей шины.
        Assert.Contains(graph.Connections, c => c.To.Param == ParamId.Gain && c.To.Node == n.Bus);
    }

    [Fact]
    public void MotorGraphsDecodeForEveryMotor()
    {
        foreach (var (name, motor) in MotorSounds.Motors)
        {
            string?[] squeals = motor.Running is null ? [null] : [null, motor.Running == "clatter" ? "squeal" : "screech"];
            foreach (var squeal in squeals)
            {
                var (built, n) = MotorSounds.Voice(name, motor.Running, squeal, 0.7f);
                var graph = VoiceGraph.Decode(built.Bytes);
                Assert.Equal($"motor:{name}:idle", Node<SampleSpec>(graph, n.Idle).Name);
                Assert.Equal($"motor:{name}:load", Node<SampleSpec>(graph, n.Loaded).Name);
                // Одна точка начала на обе петли: такты совпадают.
                Assert.Equal(0.7f, Node<SampleSpec>(graph, n.Idle).Offset);
                Assert.Equal(0.7f, Node<SampleSpec>(graph, n.Loaded).Offset);
                Assert.True(Node<SampleSpec>(graph, n.Idle).Loop);
                Node<FilterSpec>(graph, n.Tone);
                Node<GainSpec>(graph, n.Engine);
                Node<GainSpec>(graph, n.IdleGain);
                Node<GainSpec>(graph, n.LoadGain);
                Assert.Equal(motor.Running is null, n.Ground < 0);
                if (motor.Running is { } running)
                {
                    Assert.Equal($"running:{running}", Node<SampleSpec>(graph, n.Ground).Name);
                    Node<GainSpec>(graph, n.GroundGain);
                }
                Assert.Equal(squeal is null, n.Scrape < 0);
                if (squeal is not null)
                {
                    Assert.Equal($"running:{squeal}", Node<SampleSpec>(graph, n.Scrape).Name);
                    Node<GainSpec>(graph, n.ScrapeGain);
                }
            }
        }
    }

    [Fact]
    public void AmbienceGraphsDecode()
    {
        var (built, n) = AmbienceAudio.Bed(12.5);
        var graph = VoiceGraph.Decode(built.Bytes);
        Node<ReverbSpec>(graph, n.Reverb);
        Assert.Equal(400f, Node<FilterSpec>(graph, n.WindFilter).Frequency);
        Assert.Equal(FilterType.Bandpass, Node<FilterSpec>(graph, n.HowlBands[1]).Type);
        Assert.Equal(780f, Node<FilterSpec>(graph, n.HowlBands[1]).Frequency);
        Node<PanSpec>(graph, n.HowlPans[0]);
        Assert.All(n.Drones, drone => Assert.Equal(Wave.Sawtooth, Node<OscSpec>(graph, drone).Wave));
        Assert.Equal([-7f, 7f], n.Drones.Select(drone => Node<OscSpec>(graph, drone).Detune));
        // Вступление: громкость выхода с нуля за шесть секунд от начала голоса.
        var fade = graph.Automations.Where(a => a.Node == 0 && a.Param == ParamId.Gain).ToArray();
        Assert.Equal([(AutomationKind.Set, 0f, 12.5), (AutomationKind.Linear, 1f, 18.5)], fade.Select(a => (a.Kind, a.Value, a.Time)));

        var note = VoiceGraph.Decode(AmbienceAudio.Note(3, 220, 6, 12, 8, 0.8, new NoteColor(1, 1, 1)).Bytes);
        // Основной тон, подтон (эрг), дребезг (красные пустоши), октава-дубль и звон (солончаки).
        Assert.Equal(1 + 1 + 4 * 2 + 2, note.Nodes.Count);
        Assert.Contains(note.Automations, a => a.Kind == AutomationKind.Exponential && a.Value == 0.0001f);
        var plain = VoiceGraph.Decode(AmbienceAudio.Note(3, 220, 6, 12, 8, 0.8, default).Bytes);
        Assert.Equal(1 + 1 + 2 * 2, plain.Nodes.Count);
        var beat = VoiceGraph.Decode(AmbienceAudio.Beat(1, 55, 0.1).Bytes);
        Assert.Equal(2, beat.Nodes.OfType<OscSpec>().Count());
        // Даль: записи сквозь эхо основы — соединение уходит в узел другого голоса.
        var far = VoiceGraph.Decode(AmbienceAudio.Far("thunder", 1, 400, -0.5, 0.1, 0.2, new S.VoiceEndpoint(7, (ushort)n.Reverb)).Bytes);
        Assert.Contains(far.Connections, c => !c.To.IsLocal && c.To.Voice == 7 && c.To.Node == n.Reverb);
    }

    [Fact]
    public void EveryGraphPlaysInTheEngine()
    {
        var engine = new AudioEngine(new AudioEngineOptions { Seed = 11 });
        BankFixture.Fill(engine);
        var bed = engine.Voice(AmbienceAudio.Bed(engine.Now).Graph.Bytes);
        engine.Voice(FlightAudio.Voice);
        foreach (var (name, motor) in MotorSounds.Motors)
            engine.Voice(MotorSounds.Voice(name, motor.Running, motor.Running is null ? null : "squeal", 0.3f).Graph.Bytes);
        engine.Voice(AmbienceAudio.Note(engine.Now + 0.1, 220, 0.2, 0.5, 0.5, 0.8, new NoteColor(1, 1, 1)).Bytes, new VoiceOptions { Length = 1.3f });
        engine.Voice(AmbienceAudio.Beat(engine.Now + 0.1, 55, 0.2).Bytes, new VoiceOptions { Length = 1 });
        var left = engine.Render(24000).Left;
        Assert.True(left.Max(MathF.Abs) > 0.001f);
        Assert.All(left, value => Assert.True(float.IsFinite(value)));
        Assert.True(engine.ActiveVoices >= 8);
        GC.KeepAlive(bed);
    }

    [Fact]
    public void DriveModelMatchesTs()
    {
        foreach (var (name, rows) in DriveGolden.All)
        {
            var motor = MotorSounds.Motors[name];
            var handling = name switch { "petrol" => Handling.Petrol, "diesel" => Handling.Diesel, "heavy" => Handling.Heavy, _ => Handling.Air };
            var drive = new Drive(motor.Start is null);
            var events = new Dictionary<int, string>();
            var row = 0;
            for (var step = 0; step < 900; step++)
            {
                var target = step < 30 ? 0 : step < 180 ? 0.9 : step < 240 ? 0 : step < 300 ? 0.3 : step < 360 ? 1.5 : 0;
                var happened = drive.Step(motor, handling, target, 1 / 60.0);
                if (step % 12 != 0 && happened == DriveEvent.None)
                    continue;
                var expected = rows[row++];
                var where = $"{name}, шаг {step}";
                Assert.Equal(expected.Step, step);
                Assert.Equal(expected.Event, happened switch { DriveEvent.Start => "start", DriveEvent.Brake => "brake", _ => null });
                Assert.Equal(expected.Phase, drive.Phase.ToString().ToLowerInvariant());
                var sound = drive.Sound(motor, 0.8);
                void Close(double want, double got, string what) => Assert.True(Math.Abs(want - got) <= 1e-9, $"{where}: {what} {got} вместо {want}");
                Close(expected.Speed, drive.Speed, "скорость");
                Close(expected.Rpm, drive.Rpm, "обороты");
                Close(expected.Throttle, drive.Throttle, "газ");
                Assert.Equal(expected.Gear, drive.Gear);
                Close(expected.Rate, sound.Rate, "rate");
                Close(expected.Load, sound.Load, "load");
                Close(expected.Level, sound.Level, "level");
                Close(expected.Running, sound.Running, "running");
                Close(expected.Squeal, sound.Squeal, "squeal");
            }
            Assert.Equal(rows.Length, row);
        }
    }
}
