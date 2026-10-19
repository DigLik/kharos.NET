using Kharos.Client.Game;
using Kharos.Client.Ui;
using Kharos.Mod.Abi;
using S = Kharos.Mod.Sdk.Client;

namespace Kharos.Client.Tests.Game;

/// <summary>
/// Дополнения ABI клиента 2 после волны 1 Н6 — без смены версии: биты стиля ScrollEnd, Modal, KeepFocus; поле с
/// настройками (0x1C FieldWith); пунктир (0x27 Dashes, 0x28 DashedCircle). Запись SDK мода → разбор движком → поведение
/// интерфейса и рисунки; старые записи не меняются.
/// </summary>
public sealed class ClientAbiAdditionsTests : IDisposable
{
    private readonly Gui _ui = new(Fonts.Load());
    private double _time;

    public void Dispose() => _ui.Fonts.Dispose();

    /// <summary>Кадр мода body через вход и выход кадра; ввод ui.</summary>
    private ClientScene Stream(UiInput ui, Action<S.ClientContext> body)
    {
        var scene = new ClientScene(_ui) { LookOf = _ => null };
        var writer = new AbiWriter();
        _ui.Begin(ui);
        new FrameInput().Write(writer, new FrameClock(ui.Time, 1 / 60.0, 3, 0, 0.05, 1), new ClientInput { Width = 800, Height = 600 }, scene.Camera, _ui,
            scene.Atmosphere, [], null, [], 0);
        var output = new S.ClientContext().Frame(writer.Written, body);
        ClientStream.Apply(output, scene);
        _ui.Finish();
        Assert.Empty(scene.Errors);
        return scene;
    }

    private double Next() => _time += 0.016;

    private Node Find(string id)
    {
        Node? Walk(Node node)
        {
            if (node.Style.Id == id) return node;
            foreach (var child in node.Children)
                if (Walk(child) is { } found) return found;
            return null;
        }
        foreach (var root in _ui.Roots)
            if (Walk(root) is { } found) return found;
        throw new Xunit.Sdk.XunitException($"нет узла {id}");
    }

    private static byte[] Output(Action<S.ClientContext> body)
    {
        var writer = new AbiWriter();
        using var fonts = Fonts.Load();
        var gui = new Gui(fonts);
        gui.Begin(Frames.Off(0));
        new FrameInput().Write(writer, new FrameClock(0, 1 / 60.0, 0, 0, 0.05, 1), new ClientInput { Width = 800, Height = 600 }, default, gui, default, [], null, [], 0);
        return new S.ClientContext().Frame(writer.Written, body).ToArray();
    }

    [Fact]
    public void StyleFlagsReachTheEngine()
    {
        void Body(S.ClientContext c)
        {
            c.Ui.Box(new S.Style { Id = "log", Anchor = new S.Anchor(Left: 0, Top: 0), Width = 100, Height = 60, Scroll = true, ScrollEnd = true, Align = S.Align.Stretch }, () =>
            {
                for (var i = 0; i < 10; i++)
                    c.Ui.Box(new S.Style { Id = $"row:{i}", Height = 20 });
            });
            c.Ui.Box(new S.Style { Id = "dlg", Modal = true, Anchor = new S.Anchor(Left: 0, Right: 0, Top: 0, Bottom: 0) }, () =>
                c.Ui.Box(new S.Style { Id = "keep", KeepFocus = true, Width = 40, Height = 20 }));
        }
        Stream(Frames.Off(Next()), Body);
        Assert.True(Find("log").Style is { Scroll: true, ScrollEnd: true, Modal: false, KeepFocus: false });
        Assert.True(Find("dlg").Style is { Modal: true, ScrollEnd: false });
        Assert.True(Find("keep").Style is { KeepFocus: true, Modal: false });
        // Поведение: лента держится у низа (10 × 20 − 60), модальное окно забирает ввод.
        Assert.Equal(140, _ui.ScrollOf("log"));
        Assert.True(_ui.ModalOpen);
        Assert.Equal("dlg", _ui.ModalId);
        Assert.True(_ui.CapturesKeys);
    }

    [Fact]
    public void KeepFocusThroughAbiDoesNotBlurTheField()
    {
        var first = true;
        void Body(S.ClientContext c)
        {
            c.Ui.Box(new S.Style { Anchor = new S.Anchor(Left: 0, Top: 0) }, () =>
            {
                c.Ui.Field("f", "", new S.FieldOptions { Focus = first }, new S.Style { Width = 200, Height = 28 });
                c.Ui.Box(new S.Style { Id = "hint", Width = 50, Height = 20, KeepFocus = true });
                c.Ui.Box(new S.Style { Id = "plain", Width = 50, Height = 20 });
            });
            first = false;
        }
        Stream(Frames.Off(Next()), Body);
        Assert.True(_ui.Typing);
        Stream(Frames.At(Next(), 10, 40) with { Pressed = PointerButtons.Left }, Body);
        Assert.True(_ui.Typing);
        Stream(Frames.At(Next(), 10, 60) with { Pressed = PointerButtons.Left }, Body);
        Assert.False(_ui.Typing);
    }

