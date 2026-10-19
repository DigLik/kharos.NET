using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kharos.I18n;

/// <summary>Кусок строки <see cref="Texts.Rich"/>: текст или то, что подставили вместо слова (значок груза).</summary>
public readonly record struct RichPart(string? Text, object? Icon)
{
    public static RichPart Of(string text) => new(text, null);
    public static RichPart Of(object icon) => new(null, icon);
    public bool IsText => Text is not null;
    public override string ToString() => Text ?? $"[{Icon}]";
}

/// <summary>
/// Словари языков (packages/i18n): строки по ключу через точку («menu.title»), в строке подстановки «{name}» и
/// «{name:lower}» (строчными; нужны ли они, решает язык: в немецком существительные остаются с заглавной).
/// Словарь языка — вложенный JSON, как в TS. Слои: ключи оболочки (lang/*.json в сборке), затем словари модов
/// (mods/&lt;мод&gt;/i18n/&lt;язык&gt;.json) в порядке загрузки — более поздний мод перекрывает ранний, так что
/// заплатка перевода — просто ещё один мод. Нет строки на языке — берётся английская, затем русская (исходная);
/// нет и её — сам ключ. После сборки словарь только читают. Без отражения: JSON читается через JsonDocument.
/// </summary>
public sealed partial class Texts
{
    private readonly Dictionary<string, Dictionary<string, string>> _langs = new(StringComparer.Ordinal);

    [GeneratedRegex(@"\{(\w+)(?::(\w+))?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"(?<=[<\[|])[a-z]+(?=[>\]|])", RegexOptions.IgnoreCase)]
    private static partial Regex CommandWord();

    /// <summary>Пустые словари.</summary>
    public Texts() { }

    /// <summary>Словари оболочки движка (src/Kharos.I18n/lang/*.json, ресурсы сборки).</summary>
    public static Texts Shell()
    {
        var texts = new Texts();
        var assembly = typeof(Texts).Assembly;
        foreach (var (lang, _) in Langs.All)
        {
            using var stream = assembly.GetManifestResourceStream($"Kharos.I18n.lang.{lang}.json");
            if (stream is null)
                continue;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            texts.Add(lang, memory.ToArray(), "оболочка");
        }
        return texts;
    }

    /// <summary>Языки, для которых есть хоть одна строка.</summary>
    public IEnumerable<string> Languages => _langs.Keys;

    /// <summary>Все ключи языка.</summary>
    public IReadOnlyCollection<string> Keys(string lang) => _langs.TryGetValue(lang, out var map) ? map.Keys : [];

    /// <summary>Строка языка как есть (без запасных языков) или null.</summary>
    public string? Raw(string lang, string key) => _langs.TryGetValue(lang, out var map) && map.TryGetValue(key, out var text) ? text : null;

    /// <summary>Есть ли строка (на языке или запасных).</summary>
    public bool Has(string lang, string key) => Find(lang, key) is not null;

    private string? Find(string lang, string key) => Raw(lang, key) ?? (lang == "en" ? null : Raw("en", key)) ?? (lang == "ru" ? null : Raw("ru", key));

    /// <summary>Вливает словарь языка (вложенный JSON): строки с теми же ключами перекрываются.</summary>
    public void Add(string lang, ReadOnlySpan<byte> json, string source = "словарь")
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException error)
        {
            throw new FormatException($"{source}, {lang}: {error.Message}", error);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException($"{source}, {lang}: словарь — объект");
            if (!_langs.TryGetValue(lang, out var map))
                _langs[lang] = map = new Dictionary<string, string>(StringComparer.Ordinal);
            Flatten(document.RootElement, "", map, $"{source}, {lang}");
        }
    }

    /// <summary>Словари мода: язык → байты. Зовут по порядку загрузки модов — поздний перекрывает.</summary>
    public void AddMod(string mod, IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        foreach (var (lang, bytes) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            Add(lang, bytes, $"мод {mod}");
    }

    /// <summary>Словари из папки i18n мода: каждый файл «язык.json».</summary>
    public void AddDirectory(string directory, string source)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var file in Directory.GetFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            Add(Path.GetFileNameWithoutExtension(file), File.ReadAllBytes(file), source);
    }

