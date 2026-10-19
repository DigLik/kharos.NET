namespace Kharos.Client.Ui;

/// <summary>
/// Кусок строки со значками, как <c>Run</c> в ui.ts: текст своим стилем или значок облика размером size × size.
/// Значок без картинки (облика нет) занимает место пустотой.
/// </summary>
public readonly record struct Run(string? Text, TextStyle Style, Icon? Icon = null, float Size = 0, int Team = 0)
{
    public static Run Of(string text, TextStyle style = default) => new(text, style.Equals(default(TextStyle)) ? new TextStyle() : style);
    public static Run Glyph(Icon? icon, float size, int team = 0) => new(null, default, icon, size, team);
}

public sealed partial class Gui
{
    /// <summary>
    /// Строка из текста и значков с переносом (flow в ui.ts): ряд по центру высоты строки, куски — словами и
    /// значками; ряд переносится, когда следующий кусок не влезает в width (без неё — в ширину родителя).
    /// </summary>
    public void Rich(IReadOnlyList<Run> runs, float? width = null)
    {
        Open(new Style { Direction = Direction.Row, Wrap = true, Align = Align.Center, Width = width });
        try
        {
            foreach (var run in runs)
            {
                if (run.Text is null)
                {
                    if (run.Icon is { } icon) Image(icon, run.Size, run.Size, run.Team);
                    else Space(run.Size, run.Size);
                    continue;
                }
                foreach (var word in Words(run.Text)) Text(word, run.Style);
            }
        }
        finally { Close(); }
    }

    /// <summary>Слова с пробелом после каждого (пробел остаётся в конце строки, а не в начале новой); пробелы подряд — один.</summary>
    internal static List<string> Words(string text)
    {
        var words = new List<string>();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"\s*\S+\s*"))
            words.Add(System.Text.RegularExpressions.Regex.Replace(match.Value, @"\s+", " "));
        if (words.Count == 0 && text.Length > 0) words.Add(" ");
        return words;
    }
}
