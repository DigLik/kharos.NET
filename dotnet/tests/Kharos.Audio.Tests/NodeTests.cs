using Kharos.Audio.Dsp;
using Kharos.Audio.Graph;

namespace Kharos.Audio.Tests;

public sealed class OscillatorTests
{
    private static float[] Render(Wave wave, float frequency, int frames = AudioFormat.SampleRate)
    {
        var engine = Offline.Linear();
        var s = new VoiceGraphBuilder();
        s.Osc(wave, frequency).To(s.Out);
        engine.Voice(s.Build());
        var left = Offline.Left(engine, frames);
        for (var i = 0; i < left.Length; i++)
            left[i] /= Offline.Center;
        return left;
    }

    [Theory]
    [InlineData(440)]
    [InlineData(1000)]
    [InlineData(3517)]
    public void SineHasFrequencyAndUnitAmplitude(int hz)
    {
        var x = Render(Wave.Sine, hz);
        Assert.InRange(Offline.RisingCrossings(x), hz - 1, hz + 1);
        Assert.Equal(1.0, Offline.Amplitude(x, hz), 4);
        Assert.Equal(1.0, Offline.Peak(x), 3);
        // Фаза по спецификации: синус начинается с нуля и растёт.
        Assert.Equal(0f, x[0], 6);
        Assert.True(x[1] > 0);
    }

    [Theory]
    [InlineData(Wave.Square, 4 / Math.PI)]
    [InlineData(Wave.Sawtooth, 2 / Math.PI)]
    [InlineData(Wave.Triangle, 8 / (Math.PI * Math.PI))]
    public void FundamentalMatchesFourierSeries(Wave wave, double fundamental)
    {
        var x = Render(wave, 200);
        Assert.Equal(fundamental, Offline.Amplitude(x, 200), 2);
        // Вторая гармоника у этих волн по спецификации — ноль (у пилы — 1/π, она не симметрична).
        var second = wave == Wave.Sawtooth ? 1 / Math.PI : 0;
        Assert.Equal(second, Offline.Amplitude(x, 400), 2);
        Assert.InRange(Offline.Peak(x), 0.95, 1.12);
    }

    [Fact]
    public void SawtoothIsBandLimited()
    {
        // 5 кГц: шестая гармоника (30 кГц) у наивной пилы отражается в 18 кГц с амплитудой 2/(6π) ≈ 0,106.
        var x = Render(Wave.Sawtooth, 5000);
        Assert.True(Offline.Amplitude(x, 18000) < 0.03, $"отражение {Offline.Amplitude(x, 18000)}");
        Assert.True(Offline.Amplitude(x, 15000) > 0.12, "третья гармоника на месте");
    }

    [Fact]
    public void LfoModulatesFrequency()
    {
        var engine = Offline.Linear();
        var s = new VoiceGraphBuilder();
        var osc = s.Osc(Wave.Sine, 1000f);
        s.Lfo(5f, 100f).To(osc[ParamId.Frequency]);
        osc.To(s.Out);
        engine.Voice(s.Build());
        var x = Offline.Left(engine, AudioFormat.SampleRate);
        double min = double.MaxValue, max = 0;
        var last = -1;
        for (var i = 1; i < x.Length; i++)
            if (x[i - 1] < 0 && x[i] >= 0)
            {
                if (last >= 0)
                {
                    var hz = AudioFormat.SampleRate / (double)(i - last);
                    min = Math.Min(min, hz);
                    max = Math.Max(max, hz);
                }
                last = i;
            }
        Assert.InRange(min, 880, 920);
        Assert.InRange(max, 1080, 1125);
    }
}

public sealed class BiquadTests
{
    private const double Rate = AudioFormat.SampleRate;

