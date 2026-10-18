using Kharos.Client.Game;
using Kharos.Client.Looks;
using Kharos.Client.Ui;
using Kharos.Gpu;
using Kharos.Protocol;
using S = Kharos.Mod.Sdk.Client;

namespace Kharos.Client.Tests.Game;

/// <summary>
/// Интерфейс клиентского мода: записи SDK (S.Ui), разобранные клиентом (ClientStream → Gui), дают тот же батч, что те
/// же вызовы Gui прямо на C#, — кадр за кадром, с наведением, подсказками и щелчками по местам прошлого кадра.
/// </summary>
public sealed class UiStreamTests : IDisposable
{
    private readonly Fonts _fonts = Fonts.Load();
    private readonly (Look[] Looks, LookAtlas Atlas) _looks = Demo.Atlas();

    public void Dispose() => _fonts.Dispose();

    private static readonly Color Gold = Color.Hex(0xf0c95a), Dim = Color.Hex(0x8fa9c2);

    /// <summary>Панель на Gui напрямую. Возвращает щелчки кнопок.</summary>
    private List<string> Direct(Gui ui)
    {
        var clicks = new List<string>();
        ui.Box(new Style { Id = "top", Anchor = new Anchor(Left: 0, Right: 0, Top: 0), Height = 38, Skin = Skin.Panel, Padding = (4, 12), Direction = Direction.Row, Align = Align.Center, Gap = 12 }, () =>
        {
            ui.Coin();
            ui.Text("1250", new TextStyle { Font = FontName.Title, Color = Gold, Shadow = Skins.Ink, Lift = 2 });
            ui.Space(grow: 1);
            ui.Image(_looks.Looks[0].Icon, 24, 24, 1);
            if (ui.Button("menu", Style.Button with { Padding = (2, 10), Height = 30 }, () => ui.Text("Меню")) is { } click)
                clicks.Add($"menu:{click.Button}");
            ui.Tip("menu", "Меню игры");
        });
        ui.Box(new Style { Id = "side", Anchor = new Anchor(Right: 8, CenterY: 0), Width = 220, Skin = Skin.Well, Padding = (6, 8, 10, 4), Gap = 4, Align = Align.Stretch, Alpha = 0.9f, Outline = Color.Hex(0x41617f) }, () =>
        {
            ui.Box(new Style { Id = "list", MaxHeight = 90, Scroll = true, Gap = 2, MinWidth = 100 }, () =>
            {
                for (var i = 0; i < 9; i++)
                    ui.Box(new Style { Id = $"row:{i}", Direction = Direction.Row, Justify = Align.Between, Background = i % 2 == 0 ? Color.Hex(0x1c2b3e) : null, Padding = (1, 4) }, () =>
                    {
                        ui.Text($"Строка {i}", new TextStyle { Color = Dim, Ellipsis = true });
                        ui.Text($"{i * 7}", new TextStyle { Font = FontName.Small });
                    });
            });
            ui.Box(new Style { Direction = Direction.Stack, Height = 40, Clip = true }, () =>
            {
                ui.Minimap(200, 40);
                ui.Box(new Style { Self = (Align.End, Align.Center), Padding = (0, 6) }, () => ui.Text("Карта", new TextStyle { Shadow = Skins.Ink }));
            });
            ui.Box(new Style { Direction = Direction.Row, Wrap = true, Gap = 4, MaxWidth = 200 }, () =>
            {
                for (var i = 0; i < 5; i++)
                {
                    var id = $"cell:{i}";
                    if (ui.Button(id, new Style { Width = 36, Height = 36, Skin = Skin.ButtonPrimary, Shrink = true }, () => ui.Image(_looks.Looks[i % 3].Icon, 28, 28), active: i == 2, disabled: i == 4) is { } click)
                        clicks.Add($"{id}:{click.Button}:{click.Long}");
                    ui.Tip(id, () => ui.Box(new Style { Gap = 4, Width = 180 }, () =>
                    {
                        ui.Text($"Клетка {i}", new TextStyle { Font = FontName.Title });
                        ui.Text("Длинная подсказка, которая переносится по ширине карточки.", new TextStyle { Color = Dim, Wrap = 180 });
                    }), i % 2 == 0 ? new Anchor(Right: 240, Bottom: 20) : null);
                }
            });
            ui.Text("Текст с переносом по ширине родителя, длинный-длинный.", new TextStyle { Wrap = float.PositiveInfinity, Lift = 1.5f });
            ui.Space(0, 6);
        });
        ui.Box(new Style { Anchor = new Anchor(CenterX: 0, Bottom: 4), Passive = true, Padding = 4, MinHeight = 20, Background = Color.Hex(0x05090f).Faded(0.5f) }, () =>
            ui.Text("Пассивная надпись", new TextStyle { Color = Color.White }));
        return clicks;
    }

