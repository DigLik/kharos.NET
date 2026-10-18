using Kharos.Audio.Dsp;
using Kharos.Audio.Graph;

namespace Kharos.Audio.Engine;

/// <summary>Звучание голоса в аудиопотоке: узлы графа, громкость и панорама звучания (place), срок жизни.</summary>
internal sealed class Voice
{
    public Voice(uint id, Node[] nodes, Bus bus, Endpoint? into, bool oneShot)
    {
        Id = id;
        Nodes = nodes;
        Bus = bus;
        Into = into;
        OneShot = oneShot;
        Place = new GainNode(1f) { Owner = this };
        PlacePan = new PanNode(0f) { Owner = this };
        foreach (var node in nodes)
        {
            node.Owner = this;
            if (node is SourceNode source)
                Sources.Add(source);
            Tail += node.TailFrames;
        }
        Tail += 2 * AudioFormat.Quantum;
        nodes[0].Connect(Place);
        Place.Connect(PlacePan);
    }

    public uint Id { get; }

    /// <summary>Узлы графа; 0 — выход голоса.</summary>
    public Node[] Nodes { get; }

    public Bus Bus { get; }

    /// <summary>Куда вместо шины идёт звучание: узел другого голоса.</summary>
    public Endpoint? Into { get; }

    /// <summary>Разовый звук банка (идёт в счёт предела).</summary>
    public bool OneShot { get; }

    public GainNode Place { get; }

    public PanNode PlacePan { get; }

    public List<SourceNode> Sources { get; } = [];

    /// <summary>Соединения с узлами других голосов: связываются в аудиопотоке.</summary>
    public List<Connection> External { get; } = [];

    /// <summary>Кадр, с которого голос снимается.</summary>
    public long EndFrame = long.MaxValue;

    /// <summary>Сколько кадров голос может звучать после конца источников.</summary>
    public long Tail;

    /// <summary>Сколько кадров источники молчат.</summary>
    public long Quiet;

    public bool Released;

    /// <summary>Голос снят с графа (пишет аудиопоток, читает поток игры).</summary>
    public volatile bool Finished;

    public IEnumerable<Node> AllNodes()
    {
        foreach (var node in Nodes)
            yield return node;
        yield return Place;
        yield return PlacePan;
    }

    /// <summary>Параметр узла index; 0xFFFF/0xFFFE — громкость и панорама звучания.</summary>
    public Param? GetParam(ushort index, ParamId id) => index switch
    {
        PlaceIndex => id == ParamId.Gain ? Place.Gain : null,
        PanIndex => id == ParamId.Pan ? PlacePan.Pan : null,
        _ when index < Nodes.Length => Nodes[index].GetParam(id),
        _ => null,
    };

    public const ushort PlaceIndex = 0xFFFF;
    public const ushort PanIndex = 0xFFFE;
}

/// <summary>Что нужно для сборки голоса: банк, шумы, случайность (поток игры).</summary>
internal sealed record VoiceContext(SampleBank Bank, SampleBuffer[] Noise, Func<double> Random);

