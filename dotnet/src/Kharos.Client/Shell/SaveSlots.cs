using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Kharos.Client.Game;
using Kharos.Protocol;

namespace Kharos.Client.Shell;

/// <summary>
/// Слот сохранения (SaveSlot в storage.ts): название, когда заведён и когда в него писали (мс эпохи), сколько тиков
/// прошло, сторона карты, зерно местности, партия (запись games.json) и настройки нового мира — JSON поверх записи
/// партии (size, land — генератор с зерном, weather): пока мир слота не сохранён, он заводится по ним, дальше
/// настройки лежат в самом сохранении.
/// </summary>
public sealed record SaveSlot(string Id, string Name, long Created, long Updated, ulong Tick, int Size, int? Height, long Seed,
    string? Game = null, string? Settings = null)
{
    /// <summary>В слот ещё ничего не записано.</summary>
    public bool Empty => Updated == Created && Tick == 0;
}

/// <summary>Куда смотрела камера: центр экрана в тайлах и масштаб.</summary>
public readonly record struct CameraSave(double X, double Y, double Zoom);

/// <summary>Что видно о сохранении, не поднимая мир: партия, тик, зерно хоста и настройки (раздел META файла хоста).</summary>
public sealed record SaveInfo(string Game, ulong Seed, ulong Tick, string? Settings)
{
    /// <summary>Сторона карты и высота (нет — квадрат) и зерно местности из настроек; чего нет — null.</summary>
    public (int? Size, int? Height, long? LandSeed) Map()
    {
        if (Settings is null)
            return (null, null, null);
        try
        {
            using var json = JsonDocument.Parse(Settings);
            var root = json.RootElement;
            int? Int(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
            long? seed = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("land", out var land) && land.ValueKind == JsonValueKind.Object
                && land.TryGetProperty("seed", out var s) && s.TryGetInt64(out var n) ? n : null;
            return (Int("size"), Int("height"), seed);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    /// <summary>
    /// Разбор файла сохранения хоста: не сохранение — <see cref="FormatException"/>, другая версия —
    /// <see cref="SaveVersionException"/> (тоже FormatException).
    /// </summary>
    public static SaveInfo Read(ReadOnlySpan<byte> file) => Read(SaveFile.Unpack(file));

    public static SaveInfo Read(IReadOnlyDictionary<string, byte[]> sections)
    {
        if (!sections.TryGetValue("META", out var meta))
            throw new FormatException("в сохранении нет раздела META");
        var r = new WireReader(meta);
        var game = r.Text();
        var seed = r.Varint();
        r.F64();
        var tick = r.Varint();
        r.Take(8);
        r.Blob();
        r.Varint();
        var settings = r.End ? null : Encoding.UTF8.GetString(r.Blob());
        return new SaveInfo(game, seed, tick, settings is { Length: > 0 } ? settings : null);
    }
}

/// <summary>
/// Слоты сохранений локальной игры (storage.ts) поверх <see cref="IStorage"/>: список с названиями и временем — под
/// <see cref="IndexKey"/> (меню не разбирает миры целиком), миры — файлами сохранения хоста («KHSV») под
/// «slots/&lt;id&gt;/world», камера — «slots/&lt;id&gt;/camera». Новый слот пуст; запись мира описывает слот (тик, карта,
/// зерно); удалённый слот не воскресает от поздней записи. Миры прежнего формата (JSON) не переносятся: при чтении
/// списка их слоты пропадают вместе с камерой, пустые слоты остаются. Файл сохранения переносится в новый слот,
/// чужой файл — ошибка.
/// </summary>
public sealed class SaveSlots(IStorage storage, Func<long>? clock = null)
{
    public const string IndexKey = "slots/index";

    /// <summary>Единственное сохранение самых старых версий — JSON, удаляется (kharos.save, kharos.camera в TS).</summary>
    public const string LegacyKey = "save", LegacyCameraKey = "camera";

    /// <summary>Раздел файла с названием слота: его добавляет перенос в файл (<see cref="Export"/>).</summary>
    public const string NameSection = "NAME";

    private readonly Func<long> _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public IStorage Storage { get; } = storage;

    public static string WorldKey(string id) => $"slots/{id}/world";
    public static string CameraKey(string id) => $"slots/{id}/camera";

    /// <summary>Имя нового слота при переносе файла без названия (menu.importedName).</summary>
    public string ImportedName { get; set; } = "Imported game";

    /// <summary>Слоты от последнего сыгранного к давнему.</summary>
    public List<SaveSlot> List()
    {
        DropJson();
        var slots = ReadIndex();
        slots.Sort((a, b) => b.Updated.CompareTo(a.Updated));
        return slots;
    }

    public SaveSlot? Find(string id) => List().Find(slot => slot.Id == id);

    /// <summary>Новый пустой слот: мир в него положит первая запись.</summary>
    public SaveSlot Create(string name, int size, long seed, int? height = null, string? game = null, string? settings = null)
    {
        var now = _clock();
        var slot = new SaveSlot(Guid.NewGuid().ToString("N"), name, now, now, 0, size, height is { } h && h != size ? h : null, seed, game, settings);
        var slots = List();
        slots.Add(slot);
        WriteIndex(slots);
        return slot;
    }

    /// <summary>Мир слота — файл сохранения хоста; нет его — null.</summary>
    public byte[]? Load(string id) => Storage.Get(WorldKey(id)) is { Length: > 0 } file && IsSave(file) ? file : null;

    /// <summary>Кладёт мир в слот и описывает слот по нему. Удалённый слот не воскресает: false.</summary>
    public bool Store(string id, byte[] save)
    {
        var info = SaveInfo.Read(save);
        var slots = List();
        var index = slots.FindIndex(slot => slot.Id == id);
        if (index < 0)
            return false;
        Storage.Set(WorldKey(id), save);
        var (size, height, landSeed) = info.Map();
        var slot = slots[index];
        slots[index] = slot with
        {
            Updated = Math.Max(_clock(), slot.Created + 1),
            Tick = info.Tick,
            Size = size ?? slot.Size,
            Height = height is { } h && h != (size ?? slot.Size) ? h : null,
            Seed = landSeed ?? slot.Seed,
            Game = info.Game,
            Settings = info.Settings ?? slot.Settings,
        };
        WriteIndex(slots);
        return true;
    }

    public void Rename(string id, string name) =>
        WriteIndex([.. List().Select(slot => slot.Id == id ? slot with { Name = name } : slot)]);

    public void Delete(string id)
    {
        WriteIndex([.. List().Where(slot => slot.Id != id)]);
        Storage.Set(CameraKey(id), []);
        Storage.Set(WorldKey(id), []);
    }

    /// <summary>Файл для переноса: тот же мир и раздел с названием слота; нечего переносить — null.</summary>
    public byte[]? Export(string id)
    {
        if (Find(id) is not { } slot || Load(id) is not { } file)
            return null;
        var sections = SaveFile.Unpack(file);
        var name = new ArrayBufferWriter<byte>(64);
        using (var json = new Utf8JsonWriter(name))
            json.WriteStringValue(slot.Name);
        sections[NameSection] = name.WrittenSpan.ToArray();
        return SaveFile.Pack(sections.Select(pair => (pair.Key, pair.Value)));
    }

    /// <summary>Кладёт файл сохранения в новый слот. Не сохранение или не читается — <see cref="FormatException"/>.</summary>
    public SaveSlot Import(ReadOnlySpan<byte> file)
    {
        var sections = SaveFile.Unpack(file);
        var info = SaveInfo.Read(sections);
        string? name = null;
        if (sections.Remove(NameSection, out var bytes))
        {
            try
            {
                using var json = JsonDocument.Parse(bytes);
                name = json.RootElement.ValueKind == JsonValueKind.String ? json.RootElement.GetString() : null;
            }
            catch (JsonException)
            {
            }
        }
        var (size, height, landSeed) = info.Map();
        var slot = Create(string.IsNullOrWhiteSpace(name) ? ImportedName : name, size ?? 0, landSeed ?? 0, height, info.Game, info.Settings);
        Store(slot.Id, SaveFile.Pack(sections.Select(pair => (pair.Key, pair.Value))));
        return Find(slot.Id) ?? slot;
    }

    /// <summary>Сохранённое место камеры в слоте или null, если его нет или оно испорчено.</summary>
    public CameraSave? LoadCamera(string id)
    {
        if (Storage.Get(CameraKey(id)) is not { } bytes)
            return null;
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object && Number(root, "x") is { } x && Number(root, "y") is { } y && Number(root, "zoom") is { } zoom)
                return new CameraSave(x, y, zoom);
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public void StoreCamera(string id, CameraSave camera)
    {
        if (!double.IsFinite(camera.X) || !double.IsFinite(camera.Y) || !double.IsFinite(camera.Zoom))
            return;
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("x", camera.X);
            json.WriteNumber("y", camera.Y);
            json.WriteNumber("zoom", camera.Zoom);
            json.WriteEndObject();
        }
        Storage.Set(CameraKey(id), buffer.WrittenSpan);
    }

    private static double? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.GetDouble() is var number && double.IsFinite(number) ? number : null;

    /// <summary>Файл сохранения хоста, а не прежний JSON.</summary>
    private static bool IsSave(ReadOnlySpan<byte> file) => file.Length >= 4 && file[..4].SequenceEqual("KHSV"u8);

    /// <summary>
    /// Сохранения прежнего формата — JSON — не переносятся: их слоты пропадают из списка вместе с камерой. Слоты, в
    /// которые ещё ничего не записано, остаются.
    /// </summary>
    private void DropJson()
    {
        if (Storage.Get(LegacyKey) is not null)
        {
            Storage.Set(LegacyKey, []);
            Storage.Set(LegacyCameraKey, []);
        }
        var slots = ReadIndex();
        var stale = slots.Where(slot => Storage.Get(WorldKey(slot.Id)) is { Length: > 0 } file && !IsSave(file)).Select(slot => slot.Id).ToHashSet();
        if (stale.Count == 0)
            return;
        foreach (var id in stale)
        {
            Storage.Set(WorldKey(id), []);
            Storage.Set(CameraKey(id), []);
        }
        WriteIndex([.. slots.Where(slot => !stale.Contains(slot.Id))]);
    }

    private List<SaveSlot> ReadIndex()
    {
        if (Storage.Get(IndexKey) is not { } bytes)
            return [];
        try
        {
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
                return [];
            var slots = new List<SaveSlot>();
            foreach (var item in json.RootElement.EnumerateArray())
                if (ReadSlot(item) is { } slot)
                    slots.Add(slot);
            return slots;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static SaveSlot? ReadSlot(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
            return null;
        string? Text(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        long Long(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;
        int? Int(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;
        var tick = item.TryGetProperty("tick", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetUInt64(out var ticks) ? ticks : 0;
        var settings = item.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object ? s.GetRawText() : null;
        return new SaveSlot(id.GetString()!, Text("name") ?? "", Long("created"), Long("updated"), tick, Int("size") ?? 0, Int("height"), Long("seed"), Text("game"), settings);
    }

    private void WriteIndex(IEnumerable<SaveSlot> slots)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartArray();
            foreach (var slot in slots)
            {
                json.WriteStartObject();
                json.WriteString("id", slot.Id);
                json.WriteString("name", slot.Name);
                json.WriteNumber("created", slot.Created);
                json.WriteNumber("updated", slot.Updated);
                json.WriteNumber("tick", slot.Tick);
                json.WriteNumber("size", slot.Size);
                if (slot.Height is { } height)
                    json.WriteNumber("height", height);
                json.WriteNumber("seed", slot.Seed);
                if (slot.Game is { } game)
                    json.WriteString("game", game);
                if (slot.Settings is { } settings)
                {
                    json.WritePropertyName("settings");
                    try
                    {
                        using var parsed = JsonDocument.Parse(settings);
                        parsed.RootElement.WriteTo(json);
                    }
                    catch (JsonException)
                    {
                        json.WriteNullValue();
                    }
                }
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }
        Storage.Set(IndexKey, buffer.WrittenSpan);
    }

    /// <summary>Сколько времени играли: минуты и часы из тиков (20 в секунду).</summary>
    public static (int Hours, int Minutes) Playtime(ulong tick, double dt = 0.05)
    {
        var minutes = (int)Math.Floor(tick * dt / 60);
        return (minutes / 60, minutes % 60);
    }

    /// <summary>Дата для списка: «19.10.2026 14:05» (сборка без культур — числами, одинаково на всех языках).</summary>
    public static string Date(long time) =>
        DateTimeOffset.FromUnixTimeMilliseconds(time).ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
}
