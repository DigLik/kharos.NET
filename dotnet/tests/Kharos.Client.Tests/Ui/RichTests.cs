using Kharos.Client.Game;
using Kharos.Client.Looks;
using Kharos.Client.Ui;
using Kharos.Protocol;
using S = Kharos.Mod.Sdk.Client;

namespace Kharos.Client.Tests.Ui;

/// <summary>Значки внутри текста: ряд из слов и значков, центровка по высоте строки, перенос, запись Icon в потоке мода.</summary>
public sealed class RichTests : IDisposable
{
    private readonly Gui _ui = new(Fonts.Load());
    private readonly (Look[] Looks, LookAtlas Atlas) _looks = Demo.Atlas();

    public void Dispose() => _ui.Fonts.Dispose();

    private Icon Glyph => _looks.Looks[0].Icon;

    private static IEnumerable<Node> Leaves(Node node)
    {
        if (node.Children.Count == 0) yield return node;
        foreach (var child in node.Children)
            foreach (var leaf in Leaves(child))
                yield return leaf;
    }

    [Fact]
    public void WordsKeepTheirSpaceAtTheEnd()
    {
        Assert.Equal(["a ", "b "], Gui.Words("a   b "));
        Assert.Equal([" x"], Gui.Words(" x"));
        Assert.Equal([" "], Gui.Words("  "));
        Assert.Empty(Gui.Words(""));
    }

    [Fact]
    public void IconSitsInTheMiddleOfTheTextLine()
    {
        _ui.Begin(Frames.Off(0));
        _ui.Box(new Style { Id = "p", Anchor = new Anchor(Left: 10, Top: 10) }, () =>
            _ui.Rich([Run.Of("Цена "), Run.Glyph(Glyph, 24), Run.Of(" золота")]));
        _ui.Finish();
        var row = _ui.Roots[0].Children[0];
        Assert.Equal(Direction.Row, row.Style.Direction);
        var texts = row.Children.Where(node => node.Kind == LeafKind.Text).ToList();
        var icon = row.Children.Single(node => node.Kind != LeafKind.Text);
        // Значок выше строки текста — ряд высотой со значок, текст посередине, значок слева от второго куска.
        Assert.Equal(24, row.Rect.H);
        Assert.Equal(24, icon.Rect.H);
        foreach (var text in texts)
            Assert.Equal(row.Rect.Y + 4, text.Rect.Y);
        var order = row.Children.Select(node => node.Rect.X).ToList();
        Assert.Equal(order.Order(), order);
        Assert.True(icon.Rect.X > texts[0].Rect.X && icon.Rect.X < texts[^1].Rect.X);
        Assert.Equal(icon.Rect.X + 24, texts[^1].Rect.X);
    }

    [Fact]
    public void TextShorterThanIconIsCenteredEvenWithSmallIcon()
    {
        _ui.Begin(Frames.Off(0));
        _ui.Box(new Style { Anchor = new Anchor(Left: 0, Top: 0) }, () =>
            _ui.Rich([Run.Glyph(Glyph, 8), Run.Of("abc")]));
        _ui.Finish();
        var row = _ui.Roots[0].Children[0];
        var line = Fonts.Line(FontName.Body);
        Assert.Equal(line, row.Rect.H);
        Assert.Equal((line - 8) / 2, row.Children[0].Rect.Y);
    }

    [Fact]
    public void RowWrapsWordsAndKeepsIconsWithTheirLine()
    {
        _ui.Begin(Frames.Off(0));
        _ui.Box(new Style { Anchor = new Anchor(Left: 0, Top: 0) }, () =>
            _ui.Rich([Run.Of("alpha beta gamma delta "), Run.Glyph(Glyph, 12), Run.Of(" epsilon zeta")], width: 90));
        _ui.Finish();
        var row = _ui.Roots[0].Children[0];
        Assert.Equal(90, row.Rect.W);
        var leaves = Leaves(row).Where(node => node.Rect.W > 0).ToList();
        Assert.True(leaves.Select(node => node.Rect.Y).Distinct().Count() > 1, "перенос на вторую строку");
        foreach (var leaf in leaves)
            Assert.True(leaf.Rect.X + leaf.Rect.W <= 90.5f, $"кусок за краем: {leaf.Rect}");
        // Слова идут по порядку: ниже — правее или на новой строке.
        for (var i = 1; i < leaves.Count; i++)
            Assert.True(leaves[i].Rect.Y > leaves[i - 1].Rect.Y || leaves[i].Rect.X >= leaves[i - 1].Rect.X);
    }

