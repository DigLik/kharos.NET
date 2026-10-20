using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kharos.Desktop;

/// <summary>
/// Смоук (Н7.3): KHAROS_SMOKE — файл сценария, KHAROS_SMOKE_OUT — файл отчёта (по умолчанию smoke-report.json в
/// профиле, то есть в KHAROS_HOME).
/// </summary>
public sealed record SmokeOptions(string Script, string? Report);

/// <summary>
/// Шаг сценария: строка <c>do</c> — глагол и его слова («click menu:new», «wait tick&gt;=+200»), необязательный
/// <c>timeout</c> в секундах и прочие поля объекта (у <c>order</c> — name, args, payload; у <c>type</c> и <c>chat</c> —
/// text, если в тексте важны пробелы).
/// </summary>
internal sealed record SmokeStep(int Index, string Line, string Verb, string Rest, double? Timeout, JsonElement Raw)
{
    /// <summary>Слова после глагола.</summary>
    public string[] Words { get; } = Rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Строковое поле объекта шага; нет — null.</summary>
    public string? Field(string name) =>
        Raw.ValueKind == JsonValueKind.Object && Raw.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// Разбор сценария смоука. Файл — массив шагов или объект <c>{ "timeout": с, "steps": [...] }</c> (timeout — срок ожидания
/// по умолчанию). Шаг — <c>{ "do": "глагол слова…", "timeout": с }</c>; глаголы — <see cref="Verbs"/>.
/// </summary>
internal static class SmokeScript
{
    public static readonly string[] Verbs = ["click", "type", "key", "wait", "order", "chat", "save", "leave", "load", "editor", "connect", "shot", "mark", "assert"];

    public static (List<SmokeStep> Steps, double? Timeout) Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        double? timeout = null;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("timeout", out var t) && t.ValueKind == JsonValueKind.Number)
                timeout = t.GetDouble();
            if (!root.TryGetProperty("steps", out root))
                throw new FormatException("сценарий: нет массива steps");
        }
        if (root.ValueKind != JsonValueKind.Array)
            throw new FormatException("сценарий: ждал массив шагов");
        var steps = new List<SmokeStep>();
        foreach (var item in root.EnumerateArray())
        {
            var index = steps.Count;
            string line;
            double? stepTimeout = null;
            if (item.ValueKind == JsonValueKind.String)
                line = item.GetString()!;
            else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("do", out var verb) && verb.ValueKind == JsonValueKind.String)
            {
                line = verb.GetString()!;
                if (item.TryGetProperty("timeout", out var t) && t.ValueKind == JsonValueKind.Number)
                    stepTimeout = t.GetDouble();
            }
            else
                throw new FormatException($"шаг {index}: ждал {{ \"do\": \"…\" }}");
            line = line.Trim();
            var space = line.IndexOf(' ');
            var name = (space < 0 ? line : line[..space]).ToLowerInvariant();
            var rest = space < 0 ? "" : line[(space + 1)..].Trim();
            if (!Verbs.Contains(name))
                throw new FormatException($"шаг {index}: нет глагола «{name}» (есть: {string.Join(", ", Verbs)})");
            steps.Add(new SmokeStep(index, line, name, rest, stepTimeout, item.Clone()));
        }
        return (steps, timeout);
    }
}

/// <summary>
/// Выражения <c>assert</c> и чисел условий: «левое оп правое», оп — == != &gt;= &lt;= &gt; &lt;; сторона — сумма слагаемых через
/// + и −: число, <c>tick</c>, <c>entities</c>, <c>time</c> (секунды смоука) или поле отметки <c>имя.tick</c>,
/// <c>имя.entities</c>, <c>имя.time</c>.
/// </summary>
internal static partial class SmokeExpr
{
    [GeneratedRegex(@"(==|!=|>=|<=|>|<)")]
    private static partial Regex Operator();

    [GeneratedRegex(@"([+-]?)\s*([^\s+-]+)")]
    private static partial Regex Term();

    /// <summary>Сравнение; не разобрать — FormatException.</summary>
    public static bool Check(string text, Func<string, double?> resolve)
    {
        var match = Operator().Match(text);
        if (!match.Success)
            throw new FormatException($"нет сравнения в «{text}»");
        var left = Value(text[..match.Index], resolve);
        var right = Value(text[(match.Index + match.Length)..], resolve);
        return Compare(left, match.Value, right);
    }

    public static bool Compare(double left, string op, double right) => op switch
    {
        "==" => left == right,
        "!=" => left != right,
        ">=" => left >= right,
        "<=" => left <= right,
        ">" => left > right,
        "<" => left < right,
        _ => throw new FormatException($"нет сравнения «{op}»"),
    };

    /// <summary>Сумма слагаемых.</summary>
    public static double Value(string text, Func<string, double?> resolve)
    {
        var terms = Term().Matches(text);
        if (terms.Count == 0)
            throw new FormatException("пустое выражение");
        var sum = 0.0;
        foreach (Match term in terms)
        {
            var word = term.Groups[2].Value;
            var value = double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : resolve(word) ?? throw new FormatException($"не знаю «{word}»");
            sum += term.Groups[1].Value == "-" ? -value : value;
        }
        return sum;
    }
}
