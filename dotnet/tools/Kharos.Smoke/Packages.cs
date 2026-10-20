using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Kharos.Smoke;

/// <summary>
/// Где взять kharos и kharos-server: явные пути (--kharos, --server: программа или папка), папка упаковки (--package:
/// архивы package.ps1/package.sh или распакованные папки) или сборка из исходников (--build: package.ps1 на Windows,
/// package.sh на Linux, затем распаковка).
/// </summary>
internal static class Packages
{
    public static string Rid => (OperatingSystem.IsWindows() ? "win-" : OperatingSystem.IsMacOS() ? "osx-" : "linux-")
        + (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64");

    public static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>Программа name в пути: сам файл или поиск в папке (до трёх уровней вглубь); нет — null.</summary>
    public static string? FindExe(string path, string name)
    {
        var file = Exe(name);
        if (File.Exists(path))
            return Path.GetFullPath(path);
        if (!Directory.Exists(path))
            return null;
        var found = new List<string>();
        void Walk(string dir, int depth)
        {
            var here = Path.Combine(dir, file);
            if (File.Exists(here))
            {
                found.Add(here);
                return;
            }
            if (depth == 0)
                return;
            foreach (var sub in Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal))
                Walk(sub, depth - 1);
        }
        Walk(Path.GetFullPath(path), 3);
        // kharos-server тоже лежит в папке «kharos-server-…», но ищется своё имя: kharos ≠ kharos-server.
        return found.FirstOrDefault();
    }

    /// <summary>
    /// Распаковывает архивы упаковки из dir в into (zip — сразу, tar.gz — системным tar: сохраняет бит исполнения);
    /// уже распакованное не трогает. Возвращает папку, где искать программы.
    /// </summary>
    public static string Unpack(string dir, string into, Action<string> log)
    {
        var archives = Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            .Where(f => Path.GetFileName(f).StartsWith("kharos", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
        if (archives.Count == 0)
            return dir;
        Directory.CreateDirectory(into);
        foreach (var archive in archives)
        {
            var name = Path.GetFileName(archive);
            var target = Path.Combine(into, name.Replace(".tar.gz", "", StringComparison.OrdinalIgnoreCase).Replace(".zip", "", StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(target);
            log($"распаковка {name}");
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ZipFile.ExtractToDirectory(archive, target);
            else
                Run("tar", ["-xzf", archive, "-C", target], into, log);
        }
        return into;
    }

    /// <summary>Сборка из исходников: игра и сервер для своей платформы в out/pkg (архивы), распакованные — в out/unpacked.</summary>
    public static string Build(string dotnetRoot, string version, string outDir, Action<string> log)
    {
        var pkg = Path.Combine(outDir, "pkg");
        Directory.CreateDirectory(pkg);
        foreach (var kind in new[] { "desktop", "server" })
        {
            log($"сборка {kind} {Rid} {version}");
            if (OperatingSystem.IsWindows())
                Run("pwsh", ["-NoProfile", "-File", Path.Combine(dotnetRoot, "tools", "package.ps1"), "-Rid", Rid, "-Kind", kind, "-Version", version, "-Out", pkg], dotnetRoot, log);
            else
                Run("sh", [Path.Combine(dotnetRoot, "tools", "package.sh"), Rid, kind, version, pkg], dotnetRoot, log);
        }
        return Unpack(pkg, Path.Combine(outDir, "unpacked"), log);
    }

    /// <summary>Папка dotnet репозитория: вверх от программы до Kharos.slnx.</summary>
    public static string? DotnetRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Kharos.slnx")))
                    return dir.FullName;
                if (File.Exists(Path.Combine(dir.FullName, "dotnet", "Kharos.slnx")))
                    return Path.Combine(dir.FullName, "dotnet");
            }
        return null;
    }

    private static void Run(string file, string[] args, string dir, Action<string> log)
    {
        var info = new ProcessStartInfo(file) { WorkingDirectory = dir, UseShellExecute = false };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{file} не запустился");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} {string.Join(' ', args)}: код {process.ExitCode}");
    }

    /// <summary>Путь Windows → путь внутри WSL (/mnt/c/…).</summary>
    public static string ToWsl(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);
        if (full.Length < 2 || full[1] != ':')
            return full.Replace('\\', '/');
        return $"/mnt/{char.ToLowerInvariant(full[0])}{full[2..].Replace('\\', '/')}";
    }
}
