// Точка входа десктопа: настройки из окружения и запуск оболочки (docs/dotnet.md, Р10, Н3).
using Kharos.Desktop;

try
{
    return Shell.Run(DesktopOptions.FromEnvironment());
}
catch (Exception error)
{
    DesktopLog.Write($"сбой: {error}");
    return 1;
}