    private static void Flatten(JsonElement node, string prefix, Dictionary<string, string> into, string where)
    {
        foreach (var property in node.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    into[prefix + property.Name] = property.Value.GetString()!;
                    break;
                case JsonValueKind.Object:
                    Flatten(property.Value, $"{prefix}{property.Name}.", into, where);
                    break;
                default:
                    throw new FormatException($"{where}: {prefix}{property.Name} — строка или раздел");
            }
        }
    }

    // ------------------------------------------------------------------ строки

    /// <summary>Строчные буквы: то, что делает «{name:lower}».</summary>
    public static string Lower(string text) => text.ToLowerInvariant();

    private static string Show(object? value) => value switch
    {
        null => "",
        string text => text,
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static object? Modify(object? value, string modifier) => modifier == "lower" && value is string text ? Lower(text) : value;

    private static Dictionary<string, object> Map(ReadOnlySpan<(string Name, object Value)> args)
    {
        var map = new Dictionary<string, object>(args.Length, StringComparer.Ordinal);
        foreach (var (name, value) in args)
            map[name] = value;
        return map;
    }

    /// <summary>Строка с подстановками. Нет ключа — сам ключ; нет подстановки в args — «{name}» остаётся как есть.</summary>
    public string T(string lang, string key, params ReadOnlySpan<(string Name, object Value)> args)
    {
        var text = Find(lang, key);
        if (text is null)
            return key;
        return args.IsEmpty ? text : Fill(text, Map(args));
    }

    private static string Fill(string text, Dictionary<string, object> args) =>
        Placeholder().Replace(text, match => args.TryGetValue(match.Groups[1].Value, out var value)
            ? Show(match.Groups[2].Success ? Modify(value, match.Groups[2].Value) : value)
            : match.Value);

    /// <summary>
    /// Строка, в которую подставляется не только текст, но и значки: куски вперемешку с тем, что передано (не строка и не
    /// число — значок). Подстановки, которых нет в args, остаются в тексте.
    /// </summary>
    public List<RichPart> Rich(string lang, string key, params ReadOnlySpan<(string Name, object Value)> args)
    {
        var text = Find(lang, key) ?? key;
        var map = Map(args);
        var parts = new List<RichPart>();
        var last = 0;
        foreach (Match match in Placeholder().Matches(text))
        {
            if (!map.TryGetValue(match.Groups[1].Value, out var raw))
                continue;
            if (match.Index > last)
                parts.Add(RichPart.Of(text[last..match.Index]));
            var value = match.Groups[2].Success ? Modify(raw, match.Groups[2].Value) : raw;
            parts.Add(value is string or IFormattable ? RichPart.Of(Show(value)) : RichPart.Of(value!));
            last = match.Index + match.Length;
        }
        if (last < text.Length)
            parts.Add(RichPart.Of(text[last..]));
        return parts;
    }

    /// <summary>
    /// Строка по числу: форма выбирается правилами языка — ключ «key.one», «key.few», «key.many» или «key.other»
    /// (нет формы — «key.other», затем сам «key»). В подстановки добавляется «n».
    /// </summary>
    public string Plural(string lang, string key, long count, params ReadOnlySpan<(string Name, object Value)> args)
    {
        var map = Map(args);
        map["n"] = count;
        var category = I18n.Plural.Of(lang, count);
        var text = Find(lang, $"{key}.{I18n.Plural.Suffix(category)}") ?? Find(lang, $"{key}.other") ?? Find(lang, key);
        return text is null ? key : Fill(text, map);
    }

    // ------------------------------------------------------------------ сообщения хоста

    /// <summary>Аргументы команды чата на языке игрока: «&lt;player&gt;» — «&lt;игрок&gt;». Слова не из словаря остаются как есть.</summary>
    public string CommandLabel(string lang, string label) =>
        CommandWord().Replace(label, match => Find(lang, $"chat.args.{match.Value}") ?? match.Value);

    /// <summary>Что делает команда чата: из словаря по её имени, а у команды не из него — как назвал хост.</summary>
    public string CommandHelp(string lang, string name, string fallback) => Find(lang, $"chat.help.{name}") ?? fallback;

    /// <summary>
    /// Сообщение хоста на языке игрока: id — строка раздела host, pairs — подстановки парами «имя, значение». В строках
    /// о команде её аргументы и описание тоже на языке игрока.
    /// </summary>
    public string Host(string lang, string id, IReadOnlyList<string>? pairs = null)
    {
        if (Find(lang, $"host.{id}") is not { } text)
            return id;
        var args = new Dictionary<string, object>(StringComparer.Ordinal);
        for (var i = 0; pairs is not null && i + 1 < pairs.Count; i += 2)
            args[pairs[i]] = pairs[i + 1];
        if (args.TryGetValue("command", out var command))
            args["command"] = CommandLabel(lang, (string)command);
        if (args.TryGetValue("name", out var name) && args.TryGetValue("help", out var help))
            args["help"] = CommandHelp(lang, (string)name, (string)help);
        return Fill(text, args);
    }
}
