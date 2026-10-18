using Kharos.Audio.Graph;
using Kharos.Audio.Synthesis;

namespace Kharos.Audio.Tests;

/// <summary>Эталон synth.ts: каждый stride-й отсчёт, длина и сумма модулей.</summary>
internal sealed record Golden(int Length, double Sum, int Stride, float[] Samples);

public sealed class SynthTests
{
    private const double Rate = AudioFormat.SampleRate;

    /// <summary>Винтовка из synth.ts — сухой щелчок и короткий хлопок.</summary>
    private static float[] Rifle(double rate, int variant)
    {
        var crack = Synth.Highpass(rate, 2500);
        var body = Synth.Lowpass(rate, () => 900);
        return Synth.Render(rate, 0.2, variant, (t, noise) =>
        {
            var n = noise();
            return crack(n) * Synth.Decay(t, 0.01) * 0.9 + body(n) * Synth.Decay(t, 0.05) * 1.6 + Math.Sin(2 * Math.PI * 140 * t) * Synth.Decay(t, 0.025) * 0.35;
        });
    }

    /// <summary>Пушка из synth.ts — удар, бас вниз, раскат.</summary>
    private static float[] Cannon(double rate, int variant)
    {
        var crack = Synth.Highpass(rate, 1500);
        var rumble = Synth.Lowpass(rate, () => 320);
        var phase = 0.0;
        return Synth.Render(rate, 1.3, variant, (t, noise) =>
        {
            var n = noise();
            phase += 2 * Math.PI * (40 + 60 * Synth.Decay(t, 0.12)) / rate;
            return crack(n) * Synth.Decay(t, 0.012) * 0.8 + Math.Sin(phase) * Synth.Decay(t, 0.3) * 0.8 + rumble(n) * Synth.Decay(t, 0.4) * 2.4;
        });
    }

    private static void Match(Golden golden, float[] data)
    {
        Assert.Equal(golden.Length, data.Length);
        for (var k = 0; k < golden.Samples.Length; k++)
        {
            var i = k * golden.Stride;
            Assert.True(Math.Abs(golden.Samples[k] - data[i]) <= 2e-6, $"отсчёт {i}: {data[i]} вместо {golden.Samples[k]}");
        }
        double sum = 0;
        foreach (var value in data)
            sum += Math.Abs(value);
        Assert.Equal(golden.Sum, sum, 1e-6 * golden.Sum);
    }

    [Fact]
    public void RifleMatchesTs() => Match(SynthGolden.Rifle0, Rifle(Rate, 0));

    [Fact]
    public void CannonMatchesTs() => Match(SynthGolden.Cannon1, Cannon(Rate, 1));

    [Fact]
    public void NoiseLoopsMatchTs()
    {
        Match(SynthGolden.NoisePink, Synth.NoiseLoop(Rate, 1, 8, NoiseColor.Pink));
        Match(SynthGolden.NoiseBrown, Synth.NoiseLoop(Rate, 1, 8, NoiseColor.Brown));
    }

    [Fact]
    public void ReverbImpulseMatchesTs()
    {
        var channels = Synth.ReverbImpulse(Rate, 0.5, 1);
        Match(SynthGolden.ReverbLeft, channels[0]);
        Match(SynthGolden.ReverbRight, channels[1]);
    }

    [Fact]
    public void RandomMatchesXorshift()
    {
        // (1 · 2654435761) mod 2³² = 2654435761; один шаг xorshift32.
        var state = 2654435761u;
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        Assert.Equal(state / 4294967296.0, Synth.Random(1)());
        Assert.Equal(Synth.Random(0)(), Synth.Random(0)());
    }
}
