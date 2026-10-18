namespace Kharos.Protocol;

/// <summary>Вид сообщения: первый байт. 0x01–0x0f — хост → клиент, 0x10–0x1f — клиент → хост.</summary>
public enum MessageType : byte
{
    /// <summary>Хост → клиент: кто ты, какая партия, схема, слои, моды (<see cref="Protocol.Welcome"/>).</summary>
    Welcome = 0x01,
    /// <summary>Хост → клиент: видимый мир и слои заново (тело — как у дельты).</summary>
    Snapshot = 0x02,
    /// <summary>Хост → клиент: изменения с прошлого сообщения.</summary>
    Delta = 0x03,
    /// <summary>Хост → клиент: строки чата.</summary>
    Chat = 0x04,
    /// <summary>Хост → клиент: кусок пакета мода (<see cref="ContentChunk"/>).</summary>
    Chunk = 0x05,
    /// <summary>Хост → клиент: игроки партии.</summary>
    Players = 0x06,
    /// <summary>Хост → клиент: вход не принят или игрока выгнали; затем транспорт закрывается.</summary>
    Refused = 0x07,
    /// <summary>Хост → клиент: команды чата, доступные этому подключению.</summary>
    Commands = 0x08,
    /// <summary>Хост → клиент: карта мира байтами (Land.Pack), когда она правлена и генератором не повторить.</summary>
    Land = 0x09,

    /// <summary>Клиент → хост: приказ.</summary>
    Order = 0x10,
    /// <summary>Клиент → хост: что на экране (для очереди рассылки).</summary>
    View = 0x11,
    /// <summary>Клиент → хост: строка чата или команда («/…»).</summary>
    Say = 0x12,
    /// <summary>Клиент → хост: начать заново (событие player_respawn).</summary>
    Respawn = 0x13,
    /// <summary>Клиент → хост: прислать пакеты модов по хешам.</summary>
    Fetch = 0x14,
}

/// <summary>Вид строки чата.</summary>
public enum ChatKind : byte
{
    /// <summary>Игрок пишет всем.</summary>
    Player = 0,
    /// <summary>Сообщение хоста: о входе, ответ на команду, /say, /me.</summary>
    System = 1,
    /// <summary>Шёпот: /msg, видят двое.</summary>
    Whisper = 2,
}

/// <summary>Строка чата: от кого (0 — хост), подпись, текст; у шёпота — кому.</summary>
public sealed record ChatLine(uint Player, string Name, string Text, ChatKind Kind = ChatKind.Player, string? To = null);

/// <summary>Игрок партии: номер, ник и в сети ли.</summary>
public sealed record PlayerInfo(uint Player, string Name, bool Online);

/// <summary>Команда чата, какой её знает клиент: имя без «/», аргументы, описание; secret — аргумент — пароль.</summary>
public sealed record CommandInfo(string Name, IReadOnlyList<string> Args, string Help, bool Secret = false);

/// <summary>Кусок пакета мода: хеш пакета, сжатая длина целиком, смещение и байты.</summary>
public sealed record ContentChunk(byte[] Hash, int Total, int Offset, byte[] Data);

/// <summary>Экран клиента в тайлах.</summary>
public readonly record struct ViewBox(float Left, float Top, float Right, float Bottom)
{
    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
}

/// <summary>
/// Сообщения вне мира (чат, игроки, отказ, команды, доставка модов, экран) — кодирование и разбор.
/// <code>
/// 0x04 Chat     count:varint (player:varint name:str text:str kind:u8 [to:str, если kind = 2])×
/// 0x05 Chunk    hash:32 total:varint offset:varint data:blob   — data ≤ 64 КБ
/// 0x06 Players  count:varint (player:varint name:str flags:u8)×  — flags: 1 в сети
/// 0x07 Refused  reason:str flags:u8                             — flags: 1 нужен пароль
/// 0x08 Commands admin:u8 count:varint (name:str args:varint str× help:str flags:u8)×  — flags: 1 secret
/// 0x09 Land     packed:blob                                     — deflate-raw Land.Save
/// 0x11 View     left top right bottom:f32
/// 0x12 Say      text:str
/// 0x13 Respawn
/// 0x14 Fetch    count:varint hash:32×
/// </code>
/// </summary>
public static class Messages
{
    /// <summary>Длина хеша пакета мода (SHA-256).</summary>
    public const int HashSize = 32;

