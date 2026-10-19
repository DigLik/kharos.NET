using System.Globalization;
using System.Text;
using Kharos.Audio.Synthesis;

namespace Kharos.Audio.Tests;

/// <summary>Д5: офлайн-рендер, WAV, шины с переходом музыки, звуки оболочки, громкости игрока.</summary>
public sealed class EngineFeaturesTests
{
    private const int Block = 4096;

    private static SampleBuffer Sine(double hz, int periods = 100)
    {
        var length = (int)Math.Round(AudioFormat.SampleRate / hz * periods);
        var data = new float[length];
        for (var i = 0; i < length; i++)
            data[i] = (float)Math.Sin(2 * Math.PI * hz * i / AudioFormat.SampleRate);
        return new SampleBuffer(data);
    }

    private static AudioEngine WithTones()
    {
        var engine = Offline.Linear(5);
        engine.Bank.Add("battle", Bus.Battle, Sine(480));
        engine.Bank.Add("world", Bus.World, Sine(960));
        engine.Bank.Add("ui", Bus.Interface, Sine(1200));
        engine.Bank.Add("music", Bus.Music, Sine(1600));
        return engine;
    }

    // Сценарий для сверки путей: команды — на границах блоков по 4096 кадров (кратно кванту).
    private static void Scenario(AudioEngine engine, Action<int> render)
    {
        engine.Bank.Add("tone", Bus.World, Sine(480));
        engine.Bank.Add("shot", Bus.Battle, new SampleBuffer(Synth.NoiseLoop(AudioFormat.SampleRate, 0.2, 9, Graph.NoiseColor.Pink)));
        engine.Loop("tone", 0.5f, -0.3f);
        render(Block);
        engine.Play("shot", 0.8f, 0.4f, 1.2f);
        engine.SetVolume(Bus.World, 0.3f);
        render(Block);
        engine.Play("shot", 0.6f, -0.7f);
        engine.Loop("tone", 0.1f, 0.5f);
        render(Block);
        ShellSounds.PlayClick(engine);
        render(Block);
    }

    [Fact]
    public void OfflineRenderEqualsChunkedRealtimePath()
    {
        // Одним куском (офлайн) и кусками как у вывода (разные размеры, в том числе меньше кванта): отсчёты те же.
        var whole = Offline.Linear(11);
        var wholeAudio = new List<float>();
        Scenario(whole, frames => wholeAudio.AddRange(OfflineRender.Frames(whole, frames).Interleaved));

        var chunked = Offline.Linear(11);
        var chunkedAudio = new List<float>();
        int[] sizes = [512, 77, 1000, 3, 384, 129, 1];
        var next = 0;
        Scenario(chunked, frames =>
        {
            var left = frames;
            while (left > 0)
            {
                var n = Math.Min(left, sizes[next++ % sizes.Length]);
                chunkedAudio.AddRange(OfflineRender.Frames(chunked, n).Interleaved);
                left -= n;
            }
        });

        Assert.Equal(Block * 4 * 2, wholeAudio.Count);
        Assert.Equal(wholeAudio, chunkedAudio);
        Assert.True(wholeAudio.Max(Math.Abs) > 0.01f);
    }

    [Fact]
    public void ScenarioEventsLandOnQuantumBoundaries()
    {
        const double at = 0.2;
        var scripted = Offline.Linear(3);
        scripted.Bank.Add("tone", Bus.World, Sine(480));
        var result = OfflineRender.Run(scripted, 0.5, new OfflineEvent(at, e => e.Play("tone", 1f)));

        var manual = Offline.Linear(3);
        manual.Bank.Add("tone", Bus.World, Sine(480));
        var first = OfflineRender.Frames(manual, 9600);
        manual.Play("tone", 1f);
        var second = OfflineRender.Frames(manual, 24000 - 9600);

        Assert.Equal(first.Interleaved.Concat(second.Interleaved), result.Interleaved);
        Assert.Equal(0f, result.Peak(0, 9600));
        Assert.True(result.Peak(9600, 4800) > 0.5f);
        Assert.Equal(0.5, result.Seconds, 6);
    }

