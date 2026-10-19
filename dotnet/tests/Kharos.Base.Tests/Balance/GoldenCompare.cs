using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kharos.Balance;

namespace Kharos.Base.Tests.Balance;

/// <summary>Итог сверки раздела с эталоном TS: расхождения, пропущенные куски и сколько ячеек сверено.</summary>
public sealed record CompareResult(List<string> Diffs, List<string> Skipped, int Cells)
{
    /// <summary>Доля ячеек и строк с расхождением.</summary>
    public double Share => Cells == 0 ? 0 : (double)Diffs.Count / Cells;
}

/// <summary>
/// Сверка вывода харнесса .NET (<see cref="Item"/>) с эталоном TS (golden/*.json) с допуском: числа — на 5% (целые — ещё и на
/// единицу: счёт ±1), время «м:сс» — на 2 секунды (или 5%), строки — как есть. Кусок, который .NET пропускает
/// (<see cref="SkipItem"/>), закрывает столько элементов эталона, сколько в нём сказано, и сверку не проходит, а попадает в
/// отчёт. Таблицы замеров времени тика (заголовок «сцена, стоят, идут…») зависят от машины и числами не сверяются.
/// </summary>
public static class GoldenCompare
{
    /// <summary>Относительный допуск чисел (доли).</summary>
    public const double Share = 0.05;

    /// <summary>Допуск времени «м:сс», секунды.</summary>
    public const double Seconds = 2;

    /// <summary>Допуск счёта целых чисел.</summary>
    public const double Count = 1;

    /// <summary>Папка эталонов рядом с тестами (копируется в вывод).</summary>
    public static string GoldenDir => Path.Combine(AppContext.BaseDirectory, "Balance", "golden");

