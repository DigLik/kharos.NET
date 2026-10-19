using Kharos.Client.Shell;
using Kharos.Client.Tests.Game;
using Kharos.Mod.Abi;

namespace Kharos.Client.Tests.Shell;

/// <summary>Режим редактора у оболочки (Shell/Editor*): вход из меню, право от хоста, полоса режима, выход.</summary>
public sealed class EditorShellTests
{
    [Fact]
    public void SlotOpensInTheEditorWithTheEditorRight()
    {
        Assert.SkipUnless(HelloSession.Built, HelloSession.NotBuilt);
        using var shell = new ShellHarness();
        HelloLocal? session = null;
        shell.Launcher.Real = launch => session = new HelloLocal(launch, shell.Fonts);
        var slot = shell.App.Slots.Create("карта", 32, 7);

        // «Редактор» у слота в окне сохранений.
        shell.App.Menu.Open(MenuScreen.Saves);
        shell.Frame();
        shell.Click($"save:{slot.Id}:editor");
        var launch = Assert.IsType<EditorLaunch>(session!.Launch);
        Assert.Equal(slot.Id, launch.Slot!.Id);
        var game = session.Game!;

        // Право оболочка просит сама: /admin, через полсекунды /editor; флаг редактора — во входе кадра модов.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!game.Editor)
        {
            Assert.True(DateTime.UtcNow < deadline, "нет права редактора");
            Thread.Sleep(20);
            shell.Frame();
        }
        Assert.Equal(ClientAbi.ShellEditor, shell.App.ShellFlags & ClientAbi.ShellEditor);
        shell.Frame();
        Assert.True(shell.Has("editor:bar"));
        Assert.True(shell.Has("editor:save"));

        // Выход: с несохранённым — второе нажатие подтверждает; мир слота уход не сохраняет.
        shell.Click("editor:exit");
        if (shell.App.Session is not null)
            shell.Click("editor:exit");
        Assert.Null(shell.App.Session);
        Assert.True(shell.App.Slots.Find(slot.Id)!.Empty);
    }
}
