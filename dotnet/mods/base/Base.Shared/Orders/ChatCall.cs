using System.Buffers.Binary;
using System.Text;

namespace Base.Shared.Orders;

/// <summary>
/// Команда чата, какой её видит мод (Н6-7): payload приказа, объявленного в mod.json (commands[].order), — слова после
/// имени команды, номера игроков, найденных хостом по словам (0 — слово не ник), и остаток строки как есть. Байты
/// без MemoryPack — хост пишет их сам (Kharos.Host/ChatWire.cs, там же схема). Ответ автору — сигнал chat.reply
/// (<see cref="Reply"/>), весть всем об игроке — host.announce (<see cref="Announce"/>); текст — ключ раздела host словаря
/// и подстановки парами «имя, значение», как HostText.
/// </summary>
public sealed class ChatCall
{
    public const string ReplySignal = "chat.reply";
    public const string AnnounceSignal = "host.announce";

    /// <summary>Весть не для локальной игры (игрок один).</summary>
    public const byte RemoteOnly = 1;

    public List<string> Words { get; } = [];

    /// <summary>Номер игрока по слову (параллельно <see cref="Words"/>); 0 — слово не ник.</summary>
    public List<uint> Players { get; } = [];

    public string Rest { get; private set; } = "";

    public string Word(int index) => index < Words.Count ? Words[index] : "";

    public uint Player(int index) => index < Players.Count ? Players[index] : 0;

    /// <summary>Разбор payload; битые байты — null.</summary>
    public static ChatCall? Read(ReadOnlySpan<byte> bytes)
    {
        var call = new ChatCall();
        var at = 0;
        if (!U16(bytes, ref at, out var count))
            return null;
        for (var i = 0; i < count; i++)
        {
            if (at + 4 > bytes.Length)
                return null;
            call.Players.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]));
            at += 4;
            if (Str(bytes, ref at) is not { } word)
                return null;
            call.Words.Add(word);
        }
        if (Str(bytes, ref at) is not { } rest)
            return null;
        call.Rest = rest;
        return call;
    }

    /// <summary>Сообщение хоста: ключ и подстановки парами «имя, значение».</summary>
    public static byte[] Text(string key, params (string Name, object Value)[] args)
    {
        using var stream = new MemoryStream();
        var items = new List<string> { key };
        foreach (var (name, value) in args)
        {
            items.Add(name);
            items.Add(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
        }
        Put(stream, (ushort)items.Count);
        foreach (var item in items)
            Put(stream, item);
        return stream.ToArray();
    }

    /// <summary>Payload сигнала host.announce: игрок, флаги, сообщение.</summary>
    public static byte[] Announce(uint player, byte flags, string key, params (string Name, object Value)[] args)
    {
        var text = Text(key, args);
        var bytes = new byte[5 + text.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, player);
        bytes[4] = flags;
        text.CopyTo(bytes, 5);
        return bytes;
    }

    private static bool U16(ReadOnlySpan<byte> bytes, ref int at, out ushort value)
    {
        value = 0;
        if (at + 2 > bytes.Length)
            return false;
        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
        at += 2;
        return true;
    }

    private static string? Str(ReadOnlySpan<byte> bytes, ref int at)
    {
        if (!U16(bytes, ref at, out var length) || at + length > bytes.Length)
            return null;
        var text = Encoding.UTF8.GetString(bytes.Slice(at, length));
        at += length;
        return text;
    }

    private static void Put(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void Put(Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > ushort.MaxValue)
            bytes = bytes[..ushort.MaxValue];
        Put(stream, (ushort)bytes.Length);
        stream.Write(bytes);
    }
}