    private static readonly S.Color SGold = S.Color.Hex(0xf0c95a), SDim = S.Color.Hex(0x8fa9c2), SInk = S.Color.Ink;

    /// <summary>Та же панель через SDK мода.</summary>
    private static List<string> Sdk(S.Ui ui)
    {
        var clicks = new List<string>();
        ui.Box(new S.Style { Id = "top", Anchor = new S.Anchor(Left: 0, Right: 0, Top: 0), Height = 38, Skin = S.Skin.Panel, Padding = (4, 12), Direction = S.Direction.Row, Align = S.Align.Center, Gap = 12 }, () =>
        {
            ui.Coin();
            ui.Text("1250", new S.TextStyle { Font = S.FontName.Title, Color = SGold, Shadow = SInk, Lift = 2 });
            ui.Space(grow: 1);
            ui.Image(1, 24, 24, 1);
            if (ui.Button("menu", S.Style.Button with { Padding = (2, 10), Height = 30 }, () => ui.Text("Меню")) is { } click)
                clicks.Add($"menu:{click.Button}");
            ui.Tip("menu", "Меню игры");
        });
        ui.Box(new S.Style { Id = "side", Anchor = new S.Anchor(Right: 8, CenterY: 0), Width = 220, Skin = S.Skin.Well, Padding = (6, 8, 10, 4), Gap = 4, Align = S.Align.Stretch, Alpha = 0.9f, Outline = S.Color.Hex(0x41617f) }, () =>
        {
            ui.Box(new S.Style { Id = "list", MaxHeight = 90, Scroll = true, Gap = 2, MinWidth = 100 }, () =>
            {
                for (var i = 0; i < 9; i++)
                    ui.Box(new S.Style { Id = $"row:{i}", Direction = S.Direction.Row, Justify = S.Align.Between, Background = i % 2 == 0 ? S.Color.Hex(0x1c2b3e) : null, Padding = (1, 4) }, () =>
                    {
                        ui.Text($"Строка {i}", new S.TextStyle { Color = SDim, Ellipsis = true });
                        ui.Text($"{i * 7}", new S.TextStyle { Font = S.FontName.Small });
                    });
            });
            ui.Box(new S.Style { Direction = S.Direction.Stack, Height = 40, Clip = true }, () =>
            {
                ui.Minimap(200, 40);
                ui.Box(new S.Style { Self = (S.Align.End, S.Align.Center), Padding = (0, 6) }, () => ui.Text("Карта", new S.TextStyle { Shadow = SInk }));
            });
            ui.Box(new S.Style { Direction = S.Direction.Row, Wrap = true, Gap = 4, MaxWidth = 200 }, () =>
            {
                for (var i = 0; i < 5; i++)
                {
                    var id = $"cell:{i}";
                    if (ui.Button(id, new S.Style { Width = 36, Height = 36, Skin = S.Skin.ButtonPrimary, Shrink = true }, () => ui.Image(i % 3 + 1, 28, 28), active: i == 2, disabled: i == 4) is { } click)
                        clicks.Add($"{id}:{click.Button}:{click.Long}");
                    ui.Tip(id, () => ui.Box(new S.Style { Gap = 4, Width = 180 }, () =>
                    {
                        ui.Text($"Клетка {i}", new S.TextStyle { Font = S.FontName.Title });
                        ui.Text("Длинная подсказка, которая переносится по ширине карточки.", new S.TextStyle { Color = SDim, Wrap = 180 });
                    }), i % 2 == 0 ? new S.Anchor(Right: 240, Bottom: 20) : null);
                }
            });
            ui.Text("Текст с переносом по ширине родителя, длинный-длинный.", new S.TextStyle { Wrap = float.PositiveInfinity, Lift = 1.5f });
            ui.Space(0, 6);
        });
        ui.Box(new S.Style { Anchor = new S.Anchor(CenterX: 0, Bottom: 4), Passive = true, Padding = 4, MinHeight = 20, Background = S.Color.Hex(0x05090f).Faded(0.5f) }, () =>
            ui.Text("Пассивная надпись", new S.TextStyle { Color = S.Color.White }));
        return clicks;
    }

