using System.Globalization;

namespace Kharos.Desktop;

/// <summary>
/// Снимок кадра вместо игры: KHAROS_SHOT, KHAROS_SHOT_FRAMES, KHAROS_SHOT_POINTER, KHAROS_SHOT_SIZE; Н6.4: KHAROS_SHOT_SCENE —
/// сценка витрины (или описание сцены сверки кадров), KHAROS_SHOT_HOUR — час суток (0..24) поверх часов карты,
/// KHAROS_CLOCK=fixed — замороженные экранные часы (время кадра = номер кадра, без реального времени).
/// </summary>
public sealed record ShotOptions(string Path, int Frames, (float X, float Y)? Pointer, (int Width, int Height) Size,
    string? Scene = null, float? Hour = null, bool FixedClock = false);

/// <summary>
/// Настройки запуска из окружения (как у пилота): KHAROS_MODS — папки с модами через разделитель путей, KHAROS_GAME —
/// партия, KHAROS_AUDIO=off — без звука, KHAROS_SCALE — масштаб интерфейса вместо масштаба экрана. Сеть (Н4):
/// KHAROS_SERVER=ws://хост:порт — играть на сервере вместо одиночной партии, KHAROS_NAME — ник, KHAROS_PASSWORD — пароль сервера.
/// </summary>
public sealed record DesktopOptions(IReadOnlyList<string> ModRoots, string? Game, bool Audio, float? Scale, ShotOptions? Shot,
    string? Server = null, string? Name = null, string? Password = null, RecordOptions? Record = null)
{
    public static DesktopOptions FromEnvironment()
    {
        var record = RecordOptions.FromEnvironment();
        var shot = record is not null
            // Запись идёт тем же скрытым окном, что и снимок: сценка и час те же, часы замороженные (шаг кадра ведёт запись).
            ? new ShotOptions(record.Path, record.SkipFrames + record.Frames, null, (record.Width, record.Height), Env("KHAROS_SHOT_SCENE"), Hour(Env("KHAROS_SHOT_HOUR")), true)
            : Env("KHAROS_SHOT") is { } path
            ? new ShotOptions(Path.GetFullPath(path), Int(Env("KHAROS_SHOT_FRAMES")) ?? 60, Pointer(Env("KHAROS_SHOT_POINTER")), Size(Env("KHAROS_SHOT_SIZE")),
                Env("KHAROS_SHOT_SCENE"), Hour(Env("KHAROS_SHOT_HOUR")), string.Equals(Env("KHAROS_CLOCK"), "fixed", StringComparison.OrdinalIgnoreCase))
            : null;
        return new DesktopOptions(FindModRoots(), Env("KHAROS_GAME"), !string.Equals(Env("KHAROS_AUDIO"), "off", StringComparison.OrdinalIgnoreCase),
            Float(Env("KHAROS_SCALE")) ?? record?.Ratio, shot, Env("KHAROS_SERVER"), Env("KHAROS_NAME"), Env("KHAROS_PASSWORD"), record);
    }

    /// <summary>
    /// KHAROS_MODS; иначе папка mods рядом с программой, а из репозитория — dotnet/mods выше по дереву. Значения «wasm» и
    /// «clr» — это режим модов у тестов (Sim.DefaultMode), а не папки: с ними поиск идёт как без переменной.
    /// </summary>
    internal static List<string> FindModRoots()
    {
        if (Env("KHAROS_MODS") is { } list && !list.Equals("wasm", StringComparison.OrdinalIgnoreCase) && !list.Equals("clr", StringComparison.OrdinalIgnoreCase))
            return [.. list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Path.GetFullPath)];
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                foreach (var candidate in new[] { Path.Combine(dir.FullName, "mods"), Path.Combine(dir.FullName, "dotnet", "mods") })
                    if (Directory.Exists(candidate) && Directory.GetDirectories(candidate).Any(mod => File.Exists(Path.Combine(mod, "mod.json"))))
                        return [candidate];
            }
        }
        throw new InvalidOperationException("нет папки mods: положите её рядом с программой или задайте KHAROS_MODS");
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static int? Int(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;

    private static float? Float(string? text) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;

    private static float? Hour(string? text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 24 ? value : null;

    private static (float, float)? Pointer(string? text)
    {
        if (text is null)
            return null;
        var parts = text.Split(',');
        return parts.Length == 2
            && float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ? (x, y) : null;
    }

    private static (int, int) Size(string? text)
    {
        var parts = text?.Split('x', 'X', ',');
        return parts is { Length: 2 } && Int(parts[0].Trim()) is { } width && Int(parts[1].Trim()) is { } height ? (width, height) : (1280, 800);
    }
}

/// <summary>Журнал: в stderr и в файл kharos.log в профиле пользователя (у WinExe консоли нет).</summary>
public static class DesktopLog
{
    private static readonly Lock Gate = new();

    public static string FilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kharos", "kharos.log");

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        Console.Error.WriteLine(line);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
