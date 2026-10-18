using Kharos.Audio.Dsp;
using Kharos.Audio.Graph;
using Kharos.Audio.Synthesis;

namespace Kharos.Audio.Tests;

public sealed class ConvolverTests
{
    private static float[] Noise(int length, int seed)
    {
        var random = Synth.Random(seed);
        var data = new float[length];
        for (var i = 0; i < data.Length; i++)
            data[i] = (float)(random() * 2 - 1) * 0.1f;
        return data;
    }

    private static float[] Convolve(SampleBuffer input, float[] response, int length)
    {
        var x = input.Channel(0);
        var y = new float[length];
        for (var n = 0; n < length; n++)
        {
            double sum = 0;
            for (var k = 0; k < response.Length && k <= n; k++)
                if (n - k < x.Length)
                    sum += response[k] * (double)x[n - k];
            y[n] = (float)sum;
        }
        return y;
    }

    private static float[] Render(SampleBuffer input, SampleBuffer response, bool normalize, int frames)
    {
        var engine = Offline.Linear();
        engine.Bank.Add("in", Bus.World, input);
        engine.Bank.Add("ir", Bus.World, response);
        var s = new VoiceGraphBuilder();
        s.Sample("in").To(s.Reverb("ir", normalize)).To(s.Out);
        engine.Voice(s.Build());
        var x = Offline.Left(engine, frames);
        for (var i = 0; i < x.Length; i++)
            x[i] /= Offline.Center;
        return x;
    }

    [Fact]
    public void ImpulseResponseOfOneIsIdentity()
    {
        var input = new SampleBuffer(Noise(3000, 3));
        var x = Render(input, new SampleBuffer([1f]), normalize: false, 4096);
        for (var i = 0; i < 3000; i++)
            Assert.True(Math.Abs(x[i] - input.Channel(0)[i]) < 1e-5, $"кадр {i}");
        Assert.True(Offline.Peak(x.AsSpan(3000)) < 1e-5);
    }

    [Fact]
    public void LongResponseMatchesDirectConvolution()
    {
        var input = new SampleBuffer(Noise(2500, 5));
        var response = Noise(1700, 9);
        for (var i = 0; i < response.Length; i++)
            response[i] *= (float)Math.Exp(-i / 400.0);
        var x = Render(input, new SampleBuffer(response), normalize: false, 4608);
        var expected = Convolve(input, response, 4608);
        for (var i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(x[i] - expected[i]) < 1e-5, $"кадр {i}: {x[i]} вместо {expected[i]}");
    }

    [Fact]
    public void NormalizationFollowsSpec()
    {
        var response = Noise(4800, 11);
        var buffer = new SampleBuffer(response);
        double power = 0;
        foreach (var value in response)
            power += value * (double)value;
        var expected = 0.00125 / Math.Sqrt(power / response.Length) * 44100 / 48000;
        Assert.Equal(expected, ConvolutionKernel.NormalizationScale(buffer), 5);
        // Нормированный отклик-импульс: выход = вход · scale.
        var delta = new SampleBuffer([1f]);
        var x = Render(new SampleBuffer([1f, 0.5f]), delta, normalize: true, 256);
        var scale = ConvolutionKernel.NormalizationScale(delta);
        Assert.Equal(scale, x[0], 3);
        Assert.Equal(scale * 0.5f, x[1], 3);
    }

    [Fact]
    public void StereoResponseSplitsChannels()
    {
        var engine = Offline.Linear();
        engine.Bank.Add("in", Bus.World, new SampleBuffer([1f]));
        var left = new float[300];
        var right = new float[300];
        left[0] = 1f;
        right[200] = 0.5f;
        engine.Bank.Add("ir", Bus.World, new SampleBuffer([left, right]));
        var s = new VoiceGraphBuilder();
        s.Sample("in").To(s.Reverb("ir", normalize: false)).To(s.Out);
        engine.Voice(s.Build());
        var (l, r) = engine.Render(512);
        Assert.Equal(1f, l[0], 5);
        Assert.Equal(0f, r[0], 5);
        Assert.Equal(0.5f, r[200], 5);
        Assert.Equal(0f, l[200], 5);
    }
}

public sealed class CompressorTests
{
    private static (float[] Output, float Reduction) Master(float amplitude, bool compressor)
    {
        var engine = new AudioEngine(new AudioEngineOptions { Seed = 1, Master = 1f, Compressor = compressor });
        var s = new VoiceGraphBuilder();
        s.Osc(Wave.Sine, 500f).To(s.Gain(amplitude)).To(s.Out);
        engine.Voice(s.Build());
        var x = Offline.Left(engine, AudioFormat.SampleRate);
        return (x, engine.Reduction);
    }

    /// <summary>Узел компрессора с настройками по умолчанию (−24 дБ, колено 30, 6:1 → 12:1), громкость на выходе — через 0,1.</summary>
    private static double Gain(float amplitude)
    {
        var engine = Offline.Linear();
        var s = new VoiceGraphBuilder();
        s.Osc(Wave.Sine, 500f).To(s.Gain(amplitude)).To(s.Compressor()).To(s.Gain(0.1f)).To(s.Out);
        engine.Voice(s.Build());
        var x = Offline.Left(engine, AudioFormat.SampleRate);
        return Offline.Amplitude(x.AsSpan(24000), 500) / (amplitude * 0.1 * Offline.Center);
    }

    [Fact]
    public void LoudSignalIsCompressed()
    {
        var loud = Gain(8f);
        var quiet = Gain(0.01f);
        Assert.True(20 * Math.Log10(quiet / loud) > 10, $"тихий {quiet}, громкий {loud}");
        // Тихое проходит с компенсирующим усилением (1/curve(1))^0,6 > 1.
        Assert.InRange(quiet, 1.0, 3.0);
    }

    [Fact]
    public void MasterCompressorReducesPeaks()
    {
        var (compressed, reduction) = Master(1f, compressor: true);
        var (bypass, _) = Master(1f, compressor: false);
        Assert.True(reduction < -1f, $"сжатие {reduction} дБ");
        Assert.True(Offline.Peak(compressed.AsSpan(24000)) < Offline.Peak(bypass.AsSpan(24000)));
        Assert.True(Offline.Peak(compressed) <= 1f);
    }
}