    /// <summary>Элементы эталона раздела <paramref name="name"/> (файл golden/&lt;name&gt;.json).</summary>
    public static JsonArray Golden(string name) =>
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(GoldenDir, name + ".json")))!["items"]!.AsArray();

    /// <summary>Сверяет вывод <paramref name="actual"/> с элементами эталона, начиная с <paramref name="offset"/>.</summary>
    public static CompareResult Compare(JsonArray golden, IReadOnlyList<Item> actual, int offset = 0)
    {
        var diffs = new List<string>();
        var skipped = new List<string>();
        var cells = 0;
        var at = offset;
        for (var i = 0; i < actual.Count; i++, at++)
        {
            var item = actual[i];
            if (item is SkipItem skip)
            {
                skipped.Add($"элементы эталона {at}..{at + skip.Covers - 1}: {skip.Reason}");
                at += skip.Covers - 1;
                continue;
            }
            if (at >= golden.Count)
            {
                diffs.Add($"#{at}: в эталоне нет такого элемента, а .NET напечатал {Describe(item)}");
                continue;
            }
            var expected = golden[at]!.AsObject();
            switch (item)
            {
                case TextItem text when expected["text"] is { } line:
                    cells++;
                    if (CompareText(line.GetValue<string>(), text.Text) is { } why)
                        diffs.Add($"#{at} текст: {why}\n    TS:   {line.GetValue<string>()}\n    .NET: {text.Text}");
                    break;
                case TableItem table when expected["table"] is JsonObject golden_:
                    cells += CompareTable(at, golden_, table, diffs);
                    break;
                default:
                    diffs.Add($"#{at}: в эталоне {(expected["text"] is not null ? "текст" : "таблица")}, а .NET напечатал {Describe(item)}");
                    break;
            }
        }
        if (at < golden.Count && actual.Count > 0 && actual[^1] is not SkipItem)
            diffs.Add($"в эталоне ещё {golden.Count - at} элементов после #{at - 1}, которых .NET не напечатал");
        return new CompareResult(diffs, skipped, cells);
    }

    private static string Describe(Item item) => item switch
    {
        TextItem text => "текст «" + text.Text + "»",
        TableItem table => "таблицу «" + string.Join(", ", table.Head) + "»",
        SkipItem skip => "пропуск (" + skip.Reason + ")",
        _ => item.ToString() ?? "",
    };

    private static int CompareTable(int at, JsonObject golden, TableItem actual, List<string> diffs)
    {
        var head = golden["head"]!.AsArray().Select(cell => Text(cell)).ToList();
        var actualHead = actual.Head.Select(cell => Convert.ToString(cell, CultureInfo.InvariantCulture) ?? "").ToList();
        if (!head.SequenceEqual(actualHead))
        {
            diffs.Add($"#{at} заголовок таблицы: TS [{string.Join(", ", head)}], .NET [{string.Join(", ", actualHead)}]");
            return 1;
        }
        var rows = golden["rows"]!.AsArray();
        if (rows.Count != actual.Rows.Count)
            diffs.Add($"#{at} таблица [{string.Join(", ", head)}]: строк {rows.Count} в TS и {actual.Rows.Count} в .NET");
        // Замеры времени тика зависят от машины: форму сверяем, числа — нет.
        var timings = head.Count >= 4 && head[0] == "сцена" && head[1] == "стоят";
        var count = 0;
        for (var r = 0; r < Math.Min(rows.Count, actual.Rows.Count); r++)
        {
            var expected = rows[r]!.AsArray();
            var row = actual.Rows[r];
            if (expected.Count != row.Length)
            {
                diffs.Add($"#{at} строка {r}: ячеек {expected.Count} в TS и {row.Length} в .NET");
                continue;
            }
            for (var c = 0; c < row.Length; c++)
            {
                count++;
                if (timings && c > 0)
                    continue;
                if (CompareCell(expected[c]!, row[c]) is { } why)
                    diffs.Add($"#{at} строка {r} «{Text(expected[0])}», столбец «{head[c]}»: {why} (TS {Text(expected[c])}, .NET {Convert.ToString(row[c], CultureInfo.InvariantCulture)})");
            }
        }
        return count;
    }

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToJsonString() ?? "";

    private static string? CompareCell(JsonNode golden, object actual)
    {
        if (golden is JsonValue value && value.TryGetValue<double>(out var number))
            return actual is string text
                ? (double.TryParse(text, CultureInfo.InvariantCulture, out var parsed) && Close(number, parsed) ? null : "в TS число, в .NET строка")
                : Close(number, Convert.ToDouble(actual, CultureInfo.InvariantCulture)) ? null : "число вне допуска";
        return actual is string actualText ? CompareText(Text(golden), actualText) : $"в TS строка, в .NET {actual}";
    }

    /// <summary>Числа в допуске: 5% (целые — ещё и ±1).</summary>
    public static bool Close(double golden, double actual)
    {
        var tolerance = Math.Max(Share * Math.Abs(golden), golden == Math.Floor(golden) ? Count : 0);
        return Math.Abs(golden - actual) <= tolerance + 1e-9;
    }

    private static readonly Regex Token = new(@"(\d+:\s?\d{1,2})|(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private static double Clock(string time)
    {
        var parts = time.Split(':');
        return int.Parse(parts[0], CultureInfo.InvariantCulture) * 60 + int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
    }

    /// <summary>Строки равны с допуском на числа и время внутри; null — в допуске.</summary>
    public static string? CompareText(string golden, string actual)
    {
        if (golden == actual)
            return null;
        var expected = Token.Matches(golden);
        var found = Token.Matches(actual);
        if (expected.Count != found.Count || Token.Replace(golden, "#") != Token.Replace(actual, "#"))
            return "текст отличается";
        for (var i = 0; i < expected.Count; i++)
        {
            var (g, a) = (expected[i], found[i]);
            if (g.Groups[1].Success != a.Groups[1].Success)
                return "число вместо времени";
            if (g.Groups[1].Success)
            {
                var (gt, at) = (Clock(g.Value), Clock(a.Value));
                if (Math.Abs(gt - at) > Math.Max(Seconds, Share * gt))
                    return $"время {g.Value.Trim()} против {a.Value.Trim()}";
            }
            else if (!Close(double.Parse(g.Value, CultureInfo.InvariantCulture), double.Parse(a.Value, CultureInfo.InvariantCulture)))
                return $"число {g.Value} против {a.Value}";
        }
        return null;
    }
}
