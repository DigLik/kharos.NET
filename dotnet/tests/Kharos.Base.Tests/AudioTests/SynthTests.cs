using Kharos.Mod.Sdk.Client;
using Base.Client.Sounds;

namespace Kharos.Base.Tests.AudioTests;

/// <summary>Перенос packages/tests/audio.test.ts: синтез звуков банка base на 44,1 кГц, как в TS-тесте.</summary>
public sealed class SynthTests
{
    private const int Rate = 44100;

    /// <summary>Далёкие звуки фона (DISTANT_NAMES): разовые, но проверяются отдельным тестом.</summary>
    private static readonly string[] Distant = ["creak", "clank", "thunder"];

    /// <summary>Фоновые петли (ALL_LOOP_NAMES): работа зданий, шаги и петли мира.</summary>
    private static readonly string[] Loops =
    [
        "hum", "machinery", "drill", "steps",
        "overload", "construct", "weld", "tractor", "servo", "reactor", "dome", "hydraulic", "steam", "rocket", "charge", "flow",
    ];

    /// <summary>Разовые звуки (ALL_SOUND_NAMES): у них по SoundBank.Variants вариантов, и они не далёкие.</summary>
    private static IEnumerable<SoundBank.Entry> Sounds => SoundBank.All.Where(entry => entry.Variants == SoundBank.Variants && !Distant.Contains(entry.Name));

    private static (double Peak, double Energy) Measure(float[] data)
    {
        double peak = 0, energy = 0;
        foreach (var value in data)
        {
            peak = Math.Max(peak, Math.Abs(value));
            energy += (double)value * value;
        }
        return (peak, energy);
    }

    private static float[][] Variants(SoundBank.Entry entry) => [.. Enumerable.Range(0, SoundBank.Variants).Select(variant => entry.Render(Rate, variant)[0])];

    [Fact(DisplayName = "звуки синтезируются: конечные, не тишина, пик — единица, варианты различаются")]
    public void SoundsSynthesize()
    {
        // 24 боя и интерфейса, 3 мотора, 44 мира: полнота списка TS.
        Assert.Equal(71, Sounds.Count());
        foreach (var entry in Sounds)
        {
            var variants = Variants(entry);
            foreach (var data in variants)
            {
                // Щелчки интерфейса — сотые доли секунды; короче — уже не звук, а треск.
                Assert.True(data.Length > Rate * 0.02, $"{entry.Name}: короткий");
                Assert.True(data.Length < Rate * 7, $"{entry.Name}: длинный");
                var (peak, energy) = Measure(data);
                Assert.True(double.IsFinite(energy), $"{entry.Name}: не число");
                Assert.InRange(peak, 0.995, 1.005);
                Assert.True(Math.Sqrt(energy / data.Length) > 0.02, $"{entry.Name}: тихо");
                // Конец гаснет до нуля: без щелчка.
                Assert.True(Math.Abs(data[^1]) < 0.01, $"{entry.Name}: щелчок в конце");
            }
            Assert.False(variants[0].AsSpan().SequenceEqual(variants[1]), $"{entry.Name}: варианты не различаются");
        }
    }

    [Fact(DisplayName = "далёкие звуки фона: конечные, не тишина, пик — единица, варианты различаются")]
    public void DistantSoundsSynthesize()
    {
        foreach (var name in Distant)
        {
            var entry = SoundBank.All.Single(e => e.Name == name);
            var variants = Variants(entry);
            foreach (var data in variants)
            {
                Assert.True(data.Length > Rate, $"{name}: короткий");
                Assert.True(data.Length < Rate * 6, $"{name}: длинный");
                var (peak, energy) = Measure(data);
                Assert.True(double.IsFinite(energy), $"{name}: не число");
                Assert.InRange(peak, 0.995, 1.005);
                Assert.True(Math.Sqrt(energy / data.Length) > 0.01, $"{name}: тихо");
                Assert.True(Math.Abs(data[^1]) < 0.01, $"{name}: щелчок в конце");
            }
            Assert.False(variants[0].AsSpan().SequenceEqual(variants[1]), $"{name}: варианты не различаются");
        }
    }

    [Fact(DisplayName = "петли шума: заданная громкость, без постоянной составляющей и без шва на стыке")]
    public void NoiseLoopsAreLevelAndSeamless()
    {
        foreach (var color in new[] { NoiseColor.White, NoiseColor.Pink, NoiseColor.Brown })
        {
            var data = Dsp.NoiseLoop(Rate, 2, 1, color);
            Assert.Equal(Rate * 2, data.Length);
            double mean = 0, energy = 0, step = 0;
            for (var i = 0; i < data.Length; i++)
            {
                mean += data[i] / (double)data.Length;
                energy += (double)data[i] * data[i];
                if (i > 0)
                    step = Math.Max(step, Math.Abs(data[i] - data[i - 1]));
            }
            Assert.True(Math.Abs(mean) < 1e-3, $"{color}: постоянная составляющая {mean}");
            Assert.InRange(Math.Sqrt(energy / data.Length), Dsp.NoiseLevel - 0.0005, Dsp.NoiseLevel + 0.0005);
            // Стык конца с началом — не резче, чем самый резкий шаг внутри петли.
            Assert.True(Math.Abs(data[0] - data[^1]) <= step, $"{color}: шов");
            Assert.False(Dsp.NoiseLoop(Rate, 2, 2, color).AsSpan().SequenceEqual(data), $"{color}: другое зерно звучит так же");
        }
        // Бурый шум глухой: соседние сэмплы почти равны, в отличие от белого.
        static double Smoothness(float[] data)
        {
            double sum = 0;
            for (var i = 1; i < data.Length; i++)
                sum += Math.Abs(data[i] - data[i - 1]);
            return sum / data.Length;
        }
        Assert.True(Smoothness(Dsp.NoiseLoop(Rate, 1, 1, NoiseColor.Brown)) < Smoothness(Dsp.NoiseLoop(Rate, 1, 1, NoiseColor.White)) / 5);
    }

    [Fact(DisplayName = "отклик эха: стерео, разные уши, гаснет к концу")]
    public void ReverbImpulseIsStereoAndFades()
    {
        var channels = Dsp.ReverbImpulse(Rate, 3, 1);
        Assert.Equal(2, channels.Length);
        var (left, right) = (channels[0], channels[1]);
        Assert.Equal(Rate * 3, left.Length);
        Assert.Equal(Rate * 3, right.Length);
        Assert.False(left.AsSpan().SequenceEqual(right));
        static double Energy(float[] data, int from, int to)
        {
            double sum = 0;
            for (var i = from; i < to; i++)
                sum += (double)data[i] * data[i];
            return sum;
        }
        var tenth = left.Length / 10;
        Assert.True(Energy(left, left.Length - tenth, left.Length) < Energy(left, 0, tenth) / 1000);
    }

    [Fact(DisplayName = "фоновые петли: конечные, не тишина, пик — единица")]
    public void BackgroundLoopsSynthesize()
    {
        foreach (var name in Loops)
        {
            var entry = SoundBank.All.Single(e => e.Name == name);
            Assert.Equal(1, entry.Variants);
            var data = entry.Render(Rate, 0)[0];
            var (peak, energy) = Measure(data);
            Assert.True(double.IsFinite(energy), $"{name}: не число");
            Assert.InRange(peak, 0.995, 1.005);
            Assert.True(Math.Sqrt(energy / data.Length) > 0.02, $"{name}: тихо");
        }
    }
}
