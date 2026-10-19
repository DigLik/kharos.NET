using System.Text;
using System.Text.Json;
using Kharos.Client.Game;
using Kharos.Client.Shell;
using Kharos.Client.Tests.Game;
using Kharos.Content;
using Kharos.Host;
using Kharos.Mod.Abi;
using HostGame = Kharos.Host.Host;

namespace Kharos.Client.Tests.Shell;

/// <summary>Партия hello в своём потоке хоста — как у десктопа, только без окна (LocalSession в Kharos.Desktop).</summary>
internal sealed class HelloLocal : IGameSession
{
    private readonly LocalGame _local;

    public HelloLocal(Launch launch, Kharos.Client.Ui.Fonts fonts, string? settings = null)
    {
        Launch = launch;
        var content = GameContent.Load([ModPackage.Read(HelloSession.ModDir)]);
        var options = new HostOptions
        {
            Seed = 1, Modules = new Kharos.Plugins.Game.GameModuleOptions { CallTimeoutMs = 1000 },
            Settings = settings is null ? null : JsonDocument.Parse(settings).RootElement.Clone(),
        };
        _local = LocalGame.Start(() => new HostGame(content, options));
        Game = new ClientGame(content, _local.Client, new ClientGameOptions
        {
            Fonts = fonts, ModMode = HelloSession.ClientMode, ModuleOptions = new Kharos.Plugins.Game.GameModuleOptions { CallTimeoutMs = 1000 },
        });
    }

    public Launch Launch { get; }
    public SessionState State => SessionState.Ready;
    public string? Status => null;
    public string? Error => null;
    public bool WrongPassword => false;
    public ClientGame? Game { get; private set; }
    public bool Left { get; private set; }

    public void Poll() { }
    public void Retry(string password) { }
    public void Leave() => Left = true;

    public void Dispose()
    {
        Game?.Dispose();
        Game = null;
        _local.Dispose();
    }
}

/// <summary>Окна оболочки поверх настоящей партии: меню паузы, чат с командами хоста, права и настройки для модов.</summary>
public sealed class PlayTests
{
    [Fact]
    public void PauseMenuChatAndEditorRightOverALocalGame()
    {
        Assert.SkipUnless(HelloSession.Built, HelloSession.NotBuilt);
        using var shell = new ShellHarness();
        HelloLocal? session = null;
        shell.Launcher.Real = launch => session = new HelloLocal(launch, shell.Fonts, """{"weather":{"startHour":6,"dayLength":0}}""");
        shell.App.Start(new GameLaunch(null));
        shell.Frame();
        var game = session!.Game!;
        Assert.Same(game, shell.App.Session!.Game);

        // Меню паузы — по клавише меню; модальное: клавиши модам не идут, флаг оболочки — во входе кадра.
        shell.Press("f10");
        Assert.True(shell.App.Paused);
        shell.Frame();
        Assert.True(shell.Has("pause"));
        Assert.True(game.CapturesKeys);
        Assert.Equal(ClientAbi.ShellMenu, shell.App.ShellFlags & ClientAbi.ShellMenu);
        shell.Press("escape");
        Assert.False(shell.App.Paused);

        // Просьба мода (запись Shell) открывает чат с его текстом.
        game.ShellRequests.Add((ClientAbi.ShellActionChat, "/he"));
        shell.Frame();
        Assert.True(shell.App.Chat.IsOpen);
        Assert.Equal("/he", shell.App.Chat.Draft);
        shell.Press("escape");
        Assert.False(shell.App.Chat.IsOpen);

        // Чат по Enter: /admin спрашивает пароль скрыто, /editor даёт право редактора — оно в настройках для модов.
        Say(shell, "/admin");
        Assert.True(shell.App.Chat.Password);
        shell.Frame();
        shell.Type("local");
        shell.Press("enter");
        Until(shell, () => game.Admin, "режим администратора");
        // Хост пропускает строки чата чаще, чем раз в полсекунды (ChatGap).
        Thread.Sleep(600);
        Say(shell, "/editor");
        Until(shell, () => game.Editor, "право редактора");
        Assert.Equal(ClientAbi.ShellEditor, shell.App.ShellFlags & ClientAbi.ShellEditor);
        var output = new byte[4096];
        var length = game.Tools.Call(ClientAbi.ToolSettings, [], output);
        var settings = Encoding.UTF8.GetString(output, 0, length);
        using (var json = JsonDocument.Parse(settings))
        {
            var root = json.RootElement;
            Assert.True(root.GetProperty("rights").GetProperty("editor").GetBoolean());
            Assert.True(root.GetProperty("rights").GetProperty("admin").GetBoolean());
            Assert.Equal(6, root.GetProperty("host").GetProperty("weather").GetProperty("startHour").GetInt32());
        }
        // Ответы хоста — ключами, на языке игрока.
        Assert.Contains(game.Chat, line => line.Text == "editorOn");
        Assert.DoesNotContain(game.Chat.Select(game.TextOf), text => text == "editorOn");

        // Выход из меню паузы — уход из партии и главное меню.
        shell.Press("f10");
        shell.Click("pause:exit");
        Assert.True(session.Left);
        Assert.Null(shell.App.Session);
        Assert.Equal(MenuScreen.Main, shell.App.Menu.Screen);
    }

    private static void Say(ShellHarness shell, string line)
    {
        shell.Press("enter");
        Assert.True(shell.App.Chat.IsOpen);
        shell.Frame();
        shell.Type(line);
        Assert.Equal(line, shell.App.Chat.Draft);
        shell.Press("enter");
        Assert.True(!shell.App.Chat.IsOpen || shell.App.Chat.Password);
    }

    private static void Until(ShellHarness shell, Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, $"не дождались: {what}");
            Thread.Sleep(15);
            shell.Frame();
        }
    }
}
