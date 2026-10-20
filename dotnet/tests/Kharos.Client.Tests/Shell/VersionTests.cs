using System.Reflection;
using System.Text.RegularExpressions;
using Kharos.Client.Shell;
using Kharos.I18n;
using Kharos.Protocol;

namespace Kharos.Client.Tests.Shell;

/// <summary>Версия сборки (Н7-4) и «скачайте новую версию» при несовпадении протокола (Н7-5).</summary>
public sealed partial class VersionTests
{
    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemVer();

    [Fact]
    public void BuildInfoComesFromTheProps()
    {
        Assert.Matches(SemVer(), BuildInfo.Version);
        var props = File.ReadAllText(Path.Combine(FindRoot(), "Directory.Build.props"));
        Assert.Contains($"<KharosVersion Condition=\"'$(KharosVersion)' == ''\">{BuildInfo.Version}</KharosVersion>", props);
        Assert.Equal("DigLik/kharos.NET", BuildInfo.Repo);
        Assert.Equal("https://github.com/DigLik/kharos.NET/releases/latest", BuildInfo.ReleasesUrl);
        // Информационная версия сборки — версия плюс хеш коммита.
        var informational = typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.StartsWith(BuildInfo.Version, informational);
        Assert.Equal(BuildInfo.Commit.Length > 0, informational.Contains('+'));
        Assert.Equal(BuildInfo.Full, BuildInfo.Commit.Length > 0 ? $"{BuildInfo.Version}+{BuildInfo.Commit}" : BuildInfo.Version);
    }

    [Fact]
    public void MenuLineNamesVersionAndProtocolInEveryLanguage()
    {
        var texts = Texts.Shell();
        Assert.Equal("Версия 0.2.0 · протокол 3", texts.T("ru", "menu.protocolVersion", ("version", "0.2.0"), ("protocol", 3)));
        foreach (var lang in new[] { "en", "de", "es", "fr", "pt", "uk", "uz" })
        {
            var line = texts.T(lang, "menu.protocolVersion", ("version", "0.2.0"), ("protocol", 3));
            Assert.Contains("0.2.0", line);
            Assert.Contains("3", line);
            Assert.DoesNotContain("{", line);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DownloadButtonAppearsOnlyOnVersionMismatch(bool mismatch)
    {
        using var shell = new ShellHarness();
        shell.Frame();
        shell.App.Menu.OpenUrl = url => Opened.Add(url);
        Opened.Clear();
        shell.Click("menu:network");
        shell.Click("menu:network:connect");
        var session = Assert.Single(shell.Launcher.Sessions);
        session.State = SessionState.Failed;
        session.Error = "хост не пустил: Версии не совпадают";
        session.VersionMismatch = mismatch;
        shell.Frame();
        Assert.Equal(mismatch, shell.Has("play:download"));
        if (!mismatch)
            return;
        shell.Click("play:download");
        Assert.Equal([BuildInfo.ReleasesUrl], Opened);
    }

    private static readonly List<string> Opened = [];

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
        throw new DirectoryNotFoundException("нет Directory.Build.props");
    }
}
