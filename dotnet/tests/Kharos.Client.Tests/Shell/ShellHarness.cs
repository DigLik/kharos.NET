using Kharos.Client.Game;
using Kharos.Client.Shell;
using Kharos.Client.Ui;

namespace Kharos.Client.Tests.Shell;

/// <summary>Партия-подделка: сама не подключается, состояние ставит тест.</summary>
internal sealed class FakeSession(Launch launch) : IGameSession
{
    public Launch Launch { get; } = launch;
    public SessionState State { get; set; } = SessionState.Connecting;
    public string? Status { get; set; } = "подключаюсь";
    public string? Error { get; set; }
    public bool WrongPassword { get; set; }
    public ClientGame? Game { get; set; }
    public List<string> Passwords { get; } = [];
    public int Polls { get; private set; }
    public bool Left { get; private set; }
    public bool Disposed { get; private set; }

    public void Poll() => Polls++;
    public void Retry(string password)
    {
        Passwords.Add(password);
        State = SessionState.Connecting;
    }
    public void Leave() => Left = true;
    public void Dispose() => Disposed = true;
}

/// <summary>Запуски, которые просила оболочка, и их подделки.</summary>
internal sealed class FakeLauncher : IGameLauncher
{
    public List<FakeSession> Sessions { get; } = [];

    /// <summary>Чем заменить партию (тест с настоящей партией).</summary>
    public Func<Launch, IGameSession>? Real { get; set; }

    public IGameSession Start(Launch launch)
    {
        if (Real is { } real)
            return real(launch);
        var session = new FakeSession(launch);
        Sessions.Add(session);
        return session;
    }
}

/// <summary>
/// Оболочка без окна и звука: кадры с указателем и клавишами, щелчок по узлу по его месту прошлого кадра (нажатие
/// и отпускание в двух кадрах, как у мыши).
/// </summary>
internal sealed class ShellHarness : IDisposable
{
    private double _time;

    public ShellHarness(IStorage? storage = null)
    {
        Fonts = Fonts.Load();
        Storage = storage ?? new MemoryStorage();
        App = new ShellApp(new ShellAppOptions { Fonts = Fonts, Storage = Storage, Launcher = Launcher });
    }

    public Fonts Fonts { get; }
    public IStorage Storage { get; }
    public FakeLauncher Launcher { get; } = new();
    public ShellApp App { get; }

    /// <summary>Интерфейс, в котором сейчас окна оболочки: партии или меню.</summary>
    public Gui Gui => App.Session?.Game?.Gui ?? App.Gui;

    public (float X, float Y)? Pointer { get; set; } = (2, 2);

    public Kharos.Gpu.Frame Frame(PointerButtons pressed = PointerButtons.None, PointerButtons released = PointerButtons.None, string[]? hits = null, string text = "")
    {
        _time += 1 / 60.0;
        return App.Step(new ClientInput
        {
            Time = _time, Width = 1280, Height = 800, Pointer = Pointer, Pressed = pressed, Released = released,
            Hits = hits ?? [], Keys = hits ?? [], Text = text, Ctrl = hits?.Contains("ctrl") == true, Shift = hits?.Contains("shift") == true,
        });
    }

    /// <summary>Есть ли узел id в прошлом кадре.</summary>
    public bool Has(string id) => Gui.RectOf(id) is { W: > 0, H: > 0 };

    /// <summary>Щелчок по узлу id: нажатие и отпускание над его серединой.</summary>
    public void Click(string id)
    {
        if (Gui.RectOf(id) is not { W: > 0, H: > 0 } rect)
        {
            Frame();
            Assert.True(Has(id), $"нет узла {id}: {string.Join(", ", Gui.Hits.Select(hit => hit.Id).Take(40))}");
            rect = Gui.RectOf(id)!.Value;
        }
        Pointer = (rect.X + rect.W / 2, rect.Y + rect.H / 2);
        Frame(pressed: PointerButtons.Left);
        Frame(released: PointerButtons.Left);
        Pointer = (2, 2);
        Frame();
    }

    public void Press(params string[] keys) => Frame(hits: keys);

    public void Type(string text) => Frame(text: text);

    public void Dispose()
    {
        App.Dispose();
        Fonts.Dispose();
    }
}
