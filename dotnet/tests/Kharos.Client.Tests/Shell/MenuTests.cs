using System.Text.Json;
using Kharos.Client.Game;
using Kharos.Client.Shell;
using Kharos.Client.Ui;

namespace Kharos.Client.Tests.Shell;

/// <summary>Главное меню и окна оболочки без окна и видеокарты: переходы по щелчкам, запуски, окна подключения.</summary>
public sealed class MenuTests
{
    [Fact]
    public void MainMenuOpensScreensAndComesBack()
    {
        using var shell = new ShellHarness();
        shell.Frame();
        Assert.Equal(MenuScreen.Main, shell.App.Menu.Screen);
        // Продолжать нечего — кнопки нет.
        Assert.False(shell.Has("menu:resume"));
        foreach (var (button, screen, window) in new[]
        {
            ("menu:new", MenuScreen.New, "menu:new"), ("menu:saves", MenuScreen.Saves, "menu:saves"),
            ("menu:network", MenuScreen.Network, "menu:network"), ("menu:settings", MenuScreen.Settings, "settings"),
            ("menu:about", MenuScreen.About, "menu:about"),
        })
        {
            shell.Click(button);
            Assert.Equal(screen, shell.App.Menu.Screen);
            Assert.True(shell.Has(window));
            shell.Click($"{window}:back");
            Assert.Equal(MenuScreen.Main, shell.App.Menu.Screen);
        }
        Assert.Empty(shell.Launcher.Sessions);
    }

    [Fact]
    public void NewGameCreatesSlotWithSettingsAndStartsIt()
    {
        using var shell = new ShellHarness();
        shell.Frame();
        shell.Click("menu:new");
        shell.Click("menu:new:size:1");
        Assert.Equal(1024, shell.App.Menu.Game.Size);
        shell.Click("menu:new:preset:1");
        var seed = shell.App.Menu.Game.Generator.Seed;
        Assert.Equal(0.5, shell.App.Menu.Game.Generator.RockLevel);
        Assert.True(shell.Has("menu:preview"));
        shell.Click("menu:new:start");

        var session = Assert.Single(shell.Launcher.Sessions);
        var slot = Assert.IsType<SlotLaunch>(session.Launch).Slot;
        Assert.Equal(("Игра 1", 1024, (long)seed), (slot.Name, slot.Size, slot.Seed));
        using (var settings = JsonDocument.Parse(slot.Settings!))
        {
            var root = settings.RootElement;
            Assert.Equal(1024, root.GetProperty("size").GetInt32());
            Assert.Equal(seed, root.GetProperty("land").GetProperty("seed").GetInt32());
            Assert.Equal(0.5, root.GetProperty("land").GetProperty("rockLevel").GetDouble());
            Assert.Equal(1200, root.GetProperty("weather").GetProperty("dayLength").GetInt32());
        }
        Assert.Equal([slot.Id], shell.App.Slots.List().Select(other => other.Id));

        // Подключается — окно с отменой; отмена — в меню, и «Продолжить» ведёт в этот слот.
        Assert.True(shell.Has("play:notice"));
        shell.Click("play:abort");
        Assert.Null(shell.App.Session);
        Assert.True(session.Disposed);
        Assert.False(session.Left);
        Assert.True(shell.Has("menu:resume"));
        shell.Click("menu:resume");
        Assert.Equal(slot.Id, Assert.IsType<SlotLaunch>(shell.Launcher.Sessions[^1].Launch).Slot.Id);
    }

    [Fact]
    public void SavesRenameAndRemoveWithConfirmation()
    {
        using var shell = new ShellHarness();
        var slot = shell.App.Slots.Create("Старая", 512, 1);
        shell.Frame();
        shell.Click("menu:saves");
        Assert.Single(shell.App.Menu.Saves);
        shell.Click($"save:{slot.Id}:rename");
        shell.Frame(hits: ["ctrl", "a"]);
        shell.Type("Новая");
        shell.Press("enter");
        Assert.Equal("Новая", shell.App.Slots.List().Single().Name);

        shell.Click($"save:{slot.Id}:remove");
        Assert.Single(shell.App.Slots.List());
        shell.Click($"save:{slot.Id}:sure");
        Assert.Empty(shell.App.Slots.List());
        Assert.Empty(shell.App.Menu.Saves);

        // Файл не сохранение — ошибка в окне, слотов не прибавилось.
        shell.App.Menu.ReadFile = _ => "не сохранение"u8.ToArray();
        shell.App.Menu.Import("C:\\x.kharos");
        Assert.Empty(shell.App.Slots.List());
    }

    [Fact]
    public void NetworkJoinsServerAndAsksPassword()
    {
        using var shell = new ShellHarness();
        shell.Frame();
        shell.Click("menu:network");
        shell.Click("menu:network:nick");
        shell.Type("Игрок 2");
        shell.Click("menu:network:connect");
        var session = Assert.Single(shell.Launcher.Sessions);
        Assert.Equal(new ServerLaunch(ShellPrefs.DefaultServer, "Игрок 2"), session.Launch);
        Assert.Equal((ShellPrefs.DefaultServer, "Игрок 2"), shell.App.Prefs.Server());

        // Сервер просит пароль: поле, ввод, повтор с паролем.
        session.State = SessionState.Password;
        shell.Frame();
        Assert.True(shell.Has("play:password"));
        shell.Type("секрет");
        shell.Press("enter");
        Assert.Equal(["секрет"], session.Passwords);

        // Не состоялась — ошибка и выход в меню.
        session.State = SessionState.Failed;
        session.Error = "сервер закрыл соединение";
        shell.Frame();
        shell.Click("play:menu");
        Assert.Null(shell.App.Session);
        Assert.Equal(MenuScreen.Main, shell.App.Menu.Screen);
        Assert.Equal(["ws://localhost:8787"], shell.App.Prefs.Servers().Select(server => server.Url));
    }

    [Fact]
    public void SettingsChangeLanguageVolumeAndRebindKeys()
    {
        using var shell = new ShellHarness();
        shell.Frame();
        shell.Click("menu:settings");
        Assert.Equal("ru", shell.App.Lang);
        shell.Click("settings:lang:1");
        Assert.Equal("en", shell.App.Lang);
        Assert.Equal("en", new ShellPrefs(shell.Storage).Lang);

        shell.Click("settings:muted");
        Assert.True(shell.App.Audio.Muted);
        Assert.Equal("1"u8.ToArray(), shell.Storage.Get(Kharos.Audio.AudioPrefs.MutedKey));

        // Клавиши — внизу окна: тело прокручивается.
        shell.Gui.ScrollTo("settings:body", 10_000);
        shell.Frame();
        shell.Click("settings:key:Chat");
        Assert.Equal(ShellKey.Chat, shell.App.Settings.Binding);
        Assert.True(shell.App.CapturesKeys);
        shell.Press("t");
        Assert.Null(shell.App.Settings.Binding);
        Assert.Equal("t", shell.App.Prefs.KeyOf(ShellKey.Chat));

        // F11 — полный экран (горячая клавиша оболочки), пока поле ввода её не держит.
        var fullscreen = new List<bool>();
        shell.App.OnFullscreen = fullscreen.Add;
        shell.Press("f11");
        Assert.Equal([true], fullscreen);
        shell.Click("settings:back");
        shell.Click("menu:network");
        shell.Click("menu:network:nick");
        Assert.True(shell.App.CapturesKeys);
        shell.Press("f11");
        Assert.Equal([true], fullscreen);
    }
}
