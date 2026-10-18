using System.Diagnostics;
using Kharos.Audio.Graph;
using Kharos.Audio.Synthesis;

namespace Kharos.Audio.Tests;

public sealed class OpenAlTests
{
    /// <summary>
    /// Только инициализация: устройство, контекст и источник с громкостью 0 без буферов — и сразу закрыть. Звука нет.
    /// Нет устройства или библиотеки (CI) — пропуск.
    /// </summary>
    [Fact]
    public void DeviceOpensAndClosesSilently()
    {
        if (!OpenAlOutput.Probe(out var device, out var error))
            Assert.Skip($"OpenAL недоступен: {error}");
        Assert.False(string.IsNullOrEmpty(device));
        TestContext.Current.TestOutputHelper?.WriteLine($"устройство: {device}");
    }
}

public sealed class CostTests
{
    /// <summary>
    /// Замер: 32 голоса (шум или пила → 2 фильтра с LFO на срезе → усиление → панорама) и общий голос эха с откликом
    /// 4 с стерео, в который идут все 32. Печатает мс на квант 128 кадров; квант длится 2,67 мс.
    /// </summary>
    [Fact]
    public void ThirtyTwoVoicesWithFiltersAndReverb()
    {
        var engine = new AudioEngine(new AudioEngineOptions { Seed = 5 });
        engine.Bank.Add("reverb", Bus.World, new SampleBuffer(Synth.ReverbImpulse(AudioFormat.SampleRate, 4, 1)));
        var bed = new VoiceGraphBuilder();
        bed.Reverb("reverb").To(bed.Gain(0.3f)).To(bed.Out);
        var reverb = engine.Voice(bed.Build());
        for (var i = 0; i < 32; i++)
        {
            var s = new VoiceGraphBuilder();
            var source = i % 2 == 0 ? s.Noise(NoiseColor.Pink) : s.Osc(Wave.Sawtooth, 55f * (1 + i % 7));
            var low = s.Filter(FilterType.Lowpass, 800f + 50 * i, 2f);
            var band = s.Filter(FilterType.Peaking, 1500f, 1f, 6f);
            s.Lfo(0.3f + i * 0.05f, 300f).To(low[ParamId.Frequency]);
            var pan = s.Pan(i / 16f - 1f);
            source.To(low).To(band).To(s.Gain(0.05f)).To(pan).To(s.Out);
            pan.To(reverb.Node(1));
            engine.Voice(s.Build(), new VoiceOptions { Bus = Bus.World });
        }
        var (average, worst, allocated) = Measure(engine);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"32 голоса + эхо 4 с: {average:F3} мс на квант в среднем, худший {worst:F3} мс (квант — 2,667 мс), " +
            $"выделено {allocated} байт за {Quanta} квантов, голосов {engine.ActiveVoices}");
        Assert.Equal(0, allocated);
        Assert.Equal(33, engine.ActiveVoices);
        Assert.True(average < 2.667, $"{average} мс на квант — не успевает");
    }

    /// <summary>Только эхо: стереовход в отклик 4 с стерео.</summary>
    [Fact]
    public void ReverbAlone()
    {
        var engine = new AudioEngine(new AudioEngineOptions { Seed = 5 });
        engine.Bank.Add("reverb", Bus.World, new SampleBuffer(Synth.ReverbImpulse(AudioFormat.SampleRate, 4, 1)));
        var s = new VoiceGraphBuilder();
        s.Noise(NoiseColor.Pink).To(s.Pan(0.3f)).To(s.Reverb("reverb")).To(s.Out);
        engine.Voice(s.Build());
        var (average, worst, _) = Measure(engine);
        TestContext.Current.TestOutputHelper?.WriteLine($"эхо 4 с, стерео: {average:F3} мс на квант, худший {worst:F3} мс");
        Assert.True(average < 2.667);
    }

    private const int Quanta = 3000;

    private static (double Average, double Worst, long Allocated) Measure(AudioEngine engine)
    {
        var buffer = new float[2 * AudioFormat.Quantum];
        for (var i = 0; i < 200; i++)
            engine.Render(buffer);
        var clock = Stopwatch.StartNew();
        var worst = 0.0;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Quanta; i++)
        {
            var start = clock.Elapsed.TotalMilliseconds;
            engine.Render(buffer);
            worst = Math.Max(worst, clock.Elapsed.TotalMilliseconds - start);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        return (clock.Elapsed.TotalMilliseconds / Quanta, worst, allocated);
    }
}