    [Fact]
    public void FieldOptionsReachTheEngine()
    {
        var digits = "";
        var shortText = "";
        var secret = "";
        void Body(S.ClientContext c)
        {
            digits = c.Ui.Edited("n") ?? digits;
            shortText = c.Ui.Edited("s") ?? shortText;
            secret = c.Ui.Edited("p") ?? secret;
            c.Ui.Box(new S.Style { Id = "panel", Anchor = new S.Anchor(Left: 0, Top: 0) }, () =>
            {
                c.Ui.Field("n", digits, new S.FieldOptions { Digits = true }, new S.Style { Width = 200, Height = 28 });
                c.Ui.Field("s", shortText, new S.FieldOptions { MaxLength = 3, Placeholder = "имя" }, new S.Style { Width = 200, Height = 28 });
                c.Ui.Field("p", secret, new S.FieldOptions { Password = true }, new S.Style { Width = 200, Height = 28 });
            });
        }
        // Пустое поле с подсказкой показывает её.
        Stream(Frames.Off(Next()), Body);
        Assert.Contains("имя", Texts());
        void TypeInto(float y, string text)
        {
            Stream(Frames.At(Next(), 20, y) with { Pressed = PointerButtons.Left }, Body);
            Stream(Frames.At(Next(), 20, y) with { Released = PointerButtons.Left }, Body);
            Stream(Frames.Off(Next()) with { Text = text }, Body);
            Stream(Frames.Off(Next()), Body);
        }
        TypeInto(14, "a1b2");
        Assert.Equal("12", digits);
        TypeInto(42, "abcdef");
        Assert.Equal("abc", shortText);
        TypeInto(70, "secret");
        Assert.Equal("secret", secret);
        var texts = Texts();
        Assert.Contains("••••••", texts);
        Assert.DoesNotContain("secret", texts);
    }

    private List<string> Texts()
    {
        var texts = new List<string>();
        void Collect(Node node)
        {
            if (node.Kind == LeafKind.Text) texts.Add(node.Text);
            foreach (var child in node.Children) Collect(child);
        }
        foreach (var root in _ui.Roots) Collect(root);
        return texts;
    }

    [Fact]
    public void DashesAndDashedCircleAreDrawn()
    {
        var records = new List<FrameRecord>();
        var scene = new ClientScene(_ui) { LookOf = _ => null, Recorder = records.Add };
        var output = Output(c =>
        {
            c.Draw.Dashes(0, 0, 10, 0, 0xff0000, step: 32, dot: 4);
            c.Draw.DashedCircle(5, 5, 2, 0x00ff00, dashes: 12, dot: 3);
        });
        Assert.Equal(ClientAbi.OpDashes, output[0]);
        _ui.Begin(Frames.Off(Next()));
        ClientStream.Apply(output, scene);
        _ui.Finish();
        Assert.Empty(scene.Errors);
        // Камера по умолчанию — 32 пикселя на тайл: шаг 32 пикселя — тайл, точек на 10 тайлов — 10; по окружности — 12.
        Assert.Equal((10 + 12) * 4, scene.World.Vertices.Count);
        Assert.Equal([ClientAbi.OpDashes, ClientAbi.OpDashedCircle], records.Select(r => r.Op));
        Assert.Equal([0f, 0, 10, 0, 32, 4], records[0].Values);
        Assert.Equal([5f, 5, 2, 12, 3], records[1].Values);
    }

    [Fact]
    public void OldRecordsAreUnchanged()
    {
        // Поле без настроек — прежняя запись 0x1A; стиль без новых флагов — прежняя маска.
        var field = Output(c => c.Ui.Field("f", "v", new S.FieldOptions(), new S.Style { Width = 10 }));
        var old = Output(c => c.Ui.Field("f", "v", new S.Style { Width = 10 }));
        Assert.Equal(old, field);
        Assert.Equal(ClientAbi.OpField, field[0]);
        var withOptions = Output(c => c.Ui.Field("f", "v", new S.FieldOptions { MaxLength = 5 }, new S.Style { Width = 10 }));
        Assert.Equal(ClientAbi.OpFieldWith, withOptions[0]);
        // Та же запись и стиль (op, id, value, маска стиля, ширина — 15 байт), за ними хвост: маска 8 (maxLength) и 5.
        Assert.Equal(old.Length + 3, withOptions.Length);
        Assert.Equal(old[1..15], withOptions[1..15]);
        Assert.Equal([ClientAbi.FieldMaxLength, 5, 0], withOptions[15..18]);
    }

    [Fact]
    public void TruncatedAdditionsAreFormatErrors()
    {
        var scene = new ClientScene(_ui);
        foreach (var op in new[] { ClientAbi.OpFieldWith, ClientAbi.OpDashes, ClientAbi.OpDashedCircle })
            Assert.Throws<FormatException>(() => ClientStream.Apply([op, 1], scene));
    }
}
