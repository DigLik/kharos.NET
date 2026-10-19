using Kharos.Engine.Terrain;

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
    /// <summary>Хост → клиент: правки карты (тайлы хранимыми байтами) — что игрок узнал или что правили, без тумана.</summary>
    LandEdits = 0x0A,

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

/// <summary>
/// Строка чата: от кого (0 — хост), подпись, текст; у шёпота — кому. Args не null — это сообщение хоста: Text — ключ
/// раздела host словаря («joins»), Args — подстановки парами «имя, значение»; клиент пишет его на своём языке (Н6-5).
/// </summary>
public sealed record ChatLine(uint Player, string Name, string Text, ChatKind Kind = ChatKind.Player, string? To = null, IReadOnlyList<string>? Args = null)
{
    /// <summary>Ключ и подстановки сообщения хоста; готовый текст человека — null.</summary>
    public HostText? Host => Args is null ? null : new HostText(Text, Args);
}

/// <summary>
/// Сообщение хоста ключом: id строки раздела host словаря и подстановки парами «имя, значение» (имя без скобок).
/// Клиент берёт строку на своём языке, так что хост не знает, на каком читают.
/// </summary>
public sealed record HostText(string Key, IReadOnlyList<string> Args)
{
    /// <summary>Сообщение с подстановками: числа пишутся инвариантно.</summary>
    public static HostText Of(string key, params (string Name, object Value)[] args)
    {
        var list = new string[args.Length * 2];
        for (var i = 0; i < args.Length; i++)
            (list[i * 2], list[i * 2 + 1]) = (args[i].Name, Convert.ToString(args[i].Value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
        return new HostText(key, list);
    }

    /// <summary>Подстановки по именам.</summary>
    public Dictionary<string, string> Named()
    {
        var map = new Dictionary<string, string>(Args.Count / 2, StringComparer.Ordinal);
        for (var i = 0; i + 1 < Args.Count; i += 2)
            map[Args[i]] = Args[i + 1];
        return map;
    }
}

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
/// 0x04 Chat     count:varint (player:varint name:str text:str kind:u8 [to:str, если kind = 2]
///                                  [args:varint str×, если kind &amp; 0x80 — text это ключ host.*, args — пары имя, значение])×
/// 0x05 Chunk    hash:32 total:varint offset:varint data:blob   — data ≤ 64 КБ
/// 0x06 Players  count:varint (player:varint name:str flags:u8)×  — flags: 1 в сети
/// 0x07 Refused  reason:str flags:u8 [args:varint str×]          — flags: 1 нужен пароль, 2 reason — ключ host.* с подстановками
/// 0x08 Commands admin:u8 count:varint (name:str args:varint str× help:str flags:u8)×  — flags: 1 secret
/// 0x09 Land     packed:blob                                     — deflate-raw Land.Save
/// 0x0A LandEdits count:varint (x:svarint y:svarint terrain:u8 biome:u8 peak:u8 relief:u8)×  — хранимые байты тайла
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
            w.Varint(line.Player).Text(line.Name).Text(line.Text).Byte((byte)((byte)line.Kind | (line.Args is null ? 0 : KeyFlag)));
            if (line.Kind == ChatKind.Whisper)
                w.Text(line.To ?? "");
            if (line.Args is not null)
                WriteArgs(w, line.Args);
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
            var raw = r.Byte();
            var key = (raw & KeyFlag) != 0;
            var kind = (raw & ~KeyFlag) is var k && k <= (byte)ChatKind.Whisper ? (ChatKind)k : throw new FormatException($"строка чата: вид {raw}");
            var to = kind == ChatKind.Whisper ? r.Text() : null;
            lines.Add(new ChatLine(player, name, text, kind, to, key ? ReadArgs(ref r) : null));
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

    /// <summary>Отказ сообщением хоста: reason — ключ раздела host словаря, args — подстановки (клиент пишет на своём языке).</summary>
    public static byte[] Refused(HostText reason, bool password = false)
    {
        var w = new WireWriter(32 + reason.Key.Length * 2).Byte((byte)MessageType.Refused).Text(reason.Key).Byte((byte)((password ? 1 : 0) | 2));
        WriteArgs(w, reason.Args);
        return w.ToArray();
    }

    /// <summary>Отказ: почему (Args не null — это ключ host.* с подстановками, иначе готовый текст); password — клиенту спросить пароль.</summary>
    public static (string Reason, bool Password, IReadOnlyList<string>? Args) ReadRefused(ReadOnlySpan<byte> message)
    {
        var r = Open(message, MessageType.Refused);
        var reason = r.Text();
        var flags = r.Byte();
        var args = (flags & 2) != 0 ? ReadArgs(ref r) : null;
        return Close(ref r, (reason, (flags & 1) != 0, args));
    }

    /// <summary>Бит в байте вида строки чата: Text — ключ, за строкой подстановки.</summary>
    private const int KeyFlag = 0x80;

    private static void WriteArgs(WireWriter w, IReadOnlyList<string> args)
    {
        if (args.Count % 2 != 0)
            throw new ArgumentException("подстановки идут парами «имя, значение»");
        w.Varint((ulong)args.Count);
        foreach (var arg in args)
            w.Text(arg);
    }

    private static List<string> ReadArgs(ref WireReader r)
    {
        var count = r.Count(1);
        if (count % 2 != 0)
            throw new FormatException("подстановки идут парами «имя, значение»");
        var args = new List<string>(count);
        for (var i = 0; i < count; i++)
            args.Add(r.Text());
        return args;
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

    /// <summary>Правки карты: тайлы хранимыми байтами (<c>Land.Stored</c>) — клиент пишет их поверх своей карты.</summary>
    public static byte[] LandEdits(IReadOnlyList<LandTile> edits)
    {
        var w = new WireWriter(8 + edits.Count * 8).Byte((byte)MessageType.LandEdits).Varint((ulong)edits.Count);
        foreach (var edit in edits)
            w.SVarint(edit.X).SVarint(edit.Y).Byte(edit.Tile.Terrain).Byte(edit.Tile.Biome).Byte(edit.Tile.Peak).Byte(edit.Tile.Relief);
        return w.ToArray();
    }

    public static List<LandTile> ReadLandEdits(ReadOnlySpan<byte> message)
    {
        var r = Open(message, MessageType.LandEdits);
        var edits = new List<LandTile>();
        for (var count = r.Count(6); count > 0; count--)
        {
            var (x, y) = (r.SVarint(), r.SVarint());
            if (x is < int.MinValue or > int.MaxValue || y is < int.MinValue or > int.MaxValue)
                throw new FormatException("правка карты: тайл вне чисел");
            edits.Add(new LandTile((int)x, (int)y, new StoredTile(r.Byte(), r.Byte(), r.Byte(), r.Byte())));
        }
        return Close(ref r, edits);
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
