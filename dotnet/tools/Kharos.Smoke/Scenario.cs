using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kharos.Smoke;

/// <summary>
/// Сценарий оркестратора (scenarios/*.json): <c>{ "about", "mods": ["smoke"], "settings": {…}, "steps": [ { "do": … } ] }</c>.
/// vars — свои подстановки; mods — моды смоука из папки mods рядом с программой (серверу — через --mods, одиночной игре — в KHAROS_MODS);
/// settings — поверх settings.json сервера. Шаги — <see cref="Verbs"/>; поля шага — в <see cref="ScenarioStep"/>.
/// <para>
/// <c>server</c> — поднять сервер (settings.json со свободным портом, admin «smoke», свой save; ждёт «слушает»);
/// <c>client</c> — запустить kharos: name (ник и KHAROS_HOME home-&lt;name&gt;), script (массив шагов KHAROS_SMOKE или имя
/// файла), as (метка отчёта, по умолчанию name), env, connect (KHAROS_SERVER, по умолчанию — если сервер идёт), wait
/// (ждать выхода, по умолчанию true), timeout, code (ожидаемый код, 0); <c>wait</c> — дождаться клиента (name или as);
/// <c>kill</c> — убить клиента; <c>console</c> — строка в stdin сервера (line) и, если есть expect, ждать строку журнала
/// (as — метка групп regex); <c>expect</c> — ждать строку журнала сервера (pattern, as, timeout; с name — вывода клиента); <c>stop</c> — stop в
/// stdin, ждать выхода с кодом code (0); <c>file</c> — файл есть и не пуст (path); <c>assert</c> — выражение (expr);
/// <c>record</c> — запись ролика KHAROS_RECORD (frames кадров PNG, WAV) и проверка числа кадров и длины WAV;
/// <c>soak</c> — боты нагрузки на время --soak (<see cref="Soak"/>).
/// </para>
/// <para>
/// Подстановки <c>${…}</c> в строках шагов и в сценариях клиентов: run (папка прогона), port, url, server (папка
/// сервера), home.&lt;имя&gt;, поля отчётов клиентов по метке (<c>${b1.marks.before.entities}</c>, <c>${a.tick}</c>) и группы
/// regex (<c>${status.tick}</c>).
/// </para>
/// </summary>
internal sealed record Scenario(string Name, string About, string Dir, IReadOnlyList<string> Mods, JsonObject? Settings, IReadOnlyList<ScenarioStep> Steps)
{
    /// <summary>Свои подстановки сценария (поле vars: имя → строка), например готовые payload приказов.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();

    public static readonly string[] Verbs = ["server", "client", "wait", "kill", "console", "expect", "stop", "file", "assert", "record", "soak"];

    public static Scenario Load(string path)
    {
        var full = Path.GetFullPath(path);
        return Parse(File.ReadAllText(full), Path.GetFileNameWithoutExtension(full), Path.GetDirectoryName(full)!);
    }

    public static Scenario Parse(string json, string name, string dir)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new FormatException($"{name}: ждал объект сценария");
        var mods = root["mods"] is JsonArray list ? list.Select(m => m?.GetValue<string>() ?? throw new FormatException($"{name}: mods — список имён")).ToList() : [];
        if (root["steps"] is not JsonArray steps)
            throw new FormatException($"{name}: нет массива steps");
        var parsed = new List<ScenarioStep>();
        foreach (var node in steps)
        {
            if (node is not JsonObject step || step["do"]?.GetValueKind() != JsonValueKind.String)
                throw new FormatException($"{name}: шаг {parsed.Count}: ждал {{ \"do\": \"…\" }}");
            var verb = step["do"]!.GetValue<string>();
            if (!Verbs.Contains(verb))
                throw new FormatException($"{name}: шаг {parsed.Count}: нет действия «{verb}» (есть: {string.Join(", ", Verbs)})");
            var parsedStep = new ScenarioStep(parsed.Count, verb, step);
            parsedStep.Validate(name);
            parsed.Add(parsedStep);
        }
        var values = root["vars"] is JsonObject vars ? vars.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? "") : [];
        return new Scenario(name, root["about"]?.GetValue<string>() ?? "", dir, mods, root["settings"] as JsonObject, parsed) { Values = values };
    }

    /// <summary>Сценарий по имени (single, server, soak…) из папки scenarios рядом с программой или путь к файлу.</summary>
    public static string Find(string nameOrPath)
    {
        if (File.Exists(nameOrPath))
            return Path.GetFullPath(nameOrPath);
        var path = Path.Combine(AppContext.BaseDirectory, "scenarios", nameOrPath + ".json");
        return File.Exists(path) ? path : throw new FileNotFoundException($"нет сценария «{nameOrPath}» ({path})");
    }
}

