using System.Text.RegularExpressions;
using Kharos.I18n;
using Kharos.Protocol;

namespace Kharos.Client.Shell;

/// <summary>Подсказка дополнения: что подставить в строку целиком и что показать.</summary>
public readonly record struct Completion(string Text, string Label, string Help);

/// <summary>
/// Дополнение команд чата (chatCommands.ts): пока набирается имя команды — подходящие по началу команды, дальше —
/// варианты аргумента (choices: виды юнитов) или ники для «&lt;player&gt;». Ник с пробелом дописывается в кавычках.
/// Подписи и описания — на языке игрока (<see cref="Texts.CommandLabel"/>, <see cref="Texts.CommandHelp"/>).
/// </summary>
public static partial class ChatCompletion
{
    /// <summary>Аргумент, который дописывается ником игрока.</summary>
    public const string PlayerArg = "<player>";

    [GeneratedRegex("\"[^\"]*\"?|\\S+")]
    private static partial Regex Word();

    [GeneratedRegex("\"([^\"]*)\"?|(\\S+)")]
    private static partial Regex Split();

    /// <summary>Слова строки: через пробел, а в двойных кавычках — вместе с пробелами, как ник «Игрок 3».</summary>
    public static List<string> SplitWords(string text) =>
        [.. Split().Matches(text).Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)];

    /// <summary>Ник как аргумент команды: с пробелом — в кавычках.</summary>
    public static string QuoteName(string name) => name.Contains(' ') ? $"\"{name}\"" : name;

    /// <summary>Дополнения для набранного draft.</summary>
    public static List<Completion> Complete(string draft, IReadOnlyList<CommandInfo> commands, IReadOnlyList<string> names, Texts? texts = null, string lang = Langs.Default)
    {
        if (!draft.StartsWith('/'))
            return [];
        string Label(CommandInfo command) => Localize(texts, lang, string.Join(' ', [$"/{command.Name}", .. command.Args]));
        string Help(CommandInfo command) => texts?.CommandHelp(lang, command.Name, command.Help) ?? command.Help;
        // Последнее слово — то, что дописывается; открытая кавычка значит, что оно ещё набирается.
        var words = Word().Matches(draft[1..]).Select(match => match.Value).ToList();
        if (draft.EndsWith(' ') && draft.Count(c => c == '"') % 2 == 0)
            words.Add("");
        if (words.Count == 0)
            words.Add("");
        if (words.Count == 1)
        {
            var typed = words[0].ToLowerInvariant();
            return [.. commands.Where(command => command.Name.StartsWith(typed, StringComparison.Ordinal))
                .Select(command => new Completion($"/{command.Name} ", Label(command), Help(command)))];
        }
        var found = commands.FirstOrDefault(item => item.Name == words[0].ToLowerInvariant());
        if (found is null)
            return [];
        var index = words.Count - 2;
        var arg = index < found.Args.Count ? found.Args[index] : null;
        var last = words[^1];
        var head = draft[..(draft.Length - last.Length)];
        var prefix = last.Replace("\"", "").ToLowerInvariant();
        if (found.Choices is { } choices && index < choices.Count && choices[index] is { } options)
            return [.. options.Where(choice => choice.ToLowerInvariant().StartsWith(prefix, StringComparison.Ordinal))
                .Select(choice => new Completion($"{head}{choice} ", choice, Localize(texts, lang, arg ?? "")))];
        if (arg != PlayerArg)
            return arg is not null ? [new Completion(draft, Label(found), Help(found))] : [];
        return [.. names.Where(name => name.ToLowerInvariant().StartsWith(prefix, StringComparison.Ordinal))
            .Select(name => new Completion($"{head}{QuoteName(name)} ", name, Help(found)))];
    }

    private static string Localize(Texts? texts, string lang, string label) => texts?.CommandLabel(lang, label) ?? label;
}