    [Fact]
    public void StreamDecodesToSameBatchAsDirectGui()
    {
        var direct = new Gui(_fonts);
        var decoded = new Gui(_fonts);
        var scene = new ClientScene(decoded) { LookOf = proto => proto is >= 1 and <= 3 ? _looks.Looks[proto - 1] : null };
        var mod = new S.ClientContext();
        var input = new FrameInput();
        var writer = new AbiWriter();
        var batch = new Batch();

        // Кадры: без указателя; наведение на «menu»; держим (подсказка); нажатие и отпускание на cell:1; на выключенной
        // cell:4; колесо над списком; долгое касание cell:0 (правый щелчок); наведение на карточку cell:2 с привязкой.
        var script = new (double Time, string Target, PointerButtons Pressed, PointerButtons Released, float Wheel, bool Touch)[]
        {
            (0.1, "menu", 0, 0, 0, false),
            (0.6, "menu", 0, 0, 0, false),
            (0.7, "cell:1", 0, 0, 0, false),
            (0.75, "cell:1", PointerButtons.Left, 0, 0, false),
            (0.8, "cell:1", 0, PointerButtons.Left, 0, false),
            (0.85, "cell:1", 0, 0, 0, false),
            (0.9, "cell:4", PointerButtons.Left, 0, 0, false),
            (0.95, "cell:4", 0, PointerButtons.Left, 0, false),
            (1.0, "row:3", 0, 0, -2, false),
            (1.1, "row:3", 0, 0, 0, false),
            (1.2, "cell:0", PointerButtons.Left, 0, 0, true),
            (1.8, "cell:0", 0, 0, 0, true),
            (1.9, "cell:0", 0, PointerButtons.Left, 0, true),
            (2.0, "cell:2", 0, 0, 0, false),
            (2.5, "cell:2", 0, 0, 0, false),
            (2.6, "cell:3", 0, 0, 0, false),
            (3.1, "cell:3", 0, 0, 0, false),
        };
        var allDirect = new List<string>();
        var allSdk = new List<string>();
        var tips = 0;
        for (var frame = 0; frame <= script.Length; frame++)
        {
            var clientInput = new ClientInput { Time = 0, Width = 1024, Height = 640 };
            if (frame > 0)
            {
                var (time, target, pressed, released, wheel, touch) = script[frame - 1];
                var rect = direct.RectOf(target) ?? throw new InvalidOperationException($"нет узла {target}");
                clientInput = clientInput with { Time = time, Pointer = (rect.X + rect.W / 2, rect.Y + rect.H / 2), Pressed = pressed, Released = released, Wheel = wheel, Touch = touch };
            }
            var ui = clientInput.ToUi();

            direct.Begin(ui);
            allDirect.AddRange(Direct(direct));
            direct.Finish();

            decoded.Begin(ui);
            input.Write(writer, new FrameClock(clientInput.Time, 1 / 60.0, 1, 0, 0.05, 1), clientInput, scene.Camera, decoded, scene.Atmosphere, [], null, []);
            var output = mod.Frame(writer.Written, c => allSdk.AddRange(Sdk(c.Ui)));
            ClientStream.Apply(output, scene);
            batch.Clear();
            decoded.Finish(batch);

            Assert.Equal(direct.Batch.Vertices, batch.Vertices);
            Assert.Equal(direct.Batch.Indices, batch.Indices);
            Assert.Equal(direct.Batch.Segments, batch.Segments);
            Assert.Equal(direct.Hits, decoded.Hits);
            if (direct.Roots.Count > 3) tips++;
        }
        Assert.Equal(allDirect, allSdk);
        // Щелчок по cell:1 и правый щелчок долгим касанием по cell:0 дошли; по выключенной cell:4 — нет.
        Assert.Contains("cell:1:0:False", allSdk);
        Assert.Contains("cell:0:2:True", allSdk);
        Assert.DoesNotContain(allSdk, click => click.StartsWith("cell:4", StringComparison.Ordinal));
        Assert.True(tips >= 3, $"подсказок: {tips}");
        Assert.Empty(scene.Errors);
    }