    [Fact]
    public void CoefficientsMatchAnalyticPoints()
    {
        const double f0 = 1000;
        var q = 3.0;
        // Lowpass и highpass: Q — в децибелах, на срезе |H| = 10^(Q/20).
        Assert.Equal(Math.Pow(10, q / 20), Biquad.Compute(FilterType.Lowpass, f0, q, 0, Rate).Magnitude(f0, Rate), 9);
        Assert.Equal(Math.Pow(10, q / 20), Biquad.Compute(FilterType.Highpass, f0, q, 0, Rate).Magnitude(f0, Rate), 9);
        Assert.Equal(1.0, Biquad.Compute(FilterType.Lowpass, f0, q, 0, Rate).Magnitude(0, Rate), 9);
        Assert.Equal(0.0, Biquad.Compute(FilterType.Lowpass, f0, q, 0, Rate).Magnitude(Rate / 2, Rate), 9);
        Assert.Equal(1.0, Biquad.Compute(FilterType.Highpass, f0, q, 0, Rate).Magnitude(Rate / 2, Rate), 9);
        Assert.Equal(1.0, Biquad.Compute(FilterType.Bandpass, f0, q, 0, Rate).Magnitude(f0, Rate), 9);
        Assert.Equal(0.0, Biquad.Compute(FilterType.Notch, f0, q, 0, Rate).Magnitude(f0, Rate), 9);
        foreach (var hz in new[] { 50.0, 1000, 7000, 20000 })
            Assert.Equal(1.0, Biquad.Compute(FilterType.Allpass, f0, q, 0, Rate).Magnitude(hz, Rate), 9);
        const double gain = 12;
        var a = Math.Pow(10, gain / 40);
        Assert.Equal(a * a, Biquad.Compute(FilterType.Peaking, f0, q, gain, Rate).Magnitude(f0, Rate), 9);
        Assert.Equal(a * a, Biquad.Compute(FilterType.Lowshelf, f0, q, gain, Rate).Magnitude(0, Rate), 9);
        Assert.Equal(a, Biquad.Compute(FilterType.Lowshelf, f0, q, gain, Rate).Magnitude(f0, Rate), 9);
        Assert.Equal(1.0, Biquad.Compute(FilterType.Lowshelf, f0, q, gain, Rate).Magnitude(Rate / 2, Rate), 9);
        Assert.Equal(a * a, Biquad.Compute(FilterType.Highshelf, f0, q, gain, Rate).Magnitude(Rate / 2, Rate), 9);
        Assert.Equal(a, Biquad.Compute(FilterType.Highshelf, f0, q, gain, Rate).Magnitude(f0, Rate), 9);
        Assert.Equal(1.0, Biquad.Compute(FilterType.Highshelf, f0, q, gain, Rate).Magnitude(0, Rate), 9);
    }

    [Theory]
    [InlineData(FilterType.Lowpass)]
    [InlineData(FilterType.Highpass)]
    [InlineData(FilterType.Bandpass)]
    [InlineData(FilterType.Lowshelf)]
    [InlineData(FilterType.Highshelf)]
    [InlineData(FilterType.Peaking)]
    [InlineData(FilterType.Notch)]
    [InlineData(FilterType.Allpass)]
    public void RenderedResponseMatchesMagnitude(FilterType type)
    {
        const float f0 = 1200f, q = 2f, gain = -9f;
        var coefficients = Biquad.Compute(type, f0, q, gain, Rate);
        foreach (var hz in new[] { 100, 1200, 2500, 9000 })
        {
            var engine = Offline.Linear();
            var s = new VoiceGraphBuilder();
            s.Osc(Wave.Sine, hz).To(s.Filter(type, f0, q, gain)).To(s.Out);
            engine.Voice(s.Build());
            var x = Offline.Left(engine, 2 * AudioFormat.SampleRate).AsSpan(AudioFormat.SampleRate);
            var measured = Offline.Amplitude(x, hz) / Offline.Center;
            Assert.True(Math.Abs(measured - coefficients.Magnitude(hz, Rate)) < 2e-4,
                $"{type} на {hz} Гц: {measured} вместо {coefficients.Magnitude(hz, Rate)}");
        }
    }

    [Fact]
    public void AutomatedFrequencyFollowsParam()
    {
        // Срез lowpass уходит с 200 Гц на 8 кГц: тон 2 кГц сперва глушится, потом проходит.
        var engine = Offline.Linear();
        var s = new VoiceGraphBuilder();
        var filter = s.Filter(FilterType.Lowpass, 200f, 0f);
        filter[ParamId.Frequency].Set(200f, 0).Set(8000f, 0.5);
        s.Osc(Wave.Sine, 2000f).To(filter).To(s.Out);
        engine.Voice(s.Build());
        var x = Offline.Left(engine, AudioFormat.SampleRate);
        var before = Offline.Amplitude(x.AsSpan(4800, 14400), 2000) / Offline.Center;
        var after = Offline.Amplitude(x.AsSpan(36000, 12000), 2000) / Offline.Center;
        Assert.Equal(Biquad.Compute(FilterType.Lowpass, 200, 0, 0, Rate).Magnitude(2000, Rate), before, 3);
        Assert.Equal(Biquad.Compute(FilterType.Lowpass, 8000, 0, 0, Rate).Magnitude(2000, Rate), after, 3);
    }
}