    [Fact]
    public void RenderRefusedWhileOutputAttachedIsNotNeededWithoutDevice()
    {
        var audio = Offline.Render(e => e.Bank.Add("t", Bus.World, Sine(480)), 0.1);
        Assert.Equal(4800, audio.Frames);
        Assert.Equal(0f, audio.Peak());
    }

    [Theory]
    [InlineData(WavFormat.Pcm16)]
    [InlineData(WavFormat.Float32)]
    public void WavRoundTrip(WavFormat format)
    {
        var audio = Offline.Render(e =>
        {
            e.Bank.Add("tone", Bus.World, Sine(480));
            e.Play("tone", 0.9f, 0.5f);
        }, 0.25);
        var bytes = WavFile.ToBytes(audio, format);
        var bits = format == WavFormat.Pcm16 ? 2 : 4;
        Assert.Equal(44 + audio.Interleaved.Length * bits, bytes.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(bytes.Length - 8, BitConverter.ToInt32(bytes, 4));
        Assert.Equal(audio.Interleaved.Length * bits, BitConverter.ToInt32(bytes, 40));

        var back = WavFile.Read(bytes);
        Assert.Equal(2, back.Channels);
        Assert.Equal(AudioFormat.SampleRate, back.SampleRate);
        Assert.Equal(format, back.Format);
        Assert.Equal(audio.Frames, back.Frames);
        for (var i = 0; i < audio.Interleaved.Length; i++)
        {
            if (format == WavFormat.Float32)
                Assert.Equal(audio.Interleaved[i], back.Interleaved[i]);
            else
                Assert.InRange(Math.Abs(audio.Interleaved[i] - back.Interleaved[i]), 0, 2.0 / 32768);
        }
        Assert.True(audio.Peak() > 0.3f);
    }

    [Fact]
    public void WavWriterStreamsInPieces()
    {
        var audio = Offline.Render(e =>
        {
            e.Bank.Add("tone", Bus.World, Sine(480));
            e.Play("tone", 1f);
        }, 0.1);
        using var memory = new MemoryStream();
        using (var writer = new WavWriter(memory, WavFormat.Float32, leaveOpen: true))
        {
            writer.Write(audio.Interleaved.AsSpan(0, 1000));
            writer.Write(audio.Interleaved.AsSpan(1000));
        }
        Assert.Equal(WavFile.ToBytes(audio, WavFormat.Float32), memory.ToArray());
        Assert.Throws<FormatException>(() => WavFile.Read(new byte[20]));
    }

    [Fact]
    public void WavClampsAndSanitizes()
    {
        using var memory = new MemoryStream();
        WavFile.Write(memory, [2f, -2f, float.NaN, 0.5f], WavFormat.Pcm16);
        var back = WavFile.Read(memory.ToArray());
        Assert.Equal(32767 / 32768f, back.Interleaved[0]);
        Assert.Equal(-1f, back.Interleaved[1]);
        Assert.Equal(0f, back.Interleaved[2]);
    }

    [Fact]
    public void EveryBusHasItsOwnVolume()
    {
        Assert.Equal(["battle", "world", "interface", "music"], Enum.GetValues<Bus>().Select(Buses.Name));
        var engine = WithTones();
        // Выход мягко ограничен, поэтому четыре петли — тихо (0,2), чтобы сумма не упиралась в потолок.
        foreach (var name in new[] { "battle", "world", "ui", "music" })
            engine.Loop(name, 0.2f, 0f);
        const double one = Offline.Center * 0.2;
        var full = OfflineRender.Seconds(engine, 2.0);
        var tail = full.Channel(0).AsSpan(full.Frames - 4800);
        double[] hz = [480, 960, 1200, 1600];
        foreach (var f in hz)
            Assert.Equal(one, Offline.Amplitude(tail, f), 3);

        engine.SetVolume(Bus.Battle, 0.5f);
        engine.SetVolume(Bus.World, 0f);
        engine.SetVolume(Bus.Interface, 0.25f);
        Assert.Equal(0.5f, engine.Volume(Bus.Battle));
        Assert.Equal(1f, engine.Volume(Bus.Music));
        var next = OfflineRender.Seconds(engine, 1.0);
        tail = next.Channel(0).AsSpan(next.Frames - 4800);
        Assert.Equal(one * 0.5, Offline.Amplitude(tail, 480), 3);
        Assert.Equal(0, Offline.Amplitude(tail, 960), 3);
        Assert.Equal(one * 0.25, Offline.Amplitude(tail, 1200), 3);
        Assert.Equal(one, Offline.Amplitude(tail, 1600), 3);
    }

    [Fact]
    public void MusicCrossfadesBetweenTwoVoices()
    {
        var engine = Offline.Linear(2);
        engine.Bank.Add("a", Bus.Music, Sine(480));
        engine.Bank.Add("b", Bus.Music, Sine(960));
        var first = engine.PlayMusic("a", 0.1f);
        Assert.NotNull(first);
        var intro = OfflineRender.Seconds(engine, 1.0);
        var tail = intro.Channel(0).AsSpan(intro.Frames - 4800);
        Assert.Equal(Offline.Center, Offline.Amplitude(tail, 480), 2);
        Assert.Equal(0, Offline.Amplitude(tail, 960), 3);
        Assert.Same(first, engine.CurrentMusic);

        var second = engine.PlayMusic("b", 1f)!;
        Assert.NotSame(first, second);
        var fade = OfflineRender.Seconds(engine, 1.2).Channel(0);
        // Середина перехода: звучат оба голоса.
        var middle = fade.AsSpan(24000 - 2400, 4800);
        var a = Offline.Amplitude(middle, 480) / Offline.Center;
        var b = Offline.Amplitude(middle, 960) / Offline.Center;
        Assert.InRange(b, 0.35, 0.65);
        Assert.InRange(a, 0.1, 0.5);
        // Новый нарастает, прежний убывает.
        Assert.True(Offline.Amplitude(fade.AsSpan(2400, 2400), 960) < Offline.Amplitude(fade.AsSpan(40000, 2400), 960));
        Assert.True(Offline.Amplitude(fade.AsSpan(2400, 2400), 480) > Offline.Amplitude(fade.AsSpan(40000, 2400), 480));

        var end = OfflineRender.Seconds(engine, 2.0).Channel(0);
        Assert.Equal(Offline.Center, Offline.Amplitude(end.AsSpan(end.Length - 4800), 960), 2);
        Assert.Equal(0, Offline.Amplitude(end.AsSpan(end.Length - 4800), 480), 3);
        Assert.True(first!.IsFinished);
        Assert.Same(second, engine.CurrentMusic);

        // Шина music: своя громкость.
        engine.SetVolume(Bus.Music, 0.5f);
        var half = OfflineRender.Seconds(engine, 1.0).Channel(0);
        Assert.Equal(Offline.Center * 0.5, Offline.Amplitude(half.AsSpan(half.Length - 4800), 960), 2);

        engine.StopMusic(0.2f);
        Assert.Null(engine.CurrentMusic);
        var silence = OfflineRender.Seconds(engine, 1.0).Channel(0);
        Assert.Equal(0, Offline.Peak(silence.AsSpan(silence.Length - 4800)), 4);
    }

    [Fact]
    public void MusicWithoutRecordIsIgnored()
    {
        var engine = Offline.Linear();
        Assert.Null(engine.PlayMusic("missing"));
        Assert.Null(engine.CurrentMusic);
    }

    [Theory]
    [InlineData(ShellSounds.Click)]
    [InlineData(ShellSounds.Hover)]
    [InlineData(ShellSounds.Open)]
    [InlineData(ShellSounds.Deny)]
    public void ShellSoundsAreFiniteAudibleAndPeakAtOne(string name)
    {
        for (var variant = 0; variant < ShellSounds.Variants; variant++)
        {
            var data = ShellSounds.Make(name, variant)!;
            Assert.NotEmpty(data);
            Assert.All(data, v => Assert.True(float.IsFinite(v)));
            Assert.Equal(1f, data.Max(Math.Abs), 4);
            Assert.True(data.Sum(v => (double)v * v) / data.Length > 0.01);
        }
        Assert.Null(ShellSounds.Make("nope", 0));
    }

    [Fact]
    public void ShellSoundsPlayOnTheInterfaceBus()
    {
        var engine = Offline.Linear(4);
        ShellSounds.PlayClick(engine);
        var click = OfflineRender.Seconds(engine, 0.2);
        Assert.True(click.Peak() > 0.1f);
        Assert.True(click.Peak() < 1f);
        Assert.True(engine.Bank.Contains(ShellSounds.Hover));

        // Шина interface выключена — меню молчит.
        var muted = Offline.Linear(4);
        muted.SetVolume(Bus.Interface, 0f);
        OfflineRender.Seconds(muted, 1.0);
        ShellSounds.PlayHover(muted);
        ShellSounds.PlayClick(muted, disabled: true);
        ShellSounds.PlaySlide(muted, 0.5f);
        Assert.True(OfflineRender.Seconds(muted, 0.2).Peak() < 1e-4f);
    }

    private static AudioPrefs Prefs(AudioEngine? engine, Dictionary<string, byte[]> storage) =>
        new(engine, key => storage.GetValueOrDefault(key), (key, value) => storage[key] = value);

    [Fact]
    public void PrefsAreStoredUnderEngineKeysAndApplied()
    {
        var storage = new Dictionary<string, byte[]>();
        var engine = WithTones();
        var prefs = Prefs(engine, storage);
        Assert.Equal(1f, prefs.Volume);
        prefs.Volume = 0.5f;
        prefs.SetLevel(Bus.World, 0.25f);
        Assert.Equal("0.5", Encoding.UTF8.GetString(storage[AudioPrefs.VolumeKey]));
        Assert.Equal("0.25", Encoding.UTF8.GetString(storage["engine/audio/level/world"]));
        Assert.Equal(0.25f, engine.Volume(Bus.World));

        engine.Loop("world", 1f, 0f);
        var audio = OfflineRender.Seconds(engine, 2.0).Channel(0);
        Assert.Equal(Offline.Center * 0.25 * 0.5, Offline.Amplitude(audio.AsSpan(audio.Length - 4800), 960), 3);

        // Новый клиент над тем же хранилищем читает те же значения.
        var again = WithTones();
        var restored = Prefs(again, storage);
        Assert.Equal(0.5f, restored.Volume);
        Assert.Equal(0.25f, restored.Level(Bus.World));
        Assert.Equal(1f, restored.Level(Bus.Music));
        Assert.Equal(0.25f, again.Volume(Bus.World));

        restored.Muted = true;
        Assert.Equal("1", Encoding.UTF8.GetString(storage[AudioPrefs.MutedKey]));
        again.Loop("world", 1f, 0f);
        var quiet = OfflineRender.Seconds(again, 1.0).Channel(0);
        Assert.True(Offline.Peak(quiet.AsSpan(quiet.Length - 4800)) < 1e-4);
        restored.Muted = false;
        restored.Dim = true;
        var dim = OfflineRender.Seconds(again, 1.0).Channel(0);
        Assert.Equal(Offline.Center * 0.25 * 0.5 * AudioPrefs.Dimmed, Offline.Amplitude(dim.AsSpan(dim.Length - 4800), 960), 3);
    }

    [Fact]
    public void PrefsSurviveGarbageAndWorkWithoutAudio()
    {
        var storage = new Dictionary<string, byte[]>();
        storage[AudioPrefs.VolumeKey] = (Encoding.UTF8.GetBytes("не число"));
        storage[AudioPrefs.LevelKey(Bus.Music)] = (Encoding.UTF8.GetBytes(7.5f.ToString(CultureInfo.InvariantCulture)));
        var prefs = Prefs(null, storage);
        Assert.Equal(1f, prefs.Volume);
        Assert.Equal(1f, prefs.Level(Bus.Music));
        prefs.SetLevel(Bus.Battle, 0.4f);
        Assert.Equal(0.4f, Prefs(null, storage).Level(Bus.Battle));
    }
}
