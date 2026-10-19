using Kharos.Client.Shell;

namespace Kharos.Client.Tests.Shell;

/// <summary>Фон меню (ShowcaseBackground, Н6-4) без окна: партию showcase заводит оболочка через запуск, а при начале игры отпускает.</summary>
public sealed class BackgroundTests
{
    [Fact]
    public void BackgroundStartsBehindTheMenuAndStopsWhenAGameStarts()
    {
        using var shell = new ShellHarness();
        var background = new ShowcaseBackground(shell.Launcher, new Random(5));
        shell.App.Background = background;
        Assert.Empty(shell.Launcher.Sessions);

        // Меню идёт, фон поднимает партию showcase со сценкой и местностью в настройках.
        shell.Frame();
        var first = Assert.Single(shell.Launcher.Sessions);
        var launch = Assert.IsType<GameLaunch>(first.Launch);
        Assert.Equal("showcase", launch.Game);
        Assert.Contains("\"scene\":\"", launch.Settings);
        Assert.Contains("\"land\":{\"seed\":", launch.Settings);
        Assert.True(background.Running);
        Assert.False(background.Ready);
        Assert.True(shell.Has("menu:backdrop"));
        Assert.True(first.Polls > 0);

        // Партия началась: фон отпускает свою (хост закрывается), меню ему не нужно.
        shell.App.Start(new GameLaunch(null));
        Assert.True(first.Disposed);
        Assert.False(background.Running);
        shell.Frame();
        Assert.Equal(2, shell.Launcher.Sessions.Count);
        Assert.Equal(1UL, background.Slides);

        // Вышли в меню: фон начинается заново, сценка — другая или та же, но партия новая.
        shell.App.Leave(save: false);
        shell.Frame();
        Assert.Equal(3, shell.Launcher.Sessions.Count);
        Assert.Equal(2UL, background.Slides);

        // Окно закрыто: фон гасит партию.
        var last = shell.Launcher.Sessions[^1];
        shell.App.Dispose();
        Assert.True(last.Disposed);
        Assert.False(background.Running);
    }

    [Fact]
    public void BackgroundThatFailsDoesNotRestartAtOnce()
    {
        using var shell = new ShellHarness();
        var background = new ShowcaseBackground(shell.Launcher, new Random(1));
        shell.App.Background = background;
        shell.Frame();
        var session = Assert.Single(shell.Launcher.Sessions);
        session.State = SessionState.Failed;
        shell.Frame();
        Assert.True(session.Disposed);
        shell.Frame();
        Assert.Single(shell.Launcher.Sessions);
        Assert.False(background.Running);
        // Меню при этом на месте, на ровном фоне.
        Assert.True(shell.Has("menu:backdrop"));
    }
}