/// <summary>Сборка голоса из описания в потоке игры: аудиопоток получает готовые узлы.</summary>
internal static class VoiceFactory
{
    /// <summary>
    /// Голос по графу. at — начало (источники стартуют в at), length — длина (NaN — пока не отпустят), note — частота
    /// осцилляторов без частоты, velocity — сила огибающих; now — время контекста в миг вызова.
    /// </summary>
    public static Voice Create(uint id, VoiceGraph graph, VoiceOptions options, VoiceContext context, double now)
    {
        var at = double.IsNaN(options.At) ? now : options.At;
        var nodes = new Node[graph.Nodes.Count];
        double release = 0;
        var envelopes = new List<(int Index, EnvSpec Spec)>();
        for (var i = 0; i < nodes.Length; i++)
        {
            Node node = graph.Nodes[i] switch
            {
                GainSpec gain => new GainNode(gain.Gain),
                OscSpec osc => new OscNode(osc.Wave, float.IsNaN(osc.Frequency) ? options.Note : osc.Frequency, osc.Detune),
                NoiseSpec noise => new BufferNode(context.Noise[(int)noise.Color], loop: true, 0, 1f, 0f),
                SampleSpec sample => Sample(sample, context),
                ReverbSpec reverb => new ConvolverNode(Record(context, reverb.Name).Variants[0], reverb.Normalize),
                ConstSpec constant => new ConstNode(constant.Offset),
                FilterSpec filter => new FilterNode(filter.Type, filter.Frequency, filter.Q, filter.Gain, filter.Detune),
                DelaySpec delay => new DelayNode(delay.Time, delay.MaxTime),
                ShapeSpec shape => new ShapeNode(shape.ResolveCurve()),
                PanSpec pan => new PanNode(pan.Pan),
                LfoSpec lfo => new OscNode(lfo.Wave, lfo.Frequency, 0f, lfo.Depth),
                EnvSpec env => new GainNode(0f),
                CompressorSpec compressor => new CompressorNode(compressor.Threshold, compressor.Knee, compressor.Ratio, compressor.Attack, compressor.Release),
                var other => throw new InvalidOperationException($"узел {other.Kind} не собирается"),
            };
            if (node is SourceNode source)
                source.Start(at);
            if (graph.Nodes[i] is EnvSpec envelope)
                envelopes.Add((i, envelope));
            nodes[i] = node;
        }
        // Огибающая: в at поднимается до peak·velocity за attack, к sustain — за decay; в at + length гаснет за release.
        foreach (var (index, spec) in envelopes)
        {
            var param = nodes[index].GetParam(ParamId.Gain)!.Timeline;
            var peak = spec.Peak * options.Velocity;
            param.Insert(AutomationKind.Set, 0f, at, 0, now);
            param.Insert(AutomationKind.Linear, peak, at + spec.Attack, 0, now);
            param.Insert(AutomationKind.Linear, peak * spec.Sustain, at + spec.Attack + spec.Decay, 0, now);
            if (!float.IsNaN(options.Length))
            {
                var end = at + Math.Max(options.Length, spec.Attack + spec.Decay);
                param.Insert(AutomationKind.Set, peak * spec.Sustain, end, 0, now);
                param.Insert(AutomationKind.Linear, 0f, end + spec.Release, 0, now);
                release = Math.Max(release, spec.Release);
            }
        }
        var voice = new Voice(id, nodes, options.Bus, options.Into, oneShot: false);
        foreach (var connection in graph.Connections)
        {
            if (!connection.To.IsLocal)
            {
                voice.External.Add(connection);
                continue;
            }
            var source = nodes[connection.From];
            var target = nodes[connection.To.Node];
            if (connection.To.Param is { } id2)
                source.Connect(target.GetParam(id2)!);
            else
                source.Connect(target);
        }
        foreach (var automation in graph.Automations)
        {
            var time = double.IsNaN(automation.Time) ? at : automation.Time;
            nodes[automation.Node].GetParam(automation.Param)!.Timeline.Insert(automation.Kind, automation.Value, time, automation.TimeConstant, now);
        }
        if (!float.IsNaN(options.Length))
        {
            var end = at + options.Length + release + 0.05;
            foreach (var source in voice.Sources)
                source.Stop(end);
            voice.EndFrame = Frames.Of(end);
        }
        return voice;
    }

    /// <summary>Разовая запись: источник → громкость → панорама → шина.</summary>
    public static Voice OneShot(uint id, SampleBuffer buffer, Bus bus, float volume, float pan, float rate, double now)
    {
        var source = new BufferNode(buffer, loop: false, 0, rate, 0f);
        source.Start(now);
        var gain = new GainNode(volume);
        var voice = new Voice(id, [gain, source], bus, null, oneShot: true);
        source.Connect(gain);
        voice.PlacePan.Pan.Timeline.Insert(AutomationKind.Set, Math.Clamp(pan, -1f, 1f), now, 0, now);
        return voice;
    }

    private static SampleRecord Record(VoiceContext context, string name) =>
        context.Bank.TryGet(name, out var record) ? record : throw new KeyNotFoundException($"нет записи {name}");

    private static BufferNode Sample(SampleSpec spec, VoiceContext context)
    {
        var record = Record(context, spec.Name);
        var count = record.Variants.Count;
        var variant = spec.Variant >= 0 ? Math.Min(spec.Variant, count - 1) : Math.Min((int)(context.Random() * count), count - 1);
        var buffer = record.Variants[variant];
        // Без смещения петля начинается со случайного места: одинаковые петли не звучат в такт.
        var offset = float.IsNaN(spec.Offset) ? spec.Loop ? context.Random() * buffer.Duration : 0 : spec.Offset;
        return new BufferNode(buffer, spec.Loop, offset, spec.Rate, spec.Detune);
    }
}
