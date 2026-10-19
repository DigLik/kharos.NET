using System.Diagnostics;
using System.Globalization;
using System.Text;
using Kharos.Audio;
using Kharos.Client.Shell;
using Kharos.Gpu;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Kharos.Desktop.Tests")]

namespace Kharos.Desktop;

/// <summary>
/// Что и как записывать (пакет З, apps/native/src/record.rs): KHAROS_RECORD=ролик.mp4 (или папка без расширения —
/// тогда последовательность PNG), KHAROS_SIZE=1080x1920, KHAROS_FPS=60, KHAROS_SECONDS=20 или KHAROS_FRAMES=N,
/// KHAROS_SKIP=2 (секунды разгона до записи), KHAROS_RATIO=2 (плотность пикселей игры — масштаб интерфейса),
/// KHAROS_FFMPEG=путь|off (иначе ffmpeg из PATH). Сценка и час — KHAROS_SHOT_SCENE и KHAROS_SHOT_HOUR, как у снимка.
/// KHAROS_AUDIO=off — без звука.
/// </summary>
public sealed record RecordOptions(string Path, int Width, int Height, int Fps, int Frames, int SkipFrames, float Ratio, string? Ffmpeg, bool Audio = true)
{
    public const string DefaultSize = "1080x1920";

    /// <summary>Звук ролика: сид движка, чтобы два прогона совпадали до отсчёта.</summary>
    public const uint AudioSeed = 1;

    /// <summary>Расширения, которые ffmpeg собирает в видео; остальное (и пустое) — последовательность PNG.</summary>
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".mov", ".webm", ".avi", ".m4v"];

    private bool HasVideoExtension => VideoExtensions.Contains(System.IO.Path.GetExtension(Path).ToLowerInvariant());

    /// <summary>Видео пишется только в файл с видеорасширением и только если есть ffmpeg.</summary>
    public bool IsVideo => Ffmpeg is not null && HasVideoExtension;

    /// <summary>Папка кадров PNG (последовательность): путь без расширения видео.</summary>
    public string SequenceDirectory => HasVideoExtension ? System.IO.Path.ChangeExtension(Path, null) : Path;

    /// <summary>WAV рядом с роликом: у видео — тот же путь с .wav, у последовательности — «папка.wav».</summary>
    public string WavPath => IsVideo ? System.IO.Path.ChangeExtension(Path, ".wav") : SequenceDirectory.TrimEnd('/', '\\') + ".wav";

    /// <summary>Путь кадра номер index (с единицы) последовательности.</summary>
    public string FramePath(int index) => System.IO.Path.Combine(SequenceDirectory, $"frame_{index:D6}.png");

    /// <summary>Длина ролика, секунды.</summary>
    public double Seconds => Frames / (double)Fps;

    /// <summary>Настройки из окружения; без KHAROS_RECORD записи нет.</summary>
    public static RecordOptions? FromEnvironment(Func<string, string?>? env = null)
    {
        env ??= name => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
        if (env("KHAROS_RECORD") is not { } path)
            return null;
        var size = env("KHAROS_SIZE") ?? DefaultSize;
        var parts = size.Split('x', 'X');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
            throw new ArgumentException($"KHAROS_SIZE={size}: ждём ширину и высоту, например {DefaultSize}");
        var fps = Math.Max(1, (int)Number(env, "KHAROS_FPS", 60));
        var frames = env("KHAROS_FRAMES") is not null ? (int)Number(env, "KHAROS_FRAMES", 0) : (int)Math.Round(Number(env, "KHAROS_SECONDS", 20) * fps);
        var skip = (int)Math.Ceiling(Number(env, "KHAROS_SKIP", 2) * fps);
        var ratio = (float)Number(env, "KHAROS_RATIO", 2);
        return new RecordOptions(System.IO.Path.GetFullPath(path), width, height, fps, frames, skip, ratio, FindFfmpeg(env("KHAROS_FFMPEG")),
            !string.Equals(env("KHAROS_AUDIO"), "off", StringComparison.OrdinalIgnoreCase)).Checked();
    }

    /// <summary>Проверка размеров: кодировщик берёт чётные стороны (цвет yuv420p вдвое грубее), кадров должно быть хоть сколько-то.</summary>
    public RecordOptions Checked()
    {
        if (Width <= 0 || Height <= 0 || Width % 2 == 1 || Height % 2 == 1)
            throw new ArgumentException($"размер {Width}x{Height}: стороны должны быть чётными и не нулевыми");
        if (Frames <= 0)
            throw new ArgumentException("в ролике нет кадров: задайте KHAROS_SECONDS или KHAROS_FRAMES");
        if (Ratio <= 0)
            throw new ArgumentException("KHAROS_RATIO должен быть больше нуля");
        return this;
    }

    private static double Number(Func<string, string?> env, string name, double fallback) =>
        env(name) is not { } text ? fallback
            : double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value
            : throw new ArgumentException($"{name}={text}: ждём число");

    /// <summary>KHAROS_FFMPEG: «off» — без ffmpeg, путь — он; иначе первый ffmpeg из PATH или null.</summary>
    public static string? FindFfmpeg(string? setting)
    {
        if (string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase))
            return null;
        if (setting is not null)
            return File.Exists(setting) ? System.IO.Path.GetFullPath(setting) : null;
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = System.IO.Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }
}

