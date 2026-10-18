using Kharos.Audio.Graph;
using Kharos.Audio.Synthesis;

namespace Kharos.Audio.Tests;

public sealed class EngineTests
{
    private static SampleBuffer Ramp(int length)
    {
        var data = new float[length];
        for (var i = 0; i < length; i++)
            data[i] = i / (float)length;
        return new SampleBuffer(data);
    }

    [Fact]
    public void BufferSourceLoopsExactly()
    {
        var engine = Offline.Linear();
        engine.Bank.Add("ramp", Bus.World, Ramp(100));
        var s = new VoiceGraphBuilder();
        s.Sample("ramp", loop: true, offset: 0f, variant: 0).To(s.Out);
        engine.Voice(s.Build());
        var x = Offline.Left(engine, 1000);
        for (var i = 0; i < 900; i++)
            Assert.Equal(x[i], x[i + 100]);
        Assert.Equal(0.37f * Offline.Center, x[537], 6);
    }

    [Fact]
    public void BufferSourceRateAndOffset()
    {
        var engine = Offline.Linear();
        engine.Bank.Add("ramp", Bus.World, Ramp(1000));
        var s = new VoiceGraphBuilder();
        s.Sample("ramp", offset: 100f / AudioFormat.SampleRate, rate: 0.5f).To(s.Out);
        var sound = engine.Voice(s.Build());
        var x = Offline.Left(engine, 4096);
        // Полскорости: кадр 1 — посередине между отсчётами 100 и 101.
        Assert.Equal(0.1005f * Offline.Center, x[1], 6);
        Assert.Equal(0.6f * Offline.Center, x[1000], 6);
        // Запись кончилась на кадре 1800 — дальше тишина, голос снят.
        Assert.Equal(0f, x[1850]);
        Assert.True(sound.IsFinished);
    }

    [Fact]
    public void OneShotsAreLimited()
    {
        var engine = new AudioEngine(new AudioEngineOptions { Seed = 3 });
        engine.Bank.Add("shot", Bus.Battle, Ramp(4800), Ramp(4800));
        for (var i = 0; i < 40; i++)
            engine.Play("shot", 0.5f, 0f, 1f);
        engine.Render(new float[256]);
        Assert.Equal(32, engine.OneShots);
        for (var i = 0; i < 10; i++)
            engine.Play("shot", 0.5f);
        engine.Render(new float[256]);
        Assert.Equal(32, engine.OneShots);
        engine.Render(new float[2 * AudioFormat.SampleRate]);
        Assert.Equal(0, engine.OneShots);
        Assert.Equal(0, engine.ActiveVoices);
        // Тише 0,01 — не звучит вовсе.
        engine.Play("shot", 0.005f);
        engine.Render(new float[256]);
        Assert.Equal(0, engine.OneShots);
    }

    [Fact]
    public void PlaceMovesVoiceSmoothly()
    {
        var engine = Offline.Linear();
        var s = new VoiceGraphBuilder();
        s.Const(1f).To(s.Out);
        var sound = engine.Voice(s.Build());
        sound.Place(0.5f, 1f);
        var (left, right) = engine.Render(AudioFormat.SampleRate / 2);
        Assert.True(right[100] < 0.75f && right[100] > 0.5f, "плавно, а не скачком");
        Assert.Equal(0f, left[^1], 4);
        Assert.Equal(0.5f, right[^1], 4);
    }

    [Fact]
    public void ReleaseFadesAndRemoves()
    {
        var engine = Offline.Linear();
        var s = new VoiceGraphBuilder();
        s.Osc(Wave.Sine, 300f).To(s.Out);
        var sound = engine.Voice(s.Build());
        // 39 квантов: команда применится ровно с кадра 4992.
        engine.Render(4992);
        sound.Release(0.1f);
        var x = Offline.Left(engine, AudioFormat.SampleRate / 2);
        Assert.True(Offline.Peak(x.AsSpan(0, 480)) > 0.5);
        Assert.True(Offline.Peak(x.AsSpan(9000, 1000)) < 0.01);
        Assert.Equal(0.0, Offline.Peak(x.AsSpan(12000)));
        Assert.True(sound.IsFinished);
        Assert.Equal(0, engine.ActiveVoices);
    }

    [Fact]
    public void LengthStopsVoice()
    {
        var engine = Offline.Linear();
        var s = new VoiceGraphBuilder();
        s.Osc(Wave.Square).To(s.Out);
        var sound = engine.Voice(s.Build(), new VoiceOptions { At = 0.01, Length = 0.02f, Note = 220f });
        var x = Offline.Left(engine, 6000);
        Assert.Equal(0.0, Offline.Peak(x.AsSpan(0, 480)));
        Assert.True(Offline.Peak(x.AsSpan(480, 960)) > 0.5);
        Assert.Equal(0.0, Offline.Peak(x.AsSpan(1440 + 2400 + 128)));
        Assert.True(sound.IsFinished);
    }

