using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Kharos.Smoke;

/// <summary>Свободный порт: его выбирает ОС (привязка к порту 0), сокет сразу закрывается.</summary>
internal static class Ports
{
    public static int Free()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>Как запустить процесс: программа, аргументы, папка, окружение (null — убрать переменную), журнал и stdin.</summary>
internal sealed record Launch(string File, IReadOnlyList<string> Args, string WorkDir, IReadOnlyDictionary<string, string?> Env, string LogPath, bool Stdin = false)
{
    /// <summary>Переменные KHAROS_* родителя не наследуются: процесс видит только свои.</summary>
    public bool ClearKharosEnv { get; init; } = true;

    /// <summary>Дополнительная остановка процесса (WSL: kill по PID внутри Linux) — перед Kill самого процесса.</summary>
    public Action? OnKill { get; init; }
}

/// <summary>Запущенный процесс: вывод (stdout и stderr) — в журнал и в память строками, stdin — по <see cref="Send"/>.</summary>
internal sealed class Proc
{
    private readonly Process _process;
    private readonly StreamWriter _log;
    private readonly List<string> _lines = [];
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _outputDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _streams = 2;
    private bool _logClosed;

    public Proc(string label, Launch launch, Process process, StreamWriter log)
    {
        (Label, Launch, _process, _log) = (label, launch, process, log);
        Pid = process.Id;
        Started = DateTime.Now;
    }

    public string Label { get; }
    public Launch Launch { get; }
    public int Pid { get; }
    public DateTime Started { get; }
    public bool Exited => _process.HasExited;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;
    public Process Process => _process;

    /// <summary>Строки вывода, пришедшие с начала (снимок).</summary>
    public string[] Lines()
    {
        lock (_gate)
            return [.. _lines];
    }

    public int LineCount
    {
        get
        {
            lock (_gate)
                return _lines.Count;
        }
    }

    /// <summary>Событие на каждую строку вывода.</summary>
    public event Action<string>? Line;

