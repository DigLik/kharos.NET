using System.Text;
using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Server;

// Швы, которые заменит новый хост (игроки и жетоны, файл сохранения): всё, что сервер знает об игроках и сохранении
// мира, — здесь. Остальной код видит только эти интерфейсы.

/// <summary>Игрок в списке сервера: клиентский идентификатор (жетон), номер в мире, имя.</summary>
public sealed record PlayerRecord(string Id, uint Player, string Name);

/// <summary>
/// Соответствие клиентских идентификаторов (id в строке подключения) номерам игроков хоста. Игрок с тем же id после
/// перезапуска возвращается к своим юнитам. Потокобезопасен. ШОВ: заменяется игроками и жетонами нового хоста.
/// </summary>
public interface IPlayerRegistry
{
    /// <summary>Знает ли сервер игрока с таким id (новому места на карте не занято).</summary>
    bool Known(string? id);

    /// <summary>Номер игрока по id; нет id или он новый — заводится новый номер.</summary>
    uint Join(string? id, string? name);

    IReadOnlyList<PlayerRecord> Players { get; }

    string NameOf(uint player);

    /// <summary>Подставляет сохранённый список (при запуске).</summary>
    void Restore(IEnumerable<PlayerRecord> players);
}

/// <summary>Реестр в памяти; номера с 1 по порядку.</summary>
public sealed class PlayerRegistry : IPlayerRegistry
{
    private readonly Dictionary<string, PlayerRecord> _byId = [];
    private readonly Dictionary<uint, PlayerRecord> _byPlayer = [];
    private readonly Lock _lock = new();
    private uint _next = 1;

    public bool Known(string? id)
    {
        lock (_lock)
            return !string.IsNullOrEmpty(id) && _byId.ContainsKey(id);
    }

    public uint Join(string? id, string? name)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(id) && _byId.TryGetValue(id, out var known))
            {
                if (!string.IsNullOrEmpty(name) && name != known.Name)
                    Put(known with { Name = name });
                return known.Player;
            }
            // Без id игрок одноразовый: свой номер на каждое подключение, id ему служит выдуманный.
            var player = _next++;
            Put(new PlayerRecord(string.IsNullOrEmpty(id) ? $"anonymous-{player}" : id, player, name ?? ""));
            return player;
        }
    }

    private void Put(PlayerRecord record)
    {
        _byId[record.Id] = record;
        _byPlayer[record.Player] = record;
    }

    public IReadOnlyList<PlayerRecord> Players
    {
        get
        {
            lock (_lock)
                return [.. _byPlayer.Values.OrderBy(record => record.Player)];
        }
    }

    public string NameOf(uint player)
    {
        lock (_lock)
            return _byPlayer.TryGetValue(player, out var record) && record.Name.Length > 0 ? record.Name : $"Игрок {player}";
    }

    public void Restore(IEnumerable<PlayerRecord> players)
    {
        lock (_lock)
            foreach (var record in players)
            {
                Put(record);
                _next = Math.Max(_next, record.Player + 1);
            }
    }
}

/// <summary>Снимок для файла: версия, тик, игроки и мир (<see cref="WorldSave"/>).</summary>
public sealed record SaveSnapshot(int Version, ulong Tick, IReadOnlyList<PlayerRecord> Players, byte[] World);

/// <summary>Хранилище сохранения. ШОВ: заменяется файлами сохранения нового хоста (слои, тик, моды).</summary>
public interface ISaveStore
{
    /// <summary>Описание места для журнала.</summary>
    string Description { get; }

    /// <summary>
    /// Прочитанное сохранение; нет файла — null. Файл другой версии откладывается в сторону (<c>&lt;имя&gt;.v&lt;версия&gt;</c>),
    /// нечитаемый — в <c>&lt;имя&gt;.&lt;время&gt;.old</c>: молча перезаписать их значило бы потерять старый мир.
    /// </summary>
    SaveSnapshot? Load(Action<string> log);

    /// <summary>Сохранение не подошло миру (не разобралось): отложить в сторону.</summary>
    void Reject(string reason, Action<string> log);

    /// <summary>Атомарная запись: во временный файл и подмена, чтобы упавший посреди записи сервер не оставил половину.</summary>
    void Write(SaveSnapshot snapshot);
}

/// <summary>
/// Файл сохранения: <c>"KHSV" version:i32 tick:u64 players:varint (id:str player:u32 name:str) world-length:u32 world</c>;
/// мир — <see cref="WorldSave"/>. Версия — <see cref="Welcome.ProtocolVersion"/>.
/// </summary>
public sealed class FileSaveStore(string path) : ISaveStore
{
    private static ReadOnlySpan<byte> Magic => "KHSV"u8;
    private readonly Lock _lock = new();