    /// <summary>Больше кусок пакета не бывает.</summary>
    public const int MaxChunk = 64 * 1024;

    /// <summary>Вид сообщения; пустое — <see cref="FormatException"/>.</summary>
    public static MessageType TypeOf(ReadOnlySpan<byte> message) => message.IsEmpty ? throw new FormatException("пустое сообщение") : (MessageType)message[0];

    public static byte[] Chat(IReadOnlyList<ChatLine> lines)
    {
        var w = new WireWriter(64).Byte((byte)MessageType.Chat).Varint((ulong)lines.Count);
        foreach (var line in lines)
        {
            w.Varint(line.Player).Text(line.Name).Text(line.Text).Byte((byte)line.Kind);
            if (line.Kind == ChatKind.Whisper)
                w.Text(line.To ?? "");
        }
        return w.ToArray();
    }

    public static List<ChatLine> ReadChat(ReadOnlySpan<byte> message)
    {
        var r = Open(message, MessageType.Chat);
        var lines = new List<ChatLine>();
        for (var count = r.Count(4); count > 0; count--)
        {
            var player = r.U32();
            var name = r.Text();
            var text = r.Text();
            var kind = r.Byte() is var k && k <= (byte)ChatKind.Whisper ? (ChatKind)k : throw new FormatException($"строка чата: вид {k}");
            lines.Add(new ChatLine(player, name, text, kind, kind == ChatKind.Whisper ? r.Text() : null));
        }
        return Close(ref r, lines);
    }

    public static byte[] Chunk(ContentChunk chunk)
    {
        if (chunk.Hash.Length != HashSize || chunk.Data.Length > MaxChunk)
            throw new ArgumentException("кусок пакета: хеш 32 байта, данные до 64 КБ");
        return new WireWriter(chunk.Data.Length + 48).Byte((byte)MessageType.Chunk).Raw(chunk.Hash).Varint((ulong)chunk.Total).Varint((ulong)chunk.Offset).Blob(chunk.Data).ToArray();
    }

    public static ContentChunk ReadChunk(ReadOnlySpan<byte> message)
    {
        var r = Open(message, MessageType.Chunk);
        var hash = r.Take(HashSize).ToArray();
        var total = r.Int();
        var offset = r.Int();
        var data = r.Blob().ToArray();
        if (data.Length > MaxChunk || (long)offset + data.Length > total)
            throw new FormatException("кусок пакета не на месте");
        return Close(ref r, new ContentChunk(hash, total, offset, data));
    }

    public static byte[] Players(IReadOnlyList<PlayerInfo> players)
    {
        var w = new WireWriter(64).Byte((byte)MessageType.Players).Varint((ulong)players.Count);
        foreach (var player in players)
            w.Varint(player.Player).Text(player.Name).Byte(player.Online ? (byte)1 : (byte)0);
        return w.ToArray();
    }

    public static List<PlayerInfo> ReadPlayers(ReadOnlySpan<byte> message)
    {
        var r = Open(message, MessageType.Players);
        var players = new List<PlayerInfo>();
        for (var count = r.Count(3); count > 0; count--)
            players.Add(new PlayerInfo(r.U32(), r.Text(), (r.Byte() & 1) != 0));
        return Close(ref r, players);
    }

    /// <summary>Отказ: почему; password — клиенту спросить пароль.</summary>
    public static byte[] Refused(string reason, bool password = false) =>
        new WireWriter(16 + reason.Length * 2).Byte((byte)MessageType.Refused).Text(reason).Byte(password ? (byte)1 : (byte)0).ToArray();