/// <summary>
/// Запись ролика: время ведёт оболочка. Кадр номер N приходит игре как N / fps секунд, хост одиночной партии идёт
/// ручными тиками (LocalGame.Advance) до тика, нужного этому кадру, а звук рендерится тем же микшером без устройства
/// ровно на длину кадра — поэтому ролик кадр-в-кадр и в звуке, и в картинке, и не зависит от скорости машины.
/// Кадры — в ffmpeg по трубе (rawvideo → H.264, настройки record.rs) или, без него, PNG по номерам; звук — WAV рядом,
/// у видео он подмешивается. Партия на сервере идёт по часам, поэтому для неё оболочка выдерживает темп сама.
/// </summary>
internal sealed class RecordSession : IDisposable
{
    private readonly RecordOptions _options;
    private readonly AudioEngine? _audio;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly StringBuilder _errors = new();
    private readonly string? _videoPath;
    private WavWriter? _wav;
    private Process? _ffmpeg;
    private Stream? _pipe;
    private long _audioFrames;
    private int _written;
    private bool _finished;

    public RecordSession(RecordOptions options, AudioEngine? audio)
    {
        _options = options;
        _audio = options.Audio ? audio : null;
        if (options.IsVideo)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.Path) ?? ".");
            // Со звуком видео сначала без него, потом подмешивается: ffmpeg читает WAV, когда тот дописан.
            _videoPath = _audio is null ? options.Path : Path.ChangeExtension(options.Path, null) + ".video" + Path.GetExtension(options.Path);
            StartFfmpeg();
        }
        else
        {
            Directory.CreateDirectory(options.SequenceDirectory);
            // Прежние кадры не мешают: счёт идёт заново.
            foreach (var old in Directory.EnumerateFiles(options.SequenceDirectory, "frame_*.png"))
                File.Delete(old);
        }
        if (_audio is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.WavPath) ?? ".");
            _wav = new WavWriter(new FileStream(options.WavPath, FileMode.Create, FileAccess.ReadWrite));
        }
        DesktopLog.Write($"запись: {options.Path} — {options.Width}x{options.Height}, {options.Fps} кадров/с, {options.Frames} кадров после {options.SkipFrames} кадров разгона, "
            + (options.IsVideo ? "ffmpeg" : "последовательность PNG") + (_audio is null ? ", без звука" : ", звук WAV"));
    }

    /// <summary>Движок звука для записи: без устройства, с постоянным сидом. Рендерится запись.</summary>
    public static AudioEngine CreateAudio() => new(new AudioEngineOptions { Seed = RecordOptions.AudioSeed });

    /// <summary>Записано кадров.</summary>
    public int Written => _written;

    /// <summary>Сколько отсчётов звука записано (кадров по 1/48000 с).</summary>
    public long AudioFrames => _audioFrames;

    /// <summary>Дошла ли запись до последнего кадра.</summary>
    public bool Done => _written >= _options.Frames;

    /// <summary>Перед шагом оболочки на кадре frame: хост одиночной партии доходит до его тика.</summary>
    public void Before(ShellApp app, int frame)
    {
        if (app.Session is LocalSession local)
            local.Advance((long)Math.Floor((frame + 1) * 1000.0 / _options.Fps / local.StepMs));
    }

    /// <summary>После отрисовки кадра frame: звук на его длину и, если разгон кончился, сам кадр. true — ролик набран.</summary>
    public bool After(Renderer renderer, int frame)
    {
        // Звук идёт со всеми кадрами, и с разгонными тоже: команды игры не копятся, время звука равно времени кадров.
        var from = (long)Math.Round(frame * (double)AudioFormat.SampleRate / _options.Fps);
        var to = (long)Math.Round((frame + 1) * (double)AudioFormat.SampleRate / _options.Fps);
        var recording = frame >= _options.SkipFrames;
        if (_audio is not null)
        {
            var samples = new float[(to - from) * 2];
            _audio.Render(samples);
            if (recording)
            {
                _wav!.Write(samples);
                _audioFrames += to - from;
            }
        }
        if (!recording)
            return false;
        var pixels = renderer.ReadPixels();
        if (_pipe is not null)
            Pipe(pixels);
        else
            Png.Write(_options.FramePath(_written + 1), pixels, renderer.Width, renderer.Height);
        _written++;
        if (_written % _options.Fps == 0 || Done)
            DesktopLog.Write($"запись: {_written / (double)_options.Fps:0.#} из {_options.Seconds:0.#} с ({_written / _clock.Elapsed.TotalSeconds:0} кадров/с)");
        return Done;
    }

    /// <summary>Закрывает трубу, ждёт кодировщик, подмешивает звук.</summary>
    public void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        _wav?.Dispose();
        _wav = null;
        if (_ffmpeg is not null)
        {
            _pipe!.Dispose();
            _pipe = null;
            Wait(_ffmpeg, "кодирование");
            _ffmpeg.Dispose();
            _ffmpeg = null;
            if (_audio is not null)
                Mux();
        }
        DesktopLog.Write($"запись готова: {_options.Path} — {_written} кадров за {_clock.Elapsed.TotalSeconds:0.0} с" + (_audio is not null ? $", звук {_options.WavPath}" : ""));
    }

    public void Dispose()
    {
        _wav?.Dispose();
        _pipe?.Dispose();
        if (_ffmpeg is { HasExited: false } running)
        {
            try
            {
                running.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        }
        _ffmpeg?.Dispose();
    }

    private void StartFfmpeg()
    {
        var size = $"{_options.Width}x{_options.Height}";
        // Кадры идут сверху вниз (так их отдаёт Renderer.ReadPixels): переворот не нужен. Остальное — как у record.rs.
        var info = new ProcessStartInfo(_options.Ffmpeg!)
        {
            RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-y", "-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", size,
            "-r", _options.Fps.ToString(CultureInfo.InvariantCulture), "-i", "-",
            "-c:v", "libx264", "-preset", "medium", "-crf", "16", "-pix_fmt", "yuv420p", "-movflags", "+faststart", _videoPath!,
        })
            info.ArgumentList.Add(arg);
        _ffmpeg = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg не запустился");
        _ffmpeg.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (_errors)
                    _errors.AppendLine(e.Data);
        };
        _ffmpeg.BeginErrorReadLine();
        _pipe = _ffmpeg.StandardInput.BaseStream;
    }

    private void Pipe(byte[] pixels)
    {
        try
        {
            _pipe!.Write(pixels);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException($"ffmpeg перестал принимать кадры: {error.Message}{Errors()}", error);
        }
    }

    /// <summary>Видео без звука + WAV → ролик со звуком AAC; временный файл убирается.</summary>
    private void Mux()
    {
        var info = new ProcessStartInfo(_options.Ffmpeg!) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[]
        {
            "-y", "-hide_banner", "-loglevel", "error", "-i", _videoPath!, "-i", _options.WavPath,
            "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", _options.Path,
        })
            info.ArgumentList.Add(arg);
        using var mux = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg не запустился");
        mux.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (_errors)
                    _errors.AppendLine(e.Data);
        };
        mux.BeginErrorReadLine();
        Wait(mux, "подмешивание звука");
        File.Delete(_videoPath!);
    }

    private void Wait(Process process, string what)
    {
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg ({what}) закончил с кодом {process.ExitCode}{Errors()}");
    }

    private string Errors()
    {
        lock (_errors)
            return _errors.Length > 0 ? $": {_errors.ToString().Trim()}" : "";
    }
}
