using System.Buffers.Binary;
using Kharos.Audio.Graph;

namespace Kharos.Audio.Tests;

public sealed class GraphCodecTests
{
    private static VoiceGraph Everything()
    {
        var s = new VoiceGraphBuilder();
        var osc = s.Osc(Wave.Sawtooth, 110f, -7f);
        var noise = s.Noise(NoiseColor.Pink);
        var sample = s.Sample("motor:petrol:idle", loop: true, offset: 0.3f, variant: 2, rate: 1.25f);
        var reverb = s.Reverb("reverb", normalize: false);
        var constant = s.Const(0.5f);
        var filter = s.Filter(FilterType.Peaking, 900f, 1.5f, 6f);
        var gain = s.Gain(0.25f);
        var delay = s.Delay(0.125f, 1f);
        var shape = s.Shape(0.7f);
        var curve = s.Shape([-1f, 0f, 0.5f, 1f]);
        var pan = s.Pan(-0.4f);
        var lfo = s.Lfo(5f, 20f, Wave.Triangle);
        var env = s.Env(0.02f, 0.2f, 0.6f, 0.4f, 0.9f);
        var compressor = s.Compressor(-30f, 10f, 4f, 0.01f, 0.1f);
        osc.To(filter).To(gain).To(shape).To(curve).To(delay).To(pan).To(env).To(compressor).To(s.Out);
        noise.To(reverb).To(s.Out);
        sample.To(s.Out);
        constant.To(gain[ParamId.Gain]);
        lfo.To(osc[ParamId.Frequency]);
        filter.To(new Endpoint(42, 3, ParamId.Q));
        s.Out[ParamId.Gain].Set(0f, double.NaN).Ramp(1f, 0.5).Exp(0.01f, 1.5).Target(0.3f, 2.0, 0.1f).Cancel(3.0);
        filter[ParamId.Frequency].Set(400f, 0.25);
        return s.Build();
    }

    [Fact]
    public void RoundTripKeepsEverything()
    {
        var graph = Everything();
        var bytes = graph.Encode();
        var decoded = VoiceGraph.Decode(bytes);
        Assert.Equal(graph.Nodes, decoded.Nodes);
        Assert.Equal(graph.Connections, decoded.Connections);
        Assert.Equal(graph.Automations.Count, decoded.Automations.Count);
        for (var i = 0; i < graph.Automations.Count; i++)
        {
            var (a, b) = (graph.Automations[i], decoded.Automations[i]);
            Assert.Equal((a.Node, a.Param, a.Kind, a.Value, a.TimeConstant), (b.Node, b.Param, b.Kind, b.Value, b.TimeConstant));
            Assert.Equal(double.IsNaN(a.Time), double.IsNaN(b.Time));
            if (!double.IsNaN(a.Time))
                Assert.Equal(a.Time, b.Time);
        }
        Assert.Equal(bytes, decoded.Encode());
    }

    [Fact]
    public void HeaderIsLittleEndian()
    {
        var bytes = Everything().Encode();
        Assert.Equal("KHVG"u8.ToArray(), bytes[..4]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(15, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));
        // Узел 0 — выход: Gain (1) и f32 1.0.
        Assert.Equal(1, bytes[12]);
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(13)));
    }

    [Fact]
    public void BrokenRecordsAreRejected()
    {
        var bytes = Everything().Encode();
        Assert.Throws<FormatException>(() => VoiceGraph.Decode(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<FormatException>(() => VoiceGraph.Decode([.. bytes, 0]));
        var magic = bytes.ToArray();
        magic[0] ^= 1;
        Assert.Throws<FormatException>(() => VoiceGraph.Decode(magic));
        var version = bytes.ToArray();
        version[4] = 9;
        Assert.Throws<FormatException>(() => VoiceGraph.Decode(version));
        var kind = bytes.ToArray();
        kind[17] = 99;
        Assert.Throws<FormatException>(() => VoiceGraph.Decode(kind));
    }

    [Fact]
    public void InvalidGraphsAreRejected()
    {
        Assert.Throws<FormatException>(() => new VoiceGraph([new OscSpec()], [], []));
        Assert.Throws<FormatException>(() => new VoiceGraph([new GainSpec(), new OscSpec()], [new Connection(0, new Endpoint(0, 1))], []));
        Assert.Throws<FormatException>(() => new VoiceGraph([new GainSpec()], [new Connection(0, new Endpoint(0, 5))], []));
        Assert.Throws<FormatException>(() => new VoiceGraph([new GainSpec()], [new Connection(0, new Endpoint(0, 0, ParamId.Pan))], []));
        Assert.Throws<FormatException>(() => new VoiceGraph([new GainSpec()], [], [new Automation(0, ParamId.Frequency, AutomationKind.Set, 1f, 0)]));
        Assert.Throws<FormatException>(() => new VoiceGraph([new GainSpec()], [], [new Automation(0, ParamId.Gain, AutomationKind.Exponential, 0f, 1)]));
    }
}
