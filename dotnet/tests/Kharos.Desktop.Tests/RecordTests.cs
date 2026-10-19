using Kharos.Audio;
using Kharos.Client.Shell;
using Kharos.Desktop;
using Kharos.Gpu;

namespace Kharos.Desktop.Tests;

/// <summary>Запись роликов (пакет З): скрытое окно, последовательность PNG (без ffmpeg) и WAV той же длины.</summary>
public sealed class RecordTests : IDisposable
{
    private const int Frames = 6, Fps = 30, Skip = 3;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kharos-record-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private (string Dir, string Wav, bool Flag) Record(string name, bool audio = true)
    {
        var path = Path.Combine(_root, name);
        var record = new RecordOptions(path, 160, 96, Fps, Frames, Skip, 1f, null, audio).Checked();
        var options = new DesktopOptions(DesktopOptions.FindModRoots(), "hello", audio, 1f,
            new ShotOptions(path, Skip + Frames, null, (160, 96), null, null, true), Record: record);
        var flag = false;
        Shell.Probe = app => flag |= (app.ShellFlags & 4) != 0; // ClientAbi.ShellRecording
        try
        {
            Assert.Equal(0, Shell.Run(options));
        }
        finally
        {
            Shell.Probe = null;
        }
        return (record.SequenceDirectory, record.WavPath, flag);
    }

    private static void RequireGpu()
    {
        var gpu = GpuContext.TryCreateHeadless(out var reason);
        if (gpu is null)
            Assert.Skip($"нет адаптера WebGPU: {reason}");
        gpu.Dispose();
    }

    [Fact]
    public void RecordsPngSequenceAndWavDeterministically()
    {
        RequireGpu();
        var a = Record("a");
        var b = Record("b");
        Assert.True(a.Flag, "флаг ShellRecording не стоял");
        var framesA = Directory.GetFiles(a.Dir, "frame_*.png").Order().ToArray();
        var framesB = Directory.GetFiles(b.Dir, "frame_*.png").Order().ToArray();
        Assert.Equal(Frames, framesA.Length);
        Assert.Equal(Frames, framesB.Length);
        Assert.Equal("frame_000001.png", Path.GetFileName(framesA[0]));
        for (var i = 0; i < Frames; i++)
            Assert.True(File.ReadAllBytes(framesA[i]).AsSpan().SequenceEqual(File.ReadAllBytes(framesB[i])), $"кадр {i + 1} различается между прогонами");

        var wav = WavFile.Read(File.ReadAllBytes(a.Wav));
        Assert.Equal(Frames * AudioFormat.SampleRate / Fps, wav.Frames);
        Assert.Equal(AudioFormat.SampleRate, wav.SampleRate);
        Assert.True(File.ReadAllBytes(a.Wav).AsSpan().SequenceEqual(File.ReadAllBytes(b.Wav)), "звук различается между прогонами");
    }

    [Fact]
    public void NoWavWithoutAudio()
    {
        RequireGpu();
        var a = Record("mute", audio: false);
        Assert.Equal(Frames, Directory.GetFiles(a.Dir, "frame_*.png").Length);
        Assert.False(File.Exists(a.Wav));
    }

    [Fact]
    public void ParsesEnvironment()
    {
        var env = new Dictionary<string, string> { ["KHAROS_RECORD"] = "x/clip.mp4", ["KHAROS_SIZE"] = "640x360", ["KHAROS_FPS"] = "30", ["KHAROS_SECONDS"] = "2", ["KHAROS_FFMPEG"] = "off" };
        var o = RecordOptions.FromEnvironment(n => env.GetValueOrDefault(n))!;
        Assert.Equal((640, 360, 30, 60, 60), (o.Width, o.Height, o.Fps, o.Frames, o.SkipFrames));
        Assert.False(o.IsVideo);
        Assert.EndsWith("clip", o.SequenceDirectory);
        Assert.EndsWith("clip.wav", o.WavPath);
        env["KHAROS_FRAMES"] = "7";
        Assert.Equal(7, RecordOptions.FromEnvironment(n => env.GetValueOrDefault(n))!.Frames);
        env["KHAROS_SIZE"] = "641x360";
        Assert.Throws<ArgumentException>(() => RecordOptions.FromEnvironment(n => env.GetValueOrDefault(n)));
        Assert.Null(RecordOptions.FromEnvironment(_ => null));
    }
}
