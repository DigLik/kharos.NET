using System.Buffers.Binary;
using System.Text;
using Kharos.Protocol;

namespace Kharos.Host;

/// <summary>
/// Провод команд чата между хостом и модом (Н6-7). Байты без MemoryPack: хост его не знает, а мод читает их своей
/// копией (Base.Shared/Orders/ChatCall.cs). Все числа — little-endian.
/// <code>
/// ChatCall (payload приказа command.order):  words:u16 { player:u32 len:u16 utf8 }×words  rest:u16 utf8
///   player — номер игрока, найденного по слову (виды player и player|all), иначе 0; rest — текст после имени как есть.
/// Text (HostText):                            count:u16 { len:u16 utf8 }×count — ключ, затем пары «имя, значение»
/// chat.reply (сигнал, SignalTo автору):       Text — ответ автору команды
/// host.announce (сигнал всем):                player:u32 flags:u8 (1 — не для локальной игры) Text — весть «ник текст»
/// </code>
/// </summary>
public static class ChatWire
{
    /// <summary>Сигнал мода: ответ автору команды (To — автор, payload — Text).</summary>
    public const string ReplySignal = "chat.reply";

    /// <summary>Сигнал мода: весть всем об игроке (поражение, ядерный пуск).</summary>
    public const string AnnounceSignal = "host.announce";

    /// <summary>Вести, которые локальной игре не нужны (игрок один).</summary>
    public const byte RemoteOnly = 1;

    public static byte[] Call(IReadOnlyList<(string Word, uint Player)> words, string rest)
    {
        using var stream = new MemoryStream();
        Put(stream, (ushort)words.Count);
        foreach (var (word, player) in words)
        {
            Put(stream, player);
            Str(stream, word);
        }
        Str(stream, rest);
        return stream.ToArray();
    }

    public static byte[] Text(HostText text)
    {
        using var stream = new MemoryStream();
        Put(stream, (ushort)(1 + text.Args.Count));
        Str(stream, text.Key);
        foreach (var arg in text.Args)
            Str(stream, arg);
        return stream.ToArray();
    }

    /// <summary>Разбор Text; битые байты — null.</summary>
    public static HostText? ReadText(ReadOnlySpan<byte> bytes)
    {
        var at = 0;
        if (!U16(bytes, ref at, out var count) || count == 0)
            return null;
        var items = new List<string>(count);
        for (var i = 0; i < count; i++)
            if (Read(bytes, ref at) is { } item)
                items.Add(item);
            else
                return null;
        return new HostText(items[0], items.Skip(1).ToList());
    }

    /// <summary>Разбор сигнала host.announce: игрок, флаги, текст.</summary>
    public static (uint Player, byte Flags, HostText Text)? ReadAnnounce(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 5 || ReadText(bytes[5..]) is not { } text)
            return null;
        return (BinaryPrimitives.ReadUInt32LittleEndian(bytes), bytes[4], text);
    }

    /// <summary>Разбор ChatCall (тесты).</summary>
    public static (List<(string Word, uint Player)> Words, string Remainder)? ReadCall(ReadOnlySpan<byte> bytes)
    {
        var at = 0;
        if (!U16(bytes, ref at, out var count))
            return null;
        var words = new List<(string, uint)>();
        for (var i = 0; i < count; i++)
        {
            if (at + 4 > bytes.Length)
                return null;
            var player = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
            at += 4;
            if (Read(bytes, ref at) is not { } word)
                return null;
            words.Add((word, player));
        }
        return Read(bytes, ref at) is { } rest ? (words, rest) : null;
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

    private static string? Read(ReadOnlySpan<byte> bytes, ref int at)
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

    private static void Put(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void Str(Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > ushort.MaxValue)
            bytes = bytes[..ushort.MaxValue];
        Put(stream, (ushort)bytes.Length);
        stream.Write(bytes);
    }
}