    public string Path { get; } = path;

    public string Description => Path;

    public SaveSnapshot? Load(Action<string> log)
    {
        if (!File.Exists(Path))
            return null;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(Path);
        }
        catch (IOException error)
        {
            throw new IOException($"сохранение {Path} не читается: {error.Message}", error);
        }
        try
        {
            using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
            if (!reader.ReadBytes(4).AsSpan().SequenceEqual(Magic))
                throw new FormatException("не сохранение сервера");
            var version = reader.ReadInt32();
            if (version != Welcome.ProtocolVersion)
            {
                var aside = $"{Path}.v{version}";
                File.Move(Path, aside, overwrite: true);
                log($"сохранение версии {version}, а игра версии {Welcome.ProtocolVersion}: отложено в {aside}, мир начинается заново");
                return null;
            }
            var tick = reader.ReadUInt64();
            var count = reader.ReadInt32();
            var players = new List<PlayerRecord>(Math.Clamp(count, 0, 4096));
            for (var at = 0; at < count; at++)
                players.Add(new PlayerRecord(reader.ReadString(), reader.ReadUInt32(), reader.ReadString()));
            var length = reader.ReadInt32();
            var world = reader.ReadBytes(length);
            if (world.Length != length)
                throw new FormatException("файл обрезан");
            return new SaveSnapshot(version, tick, players, world);
        }
        catch (Exception error) when (error is FormatException or EndOfStreamException or ArgumentException or OverflowException)
        {
            Reject(error.Message, log);
            return null;
        }
    }

    public void Reject(string reason, Action<string> log)
    {
        var aside = $"{Path}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.old";
        for (var n = 2; File.Exists(aside); n++)
            aside = $"{Path}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{n}.old";
        File.Move(Path, aside);
        log($"сохранение не читается ({reason}), отложено в {aside}, мир начинается заново");
    }

    public void Write(SaveSnapshot snapshot)
    {
        // Записи идут друг за другом: две разом легли бы в один временный файл.
        lock (_lock)
        {
            if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);
            var temporary = Path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(snapshot.Version);
                writer.Write(snapshot.Tick);
                writer.Write(snapshot.Players.Count);
                foreach (var record in snapshot.Players)
                {
                    writer.Write(record.Id);
                    writer.Write(record.Player);
                    writer.Write(record.Name);
                }
                writer.Write(snapshot.World.Length);
                writer.Write(snapshot.World);
                writer.Flush();
                // На диск до подмены: иначе после сбоя питания файл мог бы оказаться пустым.
                stream.Flush(true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
    }
}

/// <summary>
/// Перенос сохранённого мира в мир хоста через публичный API мира: нынешний <c>Host</c> не принимает готовый мир.
/// Сохраняются сущности и компоненты (с номерами); тик, слои (туман) и порядок строк хранилищ — нет.
/// ШОВ: уходит вместе с загрузкой сохранения нового хоста.
/// </summary>
public static class WorldRestore
{
    /// <summary>Заменяет содержимое <paramref name="target"/> содержимым сохранения. Не подошло — <see cref="FormatException"/>.</summary>
    public static void Apply(GameWorld target, byte[] saved)
    {
        var source = WorldSave.Load(target.Schema, saved);
        var wanted = new List<uint>();
        source.Entities(wanted);
        var keep = new HashSet<uint>(wanted);
        // Номера, которых в сохранении нет, — прочь (в том числе то, что хост успел создать при старте и входе).
        var present = new List<uint>();
        target.Entities(present);
        foreach (var entity in present)
            if (!keep.Contains(entity))
                target.Destroy(entity);
        // Недостающие номера: выданные хостом дальше, чем сохранение, не вернуть; выше — заводятся и лишние снимаются.
        foreach (var entity in wanted)
        {
            if (target.Alive(entity))
                continue;
            if (entity < target.NextEntity)
                throw new FormatException($"номер {entity} уже занят и снят в мире хоста: сохранение не подходит");
            while (target.NextEntity <= entity)
            {
                var made = target.Spawn();
                if (!keep.Contains(made))
                    target.Destroy(made);
            }
        }
        var schema = target.Schema;
        for (var component = 0; component < schema.Count; component++)
        {
            var fields = schema[component].Fields;
            foreach (var entity in wanted)
            {
                var has = source.Has(entity, component);
                if (!has)
                {
                    if (target.Has(entity, component))
                        target.Remove(entity, component);
                    continue;
                }
                target.Add(entity, component);
                for (var field = 0; field < fields.Count; field++)
                    target.Set(entity, component, field, source.Get(entity, component, field));
            }
        }
    }
}