/// <summary>Шаг сценария оркестратора: действие и поля объекта.</summary>
internal sealed record ScenarioStep(int Index, string Do, JsonObject Raw)
{
    public string? Str(string name) => Raw[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public double? Num(string name) => Raw[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<double>() : null;

    public bool? Bool(string name) => Raw[name] is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>() : null;

    public string Need(string name) => Str(name) ?? throw new FormatException($"шаг {Index} ({Do}): нет поля {name}");

    /// <summary>Подпись шага в сводке.</summary>
    public string Title => Do switch
    {
        "client" => $"client {Str("as") ?? Str("name")}",
        "wait" or "kill" => $"{Do} {Str("name")}",
        "console" => $"console «{Str("line")}»",
        "expect" => $"expect {Str("as") ?? Str("pattern")}",
        "file" => $"file {Str("path")}",
        "assert" => $"assert {Str("expr")}",
        "record" => $"record {Num("frames") ?? 90} кадров",
        _ => Do,
    };

    internal void Validate(string scenario)
    {
        string[] need = Do switch
        {
            "client" => ["name"],
            "wait" or "kill" => ["name"],
            "console" => ["line"],
            "expect" => ["pattern"],
            "file" => ["path"],
            "assert" => ["expr"],
            _ => [],
        };
        foreach (var field in need)
            if (Str(field) is null)
                throw new FormatException($"{scenario}: шаг {Index} ({Do}): нет поля {field}");
        if (Do == "client" && Raw["script"] is not (JsonArray or JsonObject) && Str("script") is null)
            throw new FormatException($"{scenario}: шаг {Index} (client): нет script — массива шагов или имени файла");
        foreach (var pattern in new[] { Str("pattern"), Str("expect") })
            if (pattern is not null)
                _ = new Regex(pattern);
    }
}

/// <summary>Подстановки <c>${имя}</c>; неизвестное имя — ошибка (опечатка в сценарии не должна сойти за пустое).</summary>
internal sealed partial class Vars
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex Reference();

    public string this[string name]
    {
        get => _values.TryGetValue(name, out var value) ? value : throw new KeyNotFoundException($"нет подстановки ${{{name}}}");
        set => _values[name] = value;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string Expand(string text) => Reference().Replace(text, match => this[match.Groups[1].Value]);

    /// <summary>JSON-узел с подстановками во всех строках.</summary>
    public JsonNode? Expand(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Expand(p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Expand)]),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => JsonValue.Create(Expand(value.GetValue<string>())),
        _ => node?.DeepClone(),
    };

    /// <summary>Поля JSON-отчёта под меткой: числа, строки и логические — плоско через точку.</summary>
    public void AddReport(string label, JsonNode? node)
    {
        void Walk(string prefix, JsonNode? item)
        {
            switch (item)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj)
                        Walk($"{prefix}.{key}", value);
                    break;
                case JsonArray:
                    break;
                case JsonValue value:
                    _values[prefix] = value.GetValueKind() switch
                    {
                        JsonValueKind.Number => value.GetValue<double>().ToString(CultureInfo.InvariantCulture),
                        JsonValueKind.True => "1",
                        JsonValueKind.False => "0",
                        _ => value.ToString(),
                    };
                    break;
            }
        }
        Walk(label, node);
    }

    /// <summary>Именованные группы regex под меткой.</summary>
    public void AddGroups(string label, Match match, Regex regex)
    {
        foreach (var name in regex.GetGroupNames())
            if (!int.TryParse(name, out _) && match.Groups[name].Success)
                _values[$"{label}.{name}"] = match.Groups[name].Value;
    }
}

/// <summary>Выражение assert: «левое оп правое», стороны — суммы чисел через + и −; оп — == != &gt;= &lt;= &gt; &lt;.</summary>
internal static partial class Expr
{
    [GeneratedRegex(@"(==|!=|>=|<=|>|<)")]
    private static partial Regex Operator();

    [GeneratedRegex(@"([+-]?)\s*([0-9.eE]+)")]
    private static partial Regex Term();

    public static bool Check(string text)
    {
        var match = Operator().Match(text);
        if (!match.Success)
            throw new FormatException($"нет сравнения в «{text}»");
        var left = Sum(text[..match.Index]);
        var right = Sum(text[(match.Index + match.Length)..]);
        return match.Value switch
        {
            "==" => left == right,
            "!=" => left != right,
            ">=" => left >= right,
            "<=" => left <= right,
            ">" => left > right,
            _ => left < right,
        };
    }

    public static double Sum(string text)
    {
        var rest = Term().Replace(text, "").Trim();
        if (rest.Length > 0)
            throw new FormatException($"не число: «{text.Trim()}»");
        var terms = Term().Matches(text);
        if (terms.Count == 0)
            throw new FormatException("пустое выражение");
        var sum = 0.0;
        foreach (Match term in terms)
        {
            var value = double.Parse(term.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            sum += term.Groups[1].Value == "-" ? -value : value;
        }
        return sum;
    }
}

/// <summary>Длительность: «15m», «90s», «2h», «1.5m», просто число — секунды.</summary>
internal static class Duration
{
    public static TimeSpan Parse(string text)
    {
        text = text.Trim().ToLowerInvariant();
        var (number, unit) = text[^1] switch
        {
            's' => (text[..^1], 1.0),
            'm' => (text[..^1], 60.0),
            'h' => (text[..^1], 3600.0),
            _ => (text, 1.0),
        };
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? TimeSpan.FromSeconds(value * unit)
            : throw new FormatException($"не длительность: «{text}» (ждал 15m, 90s, 2h)");
    }
}
