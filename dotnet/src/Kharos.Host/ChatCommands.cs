using System.Text.RegularExpressions;
using Kharos.Protocol;

namespace Kharos.Host;

/// <summary>
/// Кто зовёт команду: игрок из чата (подключение) или консоль сервера (Player — null). Ответ идёт ключом словаря и
/// подстановками (<see cref="HostText"/>) — игрок читает его на своём языке; консоли ответ переводится сразу.
/// </summary>
public sealed class Caller(uint? player, bool admin, Action<HostText> reply, Connection? connection = null)
{
    private static readonly Kharos.I18n.Texts Shell = Kharos.I18n.Texts.Shell();

    /// <summary>Консоль (тесты, сервер): ответ готовым текстом на языке lang.</summary>
    public Caller(uint? player, bool admin, Action<string> reply, Connection? connection = null, string lang = "ru")
        : this(player, admin, text => reply(Shell.Host(lang, text.Key, text.Args)), connection) { }

    public uint? Player { get; } = player;
    public bool Admin { get; } = admin;
    public Connection? Connection { get; } = connection;

    /// <summary>Ответ только ему: строка key раздела host словаря с подстановками.</summary>
    public void Reply(string key, params (string Name, object Value)[] args) => reply(HostText.Of(key, args));

    public void Reply(HostText text) => reply(text);
}

/// <summary>Разобранная команда: слова после имени и Rest(n) — текст после n слов как есть.</summary>
public sealed class CommandCall(string name, IReadOnlyList<string> args, Func<int, string> rest, Caller caller)
{
    public string Name { get; } = name;
    public IReadOnlyList<string> Args { get; } = args;
    public Caller Caller { get; } = caller;

    public string Arg(int index) => index < Args.Count ? Args[index] : "";

    public string Rest(int skip) => rest(skip);
}

/// <summary>
/// Команда хоста (host.ts: HostCommand). Admin — только администратору (консоль им считается всегда); Game — только
/// из игры; Console — только из консоли, клиенту не видна; Secret — аргумент — пароль.
/// </summary>
public sealed class HostCommand
{
    public required string Name { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public string Help { get; init; } = "";
    public bool Admin { get; init; }
    public bool Game { get; init; }
    public bool Console { get; init; }
    public bool Secret { get; init; }
    public required Action<CommandCall> Run { get; init; }

    public string Usage => $"/{string.Join(' ', [Name, .. Args])}";

    public CommandInfo Info => new(Name, Args, Help, Secret);
}

/// <summary>Разобранная строка: имя команды (в нижнем регистре), слова и Rest(n) — текст после n слов.</summary>
public sealed record ParsedCommand(string Name, List<string> Args, Func<int, string> Rest);

/// <summary>Разбор строки чата (chatCommands.ts): «/имя аргументы», слова в двойных кавычках — вместе с пробелами.</summary>
public static partial class ChatCommands
{
    /// <summary>Аргумент, который клиент дописывает ником.</summary>
    public const string PlayerArg = "<player>";

    [GeneratedRegex("\"([^\"]*)\"?|(\\S+)")]
    private static partial Regex Words();

    /// <summary>Слова строки: через пробел, а в двойных кавычках — вместе.</summary>
    public static List<string> SplitWords(string text) =>
        [.. Words().Matches(text).Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)];

    /// <summary>Ник как аргумент: с пробелом — в кавычках.</summary>
    public static string QuoteName(string name) => name.Contains(' ') ? $"\"{name}\"" : name;

    /// <summary>Команда, её аргументы и rest; не команда (нет «/») — null.</summary>
    public static ParsedCommand? Parse(string text)
    {
        if (!text.StartsWith('/'))
            return null;
        var body = text[1..];
        var words = SplitWords(body);
        var name = words.Count > 0 ? words[0].ToLowerInvariant() : "";
        string Rest(int skip)
        {
            var match = Regex.Match(body, $"^\\s*(?:(?:\"[^\"]*\"?|\\S+)\\s+){{{skip + 1}}}");
            return match.Success ? body[match.Length..].Trim() : "";
        }
        return new ParsedCommand(name, words.Count > 1 ? words[1..] : [], Rest);
    }
}