    public static (string Reason, bool Password) ReadRefused(ReadOnlySpan<byte> message)
    {
        var r = Open(message, MessageType.Refused);
        var reason = r.Text();
        var password = (r.Byte() & 1) != 0;
        return Close(ref r, (reason, password));
    }

    public static byte[] Commands(IReadOnlyList<CommandInfo> commands, bool admin)
    {
        var w = new WireWriter(256).Byte((byte)MessageType.Commands).Bool(admin).Varint((ulong)commands.Count);
        foreach (var command in commands)
        {
            w.Text(command.Name).Varint((ulong)command.Args.Count);
            foreach (var arg in command.Args)
                w.Text(arg);
            w.Text(command.Help).Byte(command.Secret ? (byte)1 : (byte)0);
        }
        return w.ToArray();
    }

    public static (List<CommandInfo> Commands, bool Admin) ReadCommands(ReadOnlySpan<byte> message)
    {
        var r = Open(message, MessageType.Commands);
        var admin = r.Bool();
        var commands = new List<CommandInfo>();
        for (var count = r.Count(4); count > 0; count--)
        {
            var name = r.Text();
            var args = new string[r.Count()];
            for (var at = 0; at < args.Length; at++)
                args[at] = r.Text();
            commands.Add(new CommandInfo(name, args, r.Text(), (r.Byte() & 1) != 0));
        }
        return Close(ref r, (commands, admin));
    }

    public static byte[] Land(ReadOnlySpan<byte> packed) => new WireWriter(packed.Length + 8).Byte((byte)MessageType.Land).Blob(packed).ToArray();

    public static byte[] ReadLand(ReadOnlySpan<byte> message)
    {
        var r = Open(message, MessageType.Land);
        var packed = r.Blob().ToArray();
        return Close(ref r, packed);
    }

    public static byte[] View(ViewBox box) =>
        new WireWriter(17).Byte((byte)MessageType.View).F32(box.Left).F32(box.Top).F32(box.Right).F32(box.Bottom).ToArray();

    public static ViewBox ReadView(ReadOnlySpan<byte> message)
    {
        var r = Open(message, MessageType.View);
        var box = new ViewBox(r.F32(), r.F32(), r.F32(), r.F32());
        if (!float.IsFinite(box.Left) || !float.IsFinite(box.Top) || !float.IsFinite(box.Right) || !float.IsFinite(box.Bottom))
            throw new FormatException("экран: не числа");
        return Close(ref r, box);
    }

    public static byte[] Say(string text) => new WireWriter(8 + text.Length * 2).Byte((byte)MessageType.Say).Text(text).ToArray();

    public static string ReadSay(ReadOnlySpan<byte> message)
    {
        var r = Open(message, MessageType.Say);
        var text = r.Text();
        return Close(ref r, text);
    }

    public static byte[] Respawn() => [(byte)MessageType.Respawn];

    public static byte[] Fetch(IReadOnlyList<byte[]> hashes)
    {
        var w = new WireWriter(8 + hashes.Count * HashSize).Byte((byte)MessageType.Fetch).Varint((ulong)hashes.Count);
        foreach (var hash in hashes)
            w.Raw(hash.Length == HashSize ? hash : throw new ArgumentException("хеш пакета — 32 байта"));
        return w.ToArray();
    }

    public static List<byte[]> ReadFetch(ReadOnlySpan<byte> message)
    {
        var r = Open(message, MessageType.Fetch);
        var hashes = new List<byte[]>();
        for (var count = r.Count(HashSize); count > 0; count--)
            hashes.Add(r.Take(HashSize).ToArray());
        return Close(ref r, hashes);
    }

    private static WireReader Open(ReadOnlySpan<byte> message, MessageType type)
    {
        var r = new WireReader(message);
        if (r.Byte() != (byte)type)
            throw new FormatException($"не {type}");
        return r;
    }

    private static T Close<T>(ref WireReader r, T value) => r.End ? value : throw new FormatException("лишние байты в сообщении");
}