public sealed class PannerTests
{
    [Theory]
    [InlineData(-1f)]
    [InlineData(-0.5f)]
    [InlineData(0f)]
    [InlineData(0.3f)]
    [InlineData(1f)]
    public void MonoInputIsEqualPower(float pan)
    {
        var engine = Offline.Linear();
        var s = new VoiceGraphBuilder();
        s.Const(1f).To(s.Pan(pan)).To(s.Out);
        engine.Voice(s.Build());
        var (left, right) = engine.Render(256);
        var x = (pan + 1) / 2;
        Assert.Equal(Math.Cos(x * Math.PI / 2), left[200], 6);
        Assert.Equal(Math.Sin(x * Math.PI / 2), right[200], 6);
        Assert.Equal(1.0, left[200] * left[200] + right[200] * right[200], 6);
    }

    [Fact]
    public void StereoInputFollowsSpecFormula()
    {
        // Вход: стерео L = 1, R = 0 (моно на краю). Панорама 0,5: L·cos(π/4) и R + L·sin(π/4).
        var engine = Offline.Linear();
        var s = new VoiceGraphBuilder();
        s.Const(1f).To(s.Pan(-1f)).To(s.Pan(0.5f)).To(s.Out);
        engine.Voice(s.Build());
        var (left, right) = engine.Render(256);
        Assert.Equal(Math.Cos(Math.PI / 4), left[200], 6);
        Assert.Equal(Math.Sin(Math.PI / 4), right[200], 6);
        // Влево (−0,5): L + R·cos(x·π/2), R·sin(x·π/2) при x = 0,5 — вход R = 0, значит L = 1, R = 0.
        engine = Offline.Linear();
        s = new VoiceGraphBuilder();
        s.Const(1f).To(s.Pan(-1f)).To(s.Pan(-0.5f)).To(s.Out);
        engine.Voice(s.Build());
        (left, right) = engine.Render(256);
        Assert.Equal(1f, left[200], 6);
        Assert.Equal(0f, right[200], 6);
    }
}

public sealed class AutomationTests
{
    [Fact]
    public void CurvesFollowSpecFormulas()
    {
        var engine = Offline.Linear();
        var s = new VoiceGraphBuilder();
        var gain = s.Gain(0f);
        s.Const(1f).To(gain).To(s.Out);
        gain[ParamId.Gain]
            .Set(0.2f, 0.01)
            .Ramp(1f, 0.05)
            .Exp(0.01f, 0.1)
            .Target(0.5f, 0.12, 0.02f);
        engine.Voice(s.Build());
        var x = Offline.Left(engine, 9600);
        for (var i = 0; i < x.Length; i++)
        {
            var t = i / (double)AudioFormat.SampleRate;
            var expected = t < 0.01 ? 0.0
                : t < 0.05 ? 0.2 + 0.8 * (t - 0.01) / 0.04
                : t < 0.1 ? Math.Pow(0.01, (t - 0.05) / 0.05)
                : t < 0.12 ? 0.01
                : 0.5 + (0.01 - 0.5) * Math.Exp(-(t - 0.12) / 0.02);
            Assert.True(Math.Abs(x[i] / Offline.Center - expected) < 2e-5, $"кадр {i}: {x[i] / Offline.Center} вместо {expected}");
        }
    }

    [Fact]
    public void CommandsApplyAtQuantumAndCancel()
    {
        var engine = Offline.Linear();
        var s = new VoiceGraphBuilder();
        s.Const(1f).To(s.Out);
        var sound = engine.Voice(s.Build());
        engine.Render(1280);
        // Рампа без предыдущего события начинается с нынешнего значения в миг вызова.
        sound.Ramp(0, ParamId.Gain, 0f, engine.Now + 0.01);
        sound.Set(0, ParamId.Gain, 5f, engine.Now + 1);
        sound.Cancel(0, ParamId.Gain, engine.Now + 0.5);
        var x = Offline.Left(engine, 96000);
        Assert.Equal(Offline.Center, x[0], 5);
        Assert.Equal(Offline.Center * 0.5f, x[240], 4);
        Assert.Equal(0f, x[480], 6);
        Assert.Equal(0f, x[95000], 6);
    }

