using System.Security.Cryptography;
using System.Text;
using Kharos.Protocol;

namespace Kharos.Host;

/// <summary>Игрок, каким его знает хост: номер, токен (по нему узнаётся при входе) и ник.</summary>
public sealed class PlayerRecord(uint id, string token, string name)
{
    public uint Id { get; } = id;
    public string Token { get; internal set; } = token;
    public string Name { get; internal set; } = name;
}

/// <summary>
/// Игроки хоста (host.ts: players, names): номер выдаёт хост, токен узнаёт вернувшегося — и после перезапуска сервера,
/// он в сохранении. Незнакомый токен годного вида — новый игрок с этим токеном (клиент мог завести его сам); пустой
/// или негодный — новый игрок с новым токеном. Токен клиенту — в приветствии.
/// </summary>
public sealed class PlayerRoster
{
    /// <summary>Длина ника и строки чата (CHAT_LENGTH, NAME_LENGTH в protocol.ts).</summary>
    public const int MaxName = 24, MaxChat = 200;

    /// <summary>Токен клиента принимается такой: 8–64 знаков [0-9A-Za-z_-].</summary>
    public const int MinToken = 8, MaxToken = 64;

    private readonly Dictionary<string, PlayerRecord> _byToken = new(StringComparer.Ordinal);
    private readonly SortedDictionary<uint, PlayerRecord> _byId = [];

    /// <summary>Ник игрока без своего: «{0}» — номер.</summary>
    public string DefaultName { get; set; } = "Игрок {0}";

    /// <summary>Номер, который получит следующий новый игрок.</summary>
    public uint Next { get; private set; } = 1;

    public IEnumerable<PlayerRecord> All => _byId.Values;

    public int Count => _byId.Count;

    public PlayerRecord? Get(uint player) => _byId.GetValueOrDefault(player);

    /// <summary>Знает ли хост игрока с этим токеном.</summary>
    public bool Knows(string? token) => token is not null && _byToken.ContainsKey(token);

    /// <summary>Игрок по токену; незнакомый — null.</summary>
    public uint? Find(string? token) => token is not null && _byToken.TryGetValue(token, out var record) ? record.Id : null;

    /// <summary>
    /// Кто входит: знакомый токен — прежний игрок, иначе новый. Ник (очищенный) заменяет прежний, пустой — оставляет.
    /// </summary>
    public (PlayerRecord Record, bool New) Resolve(string? token, string? name)
    {
        var nick = CleanName(name ?? "");
        if (token is not null && _byToken.TryGetValue(token, out var known))
        {
            if (nick.Length > 0)
                known.Name = nick;
            return (known, false);
        }
        var id = Next;
        var record = new PlayerRecord(id, ValidToken(token) ? token! : NewToken(), nick.Length > 0 ? nick : string.Format(DefaultName, id));
        Add(record);
        return (record, true);
    }

    /// <summary>Игрок с заданным номером (одиночная игра, вход по номеру); нет — заводится с новым токеном.</summary>
    public PlayerRecord Ensure(uint player)
    {
        if (_byId.TryGetValue(player, out var record))
            return record;
        record = new PlayerRecord(player, NewToken(), string.Format(DefaultName, player));
        Add(record);
        return record;
    }

    public string NameOf(uint player) => _byId.TryGetValue(player, out var record) ? record.Name : string.Format(DefaultName, player);

    /// <summary>Игрок по номеру или нику без учёта регистра; нет — null.</summary>
    public uint? FindPlayer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (uint.TryParse(text, out var number) && _byId.ContainsKey(number))
            return number;
        foreach (var record in _byId.Values)
            if (string.Equals(record.Name, text, StringComparison.OrdinalIgnoreCase))
                return record.Id;
        return null;
    }

    /// <summary>Ник: без управляющих знаков и лишних пробелов, не длиннее <see cref="MaxName"/>.</summary>
    public static string CleanName(string name) => Clean(name, MaxName);

    /// <summary>Строка чата: так же, не длиннее <see cref="MaxChat"/>.</summary>
    public static string CleanChat(string text) => Clean(text, MaxChat);

    private static string Clean(string text, int most)
    {
        var builder = new StringBuilder(Math.Min(text.Length, most * 2));
        var space = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
                continue;
            if (Rune.IsWhiteSpace(rune))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space)
                builder.Append(' ');
            space = false;
            builder.Append(rune.ToString());
        }
        var clean = builder.ToString();
        if (clean.Length <= most)
            return clean;
        // Не резать суррогатную пару пополам.
        var cut = char.IsHighSurrogate(clean[most - 1]) ? most - 1 : most;
        return clean[..cut].TrimEnd();
    }

    private static bool ValidToken(string? token) =>
        token is { Length: >= MinToken and <= MaxToken } && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-' or '_');

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private void Add(PlayerRecord record)
    {
        _byId[record.Id] = record;
        _byToken[record.Token] = record;
        if (record.Id >= Next)
            Next = record.Id + 1;
    }

    // Сохранение: next:varint count:varint (id:varint token:str name:str)×

    internal void Write(WireWriter w)
    {
        w.Varint(Next).Varint((ulong)_byId.Count);
        foreach (var record in _byId.Values)
            w.Varint(record.Id).Text(record.Token).Text(record.Name);
    }

    internal void Read(ref WireReader r)
    {
        _byId.Clear();
        _byToken.Clear();
        var next = r.U32();
        for (var count = r.Count(3); count > 0; count--)
        {
            var id = r.U32();
            var token = r.Text();
            var name = r.Text();
            if (id == 0 || _byId.ContainsKey(id) || _byToken.ContainsKey(token))
                throw new FormatException($"игрок {id} дважды");
            Add(new PlayerRecord(id, token, name));
        }
        Next = Math.Max(Next, next);
    }
}
