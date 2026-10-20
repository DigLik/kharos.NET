using System.Diagnostics;
using System.Text.RegularExpressions;
using Kharos.Protocol;

namespace Kharos.Server.Tests;

/// <summary>Версия сервера (Н7-4, Н7-5): строка и флаг <c>--version</c>.</summary>
public sealed partial class VersionTests
{
    [Fact]
    public void LineHasBuildProtocolAndSaveVersions()
    {
        var line = VersionLine.Text();
        Assert.Contains(BuildInfo.Version, line);
        Assert.Contains($"протокол {Welcome.ProtocolVersion}", line);
        Assert.Contains($"сохранение {SaveFile.Version}", line);
    }

    [Fact]
    public void VersionFlagPrintsVersionAndExits()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "kharos-server.dll");
        Assert.True(File.Exists(dll), dll);
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(dll);
        info.ArgumentList.Add("--version");
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "сервер не вышел по --version");
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(VersionLine.Text(), output.Trim());
        Assert.Matches(Shape(), output.Trim());
    }

    [GeneratedRegex(@"^kharos-server \d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9a-f]+)? \(протокол 3, сохранение 1\)$")]
    private static partial Regex Shape();
}