    [Fact]
    public void ExponentialBetweenSignsHolds()
    {
        var timeline = new ParamTimeline(0f);
        timeline.Insert(AutomationKind.Set, -1f, 0, 0, 0);
        timeline.Insert(AutomationKind.Exponential, 1f, 1, 0, 0);
        Assert.Equal(-1f, timeline.At(0.5));
        Assert.Equal(1f, timeline.At(1.0));
    }

    [Fact]
    public void EnvelopeFollowsVoiceTiming()
    {
        var engine = Offline.Linear();
        var s = new VoiceGraphBuilder();
        s.Const(1f).To(s.Env(0.01f, 0.1f, 0.5f, 0.2f)).To(s.Out);
        var sound = engine.Voice(s.Build(), new VoiceOptions { Length = 0.3f, Velocity = 0.8f });
        var x = Offline.Left(engine, AudioFormat.SampleRate * 6 / 10);
        Assert.Equal(0.4, x[240] / Offline.Center, 4);
        Assert.Equal(0.8 * 0.5, x[(int)(0.2 * AudioFormat.SampleRate)] / Offline.Center, 4);
        Assert.Equal(0.2, x[(int)(0.4 * AudioFormat.SampleRate)] / Offline.Center, 4);
        Assert.Equal(0f, x[(int)(0.52 * AudioFormat.SampleRate)], 6);
        Assert.True(sound.IsFinished);
    }
}

public sealed class DelayTests
{
    private static SampleBuffer Impulse()
    {
        var data = new float[64];
        data[0] = 1f;
        return new SampleBuffer(data);
    }

    [Fact]
    public void DelayShiftsSignal()
    {
        var engine = Offline.Linear();
        engine.Bank.Add("click", Bus.World, Impulse());
        var s = new VoiceGraphBuilder();
        s.Sample("click").To(s.Delay(0.01f, 1f)).To(s.Out);
        engine.Voice(s.Build());
        var x = Offline.Left(engine, 2048);
        Assert.Equal(Offline.Center, x[480], 6);
        Assert.Equal(Offline.Center, (float)Offline.Peak(x), 6);
    }

    [Fact]
    public void FeedbackThroughDelayEchoes()
    {
        var engine = Offline.Linear();
        engine.Bank.Add("click", Bus.World, Impulse());
        var s = new VoiceGraphBuilder();
        var delay = s.Delay(0.01f, 1f);
        var feedback = s.Gain(0.5f);
        s.Sample("click").To(delay);
        delay.To(feedback).To(delay);
        delay.To(s.Out);
        engine.Voice(s.Build());
        var x = Offline.Left(engine, 4096);
        Assert.Equal(Offline.Center, x[480], 6);
        Assert.Equal(Offline.Center * 0.5f, x[960], 6);
        Assert.Equal(Offline.Center * 0.25f, x[1440], 6);
    }

    [Fact]
    public void CycleWithoutDelayIsMuted()
    {
        var engine = Offline.Linear();
        var s = new VoiceGraphBuilder();
        var a = s.Gain(1f);
        var b = s.Gain(1f);
        s.Const(1f).To(a).To(b).To(a);
        b.To(s.Out);
        engine.Voice(s.Build());
        Assert.Equal(0.0, Offline.Peak(Offline.Left(engine, 1024)));
    }
}

public sealed class ShaperTests
{
    [Fact]
    public void CurveIsInterpolated()
    {
        var engine = Offline.Linear();
        var s = new VoiceGraphBuilder();
        s.Const(0.25f).To(s.Shape([0f, 0f, 1f])).To(s.Out);
        engine.Voice(s.Build());
        // x = 0,25: v = (3 − 1)·(0,25 + 1)/2 = 1,25 → 0,25 между точками 1 и 2.
        Assert.Equal(0.25f * Offline.Center, Offline.Left(engine, 256)[100], 6);
    }
}