    internal void Attach()
    {
        _process.OutputDataReceived += (_, e) => Take(e.Data);
        _process.ErrorDataReceived += (_, e) => Take(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private void Take(string? line)
    {
        if (line is null)
        {
            if (Interlocked.Decrement(ref _streams) == 0)
            {
                lock (_gate)
                    if (!_logClosed)
                        _log.Flush();
                _outputDone.TrySetResult();
            }
            return;
        }
        lock (_gate)
        {
            _lines.Add(line);
            // Чтение вывода идёт в пуле потоков и после Dispose группы (убитый процесс дописывает трубу): закрытый журнал
            // не трогаем — исключение в обработчике вывода роняет весь процесс.
            if (!_logClosed)
            {
                _log.WriteLine(line);
                _log.Flush();
            }
        }
        Line?.Invoke(line);
    }

    /// <summary>Строка в stdin процесса.</summary>
    public void Send(string line)
    {
        _process.StandardInput.WriteLine(line);
        _process.StandardInput.Flush();
    }

    /// <summary>Ждёт выхода не дольше timeout; true — вышел.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        try
        {
            await _process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return false;
        }
        // Вывод дочитывается после выхода: не дольше двух секунд (внуки могут держать трубу).
        await Task.WhenAny(_outputDone.Task, Task.Delay(2000, CancellationToken.None)).ConfigureAwait(false);
        return true;
    }

    /// <summary>Ждёт строку вывода по условию, начиная со строки from; null — срок вышел или процесс завершился.</summary>
    public async Task<string?> ExpectAsync(Func<string, bool> match, int from, TimeSpan timeout, CancellationToken cancel)
    {
        var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Watch(string line)
        {
            if (match(line))
                found.TrySetResult(line);
        }
        Line += Watch;
        try
        {
            foreach (var line in Lines().Skip(from))
                if (match(line))
                    return line;
            var exit = _process.WaitForExitAsync(cancel);
            var done = await Task.WhenAny(found.Task, exit, Task.Delay(timeout, cancel)).ConfigureAwait(false);
            if (done == found.Task)
                return found.Task.Result;
            cancel.ThrowIfCancellationRequested();
            // Вышел: строка могла прийти последней.
            await Task.WhenAny(_outputDone.Task, Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false);
            return Lines().Skip(from).FirstOrDefault(match);
        }
        finally
        {
            Line -= Watch;
        }
    }

    /// <summary>Убивает процесс с потомками; true — был жив.</summary>
    public bool Kill()
    {
        try
        {
            if (_process.HasExited)
                return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        try
        {
            Launch.OnKill?.Invoke();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
        return true;
    }

    internal void CloseLog()
    {
        lock (_gate)
        {
            _logClosed = true;
            _log.Dispose();
        }
    }
}

/// <summary>
/// Все процессы прогона. На Windows каждый ещё и в задании ОС с KILL_ON_JOB_CLOSE: если сам оркестратор упадёт или его
/// убьют, ОС закроет задание и убьёт всех. <see cref="Dispose"/> (и Ctrl+C, и выход процесса) убивает всё живое.
/// </summary>
internal sealed partial class ProcessGroup : IDisposable
{
    private readonly List<Proc> _all = [];
    private readonly Lock _gate = new();
    private readonly nint _job;
    private bool _disposed;

    public ProcessGroup()
    {
        if (OperatingSystem.IsWindows())
            _job = Job.Create();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public IReadOnlyList<Proc> All
    {
        get
        {
            lock (_gate)
                return [.. _all];
        }
    }

    public Proc Start(string label, Launch launch)
    {
        var info = new ProcessStartInfo(launch.File)
        {
            WorkingDirectory = launch.WorkDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = launch.Stdin,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in launch.Args)
            info.ArgumentList.Add(arg);
        if (launch.ClearKharosEnv)
            foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("KHAROS_", StringComparison.OrdinalIgnoreCase)).ToList())
                info.Environment.Remove(key);
        foreach (var (key, value) in launch.Env)
        {
            if (value is null)
                info.Environment.Remove(key);
            else
                info.Environment[key] = value;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(launch.LogPath)!);
        var log = new StreamWriter(launch.LogPath, append: true, new UTF8Encoding(false));
        Process process;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            process = Process.Start(info) ?? throw new InvalidOperationException($"{launch.File} не запустился");
            if (_job != 0)
                Job.Assign(_job, process);
        }
        var proc = new Proc(label, launch, process, log);
        lock (_gate)
            _all.Add(proc);
        proc.Attach();
        return proc;
    }

    /// <summary>Убивает все живые процессы; имена убитых.</summary>
    public List<string> KillAll()
    {
        var killed = new List<string>();
        foreach (var proc in All)
            if (proc.Kill())
                killed.Add($"{proc.Label} (PID {proc.Pid})");
        return killed;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        KillAll();
        foreach (var proc in All)
            proc.CloseLog();
        if (_job != 0)
            Job.Close(_job);
    }

    /// <summary>Задание Windows: потомки умирают вместе с ним.</summary>
    private static partial class Job
    {
        private const int ExtendedLimitInformation = 9;
        private const uint KillOnJobClose = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimit
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimit
        {
            public BasicLimit Basic;
            public IoCounters Io;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial nint CreateJobObject(nint attributes, string? name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimit info, int length);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AssignProcessToJobObject(nint job, nint process);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint handle);

        public static nint Create()
        {
            var job = CreateJobObject(0, null);
            if (job == 0)
                return 0;
            var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = KillOnJobClose } };
            if (!SetInformationJobObject(job, ExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimit>()))
            {
                CloseHandle(job);
                return 0;
            }
            return job;
        }

        public static void Assign(nint job, Process process)
        {
            try
            {
                AssignProcessToJobObject(job, process.Handle);
            }
            catch (InvalidOperationException)
            {
            }
        }

        public static void Close(nint job) => CloseHandle(job);
    }
}