    [Fact]
    public void IconWithoutLookTakesItsPlace()
    {
        _ui.Begin(Frames.Off(0));
        _ui.Box(new Style { Anchor = new Anchor(Left: 0, Top: 0) }, () =>
            _ui.Rich([Run.Of("a"), Run.Glyph(null, 10), Run.Of("b")]));
        _ui.Finish();
        var row = _ui.Roots[0].Children[0];
        Assert.Equal(LeafKind.Space, row.Children[1].Kind);
        Assert.Equal(10, row.Children[1].Rect.W);
    }

    // ------------------------------------------------------------------ записи потока: Text и Icon подряд

    private ClientScene Apply(Action<S.ClientContext> body)
    {
        var scene = new ClientScene(_ui) { LookOf = proto => proto is >= 1 and <= 3 ? _looks.Looks[proto - 1] : null };
        var writer = new AbiWriter();
        var input = new ClientInput { Width = 800, Height = 600 };
        _ui.Begin(input.ToUi());
        new FrameInput().Write(writer, default, input, scene.Camera, _ui, scene.Atmosphere, [], null, []);
        var output = new S.ClientContext().Frame(writer.Written, body);
        ClientStream.Apply(output, scene);
        _ui.Finish();
        Assert.Empty(scene.Errors);
        return scene;
    }

    [Fact]
    public void TextIconTextRecordsFormOneRunLine()
    {
        Apply(c => c.Ui.Box(new S.Style { Id = "panel", Anchor = new S.Anchor(Left: 0, Top: 0) }, () =>
        {
            c.Ui.Text("Цена");
            c.Ui.Icon(1, 0, 12);
            c.Ui.Text("золота");
            c.Ui.Text("отдельно");
        }));
        var panel = _ui.Roots[0];
        // Ряд из слов и значка, и вторая строка текста под ним.
        Assert.Equal(2, panel.Children.Count);
        Assert.Equal(Direction.Row, panel.Children[0].Style.Direction);
        Assert.True(panel.Children[0].Style.Wrap);
        Assert.Equal(3, panel.Children[0].Children.Count);
        Assert.Equal(LeafKind.Text, panel.Children[1].Kind);
        Assert.Equal("отдельно", panel.Children[1].Text);
        Assert.True(panel.Children[1].Rect.Y >= panel.Children[0].Rect.Y + panel.Children[0].Rect.H);
    }

    [Fact]
    public void LoneTextsStaySeparateLinesAndLoneIconStaysAnImage()
    {
        Apply(c =>
        {
            c.Ui.Box(new S.Style { Id = "texts", Anchor = new S.Anchor(Left: 0, Top: 0) }, () =>
            {
                c.Ui.Text("раз");
                c.Ui.Text("два");
            });
            c.Ui.Box(new S.Style { Id = "icons", Anchor = new S.Anchor(Left: 200, Top: 0) }, () =>
            {
                c.Ui.Icon(1, 0, 12);
                c.Ui.Icon(2, 0, 12);
            });
        });
        var (texts, icons) = (_ui.Roots[0], _ui.Roots[1]);
        Assert.Equal(2, texts.Children.Count);
        Assert.All(texts.Children, node => Assert.Equal(LeafKind.Text, node.Kind));
        Assert.Equal(2, icons.Children.Count);
        Assert.All(icons.Children, node => Assert.Equal(12, node.Rect.W));
    }

    [Fact]
    public void IconThenTextJoinsTheRunButNextTextDoesNot()
    {
        Apply(c => c.Ui.Box(new S.Style { Id = "panel", Anchor = new S.Anchor(Left: 0, Top: 0) }, () =>
        {
            c.Ui.Icon(1, 0, 12);
            c.Ui.Text("5 золота");
            c.Ui.Text("ниже");
        }));
        var panel = _ui.Roots[0];
        Assert.Equal(2, panel.Children.Count);
        Assert.Equal(Direction.Row, panel.Children[0].Style.Direction);
        Assert.Equal("ниже", panel.Children[1].Text);
    }
}
