using System.Runtime.InteropServices;

namespace Kharos.Audio;

/// <summary>
/// Короткий сон аудиопотока. На Windows Thread.Sleep(1) спит до 15,6 мс (шаг системного таймера), этого мало для
/// очереди в 40 мс: там — таймер высокого разрешения (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, системный шаг не меняется).
/// </summary>
internal sealed partial class Napper : IDisposable
{
    private const uint HighResolution = 0x2;
    private const uint AllAccess = 0x1F0003;
    private nint _timer;

    public Napper()
    {
        if (OperatingSystem.IsWindows())
            _timer = CreateWaitableTimerExW(0, 0, HighResolution, AllAccess);
    }

    /// <summary>Уснуть примерно на milliseconds.</summary>
    public void Sleep(double milliseconds)
    {
        if (_timer != 0)
        {
            // Отрицательное — относительное время в сотнях наносекунд.
            var due = -(long)(milliseconds * 10_000);
            if (SetWaitableTimer(_timer, in due, 0, 0, 0, false))
            {
                WaitForSingleObject(_timer, 1000);
                return;
            }
        }
        Thread.Sleep(Math.Max(1, (int)milliseconds));
    }

    public void Dispose()
    {
        if (_timer != 0)
        {
            CloseHandle(_timer);
            _timer = 0;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(nint timer, in long dueTime, int period, nint completion, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