    [Fact]
    public void UnbalancedModNodesAreClosed()
    {
        var gui = new Gui(_fonts);
        var scene = new ClientScene(gui);
        var mod = new S.ClientContext();
        var writer = new AbiWriter();
        var clientInput = new ClientInput { Width = 800, Height = 600 };
        gui.Begin(clientInput.ToUi());
        new FrameInput().Write(writer, default, clientInput, scene.Camera, gui, scene.Atmosphere, [], null, []);
        // Мод открыл панель и упал: SDK закрывает узлы сам; поток с лишним Close разбирается без вреда.
        var output = mod.Frame(writer.Written, c =>
        {
            c.Ui.Open(new S.Style { Id = "a", Anchor = new S.Anchor(Left: 0, Top: 0) });
            c.Ui.Text("а");
        }).ToArray();
        ClientStream.Apply([.. output, 0x11, 0x11], scene);
        gui.Box(new Style { Id = "b", Anchor = new Anchor(Right: 0, Top: 0) }, () => gui.Text("b"));
        gui.Finish();
        Assert.Equal(2, gui.Roots.Count);
        Assert.Throws<FormatException>(() => ClientStream.Apply([0x10, 0x01], scene));
    }

    [Fact]
    public void SignalsAndInputReachTheMod()
    {
        var gui = new Gui(_fonts);
        var scene = new ClientScene(gui) { Camera = new CameraState(10, 20, 32) };
        var writer = new AbiWriter();
        var clientInput = new ClientInput
        {
            Time = 5, Width = 800, Height = 600, Pointer = (400 + 64, 300 - 32), Down = PointerButtons.Left | PointerButtons.Middle,
            Pressed = PointerButtons.Right, Wheel = 1.5f, Shift = true, Keys = ["a", "shift"], Hits = ["a"], Text = "ё",
        };
        gui.Begin(clientInput.ToUi());
        new FrameInput().Write(writer, new FrameClock(5, 0.02, 41, 0.5, 0.05, 3), clientInput, scene.Camera, gui, new AtmosphereState(0.4f, 1, 2, 0.3f),
            [new ReplicaSignal(40, new Kharos.Engine.World.Signal("hello.beep", [1, 2, 3]))], 1.25, [7u]);
        var mod = new S.ClientContext();
        var seen = new List<string>();
        mod.Frame(writer.Written, c =>
        {
            seen.Add($"{c.Time} {c.Dt} {c.Tick} {c.Alpha} {c.Clock:0.000} {c.Player} {c.ScreenWidth}x{c.ScreenHeight}");
            seen.Add($"{c.Camera.X} {c.Camera.Y} {c.Camera.Zoom} {c.Input.WorldX} {c.Input.WorldY}");
            seen.Add($"{c.Input.Down(S.PointerButton.Left)} {c.Input.Down(S.PointerButton.Right)} {c.Input.Pressed(S.PointerButton.Right)} {c.Input.Wheel} {c.Input.Shift} {string.Join(",", c.Input.Keys.Order())} {string.Join(",", c.Input.Hits)} {c.Input.Text}");
            seen.Add($"{c.Atmosphere.Light} {c.Atmosphere.WindY} {c.Audio.Available} {c.Audio.Now}");
            seen.Add(string.Join(";", c.Signals.Select(s => $"{s.Tick}:{s.Name}:{s.Payload.Length}")));
            seen.Add($"{c.Nearness(10, 20):0.00} {c.Side(10 + 400 / 32.0):0.00}");
        });
        Assert.Equal(
        [
            "5 0.02 41 0.5 2.075 3 800x600",
            "10 20 32 12 19",
            "True False True 1.5 True a,shift a ё",
            "0.4 2 True 1.25",
            "40:hello.beep:3",
            "1.00 1.00",
        ], seen);
    }
}