    [Fact]
    public void VoiceFeedsAnotherVoice()
    {
        // Как нота подложки в фильтр фона: into и соединение с параметром чужого голоса.
        var engine = Offline.Linear();
        var bed = new VoiceGraphBuilder();
        var gain = bed.Gain(0.5f);
        gain.To(bed.Out);
        var bedSound = engine.Voice(bed.Build());
        var note = new VoiceGraphBuilder();
        note.Const(1f).To(note.Out);
        var noteSound = engine.Voice(note.Build(), new VoiceOptions { Into = bedSound.Node(1), Length = 0.01f });
        var x = Offline.Left(engine, 4800);
        // Нота: моно через свою панораму (стерео 0,707), потом gain 0,5 и панорама фона (стерео — без изменений).
        Assert.Equal(0.5f * Offline.Center, x[100], 5);
        Assert.Equal(0f, x[4000], 6);
        Assert.True(noteSound.IsFinished);
        Assert.False(bedSound.IsFinished);
    }

    [Fact]
    public void LoopsGlideAndKeepOneVoice()
    {
        var engine = Offline.Linear();
        engine.Bank.Add("hum", Bus.World, new SampleBuffer(Synth.NoiseLoop(AudioFormat.SampleRate, 1, 3, NoiseColor.Pink)));
        engine.Loop("hum", 1f, 0f);
        engine.Render(new float[2 * 4800]);
        engine.Loop("hum", 0.5f, -1f);
        engine.Render(new float[2 * AudioFormat.SampleRate * 2]);
        Assert.Equal(1, engine.ActiveVoices);
        var (left, right) = engine.Render(4800);
        Assert.True(Offline.Peak(left) > 0.05);
        Assert.True(Offline.Peak(right) < 1e-3);
    }

    [Fact]
    public void SameCommandsGiveSameSamples()
    {
        static float[] Run()
        {
            var engine = new AudioEngine(new AudioEngineOptions { Seed = 77 });
            engine.Bank.Add("reverb", Bus.World, new SampleBuffer(Synth.ReverbImpulse(AudioFormat.SampleRate, 0.5, 1)));
            engine.Bank.Add("shot", Bus.Battle,
                new SampleBuffer(Synth.Render(AudioFormat.SampleRate, 0.1, 0, (t, noise) => noise() * Synth.Decay(t, 0.02))),
                new SampleBuffer(Synth.Render(AudioFormat.SampleRate, 0.1, 1, (t, noise) => noise() * Synth.Decay(t, 0.02))));
            var s = new VoiceGraphBuilder();
            var reverb = s.Reverb("reverb");
            var filter = s.Filter(FilterType.Bandpass, 700f, 3f);
            s.Lfo(0.7f, 300f).To(filter[ParamId.Frequency]);
            s.Noise(NoiseColor.Brown).To(filter).To(reverb).To(s.Out);
            s.Osc(Wave.Sawtooth, 110f).To(s.Gain(0.2f)).To(s.Out);
            engine.RegisterVoice("bed", s.Build());
            var bed = engine.Voice("bed")!;
            var output = new float[2 * AudioFormat.SampleRate];
            for (var frame = 0; frame < 20; frame++)
            {
                engine.Play("shot", (float)engine.Random(), (float)(engine.Random() * 2 - 1), 1f + (float)engine.Random() * 0.1f);
                bed.Place(0.5f + frame * 0.01f, frame % 3 - 1);
                engine.Render(output.AsSpan(frame * output.Length / 20, output.Length / 20));
            }
            return output;
        }

        var first = Run();
        var second = Run();
        Assert.Equal(first, second);
        Assert.True(Offline.Peak(first) > 0.05);
    }

    [Fact]
    public void BinaryGraphPlays()
    {
        var engine = Offline.Linear();
        var s = new VoiceGraphBuilder();
        s.Osc(Wave.Sine, 1000f).To(s.Out);
        engine.Voice(s.Build().Encode());
        Assert.Equal(Offline.Center, Offline.Amplitude(Offline.Left(engine, 4800), 1000), 4);
    }

    [Fact]
    public void BusVolumeScales()
    {
        var engine = Offline.Linear();
        var s = new VoiceGraphBuilder();
        s.Const(1f).To(s.Out);
        engine.Voice(s.Build(), new VoiceOptions { Bus = Bus.Music });
        engine.SetVolume(Bus.Music, 0.25f);
        engine.SetMasterVolume(0.5f);
        var x = Offline.Left(engine, AudioFormat.SampleRate);
        Assert.Equal(0.125f * Offline.Center, x[^1], 4);
    }
}

public sealed class HearingTests
{
    [Fact]
    public void NearnessAndSideFollowHearingTs()
    {
        // Экран 800×600 при масштабе 32: полуширина 12,5 тайла.
        var listener = new Listener(100, 100, 32, 800, 600);
        Assert.Equal(1f, Hearing.Nearness(listener, 105, 98));
        Assert.Equal(0.5f, Hearing.Nearness(listener, 100 + 12.5 + 3.75, 100), 5);
        Assert.Equal(0f, Hearing.Nearness(listener, 200, 100));
        Assert.Equal(0.5f, Hearing.Nearness(listener with { Zoom = 16 }, 100, 100), 5);
        Assert.Equal(1f, Hearing.Side(listener, 112.5), 5);
        Assert.Equal(-0.5f, Hearing.Side(listener, 93.75), 5);
    }
}
