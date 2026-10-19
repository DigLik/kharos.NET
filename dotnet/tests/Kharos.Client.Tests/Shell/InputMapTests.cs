using Kharos.Desktop;
using SilkKey = Silk.NET.Input.Key;

namespace Kharos.Client.Tests.Shell;

/// <summary>Ввод десктопа без окна: имена клавиш, Ctrl и Cmd, набранный текст из символов, повтор не нажатие.</summary>
public sealed class InputMapTests
{
    [Fact]
    public void KeysHaveNamesForFieldsAndMods()
    {
        Assert.Equal("home", InputMap.Name(SilkKey.Home));
        Assert.Equal("end", InputMap.Name(SilkKey.End));
        Assert.Equal("v", InputMap.Name(SilkKey.V));
        Assert.Equal("7", InputMap.Name(SilkKey.Number7));
        Assert.Equal("7", InputMap.Name(SilkKey.Keypad7));
        Assert.Equal("f11", InputMap.Name(SilkKey.F11));
        Assert.Equal("/", InputMap.Name(SilkKey.Slash));
        Assert.Equal("/", InputMap.Name(SilkKey.KeypadDivide));
        Assert.Equal("enter", InputMap.Name(SilkKey.KeypadEnter));
        Assert.Equal("delete", InputMap.Name(SilkKey.Delete));
        Assert.Equal("meta", InputMap.Name(SilkKey.SuperLeft));
        Assert.Null(InputMap.Name(SilkKey.Unknown));
    }

    [Fact]
    public void CtrlLettersReachFieldsAndCharsBecomeText()
    {
        var input = new InputMap();
        input.Key(SilkKey.ControlLeft, true);
        input.Key(SilkKey.V, true);
        // Ctrl+V на Windows даёт и управляющий знак: он не текст.
        input.Char('\u0016');
        var frame = input.Take(0, 800, 600, 1, 1);
        Assert.True(frame.Ctrl);
        Assert.Equal(["ctrl", "v"], frame.Hits);
        Assert.Equal("", frame.Text);

        // Повтор зажатой клавиши — не новое нажатие; отпущенная уходит из зажатых.
        input.Key(SilkKey.V, true);
        input.Key(SilkKey.ControlLeft, false);
        input.Key(SilkKey.V, false);
        frame = input.Take(0.1, 800, 600, 1, 1);
        Assert.Empty(frame.Hits);
        Assert.Empty(frame.Keys);
        Assert.False(frame.Ctrl);

        // Cmd на macOS — то же, что Ctrl; символы — текст кадра.
        input.Key(SilkKey.SuperLeft, true);
        input.Key(SilkKey.Home, true);
        input.Char('П');
        input.Char('/');
        frame = input.Take(0.2, 800, 600, 1, 1);
        Assert.True(frame.Ctrl);
        Assert.Equal(["meta", "home"], frame.Hits);
        Assert.Equal("П/", frame.Text);
        Assert.Equal("", input.Take(0.3, 800, 600, 1, 1).Text);

        // Окно потеряло фокус — зажатое отпускается.
        input.ReleaseAll();
        Assert.Empty(input.Take(0.4, 800, 600, 1, 1).Keys);
    }
}
