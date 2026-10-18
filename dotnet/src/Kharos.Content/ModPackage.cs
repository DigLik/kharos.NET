using System.Text.Json;

namespace Kharos.Content;

/// <summary>Ошибка загрузки модов: где и что.</summary>
public sealed class ContentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Мод (docs/dotnet.md, Р12) — папка: манифест <c>mod.json</c>, данные <c>data/*.json</c>, код хоста <c>host.wasm</c> и
/// клиента <c>client.wasm</c>. Данные движок читает без запуска кода. Пакет можно собрать и из байтов (мод, присланный
/// сервером).
/// <code>
/// mod.json:
/// {
///   "name": "base",                 — имя (иначе "id", иначе имя папки)
///   "version": "0.1.0", "title": "Kharos",
///   "requires": ["core", "?extra"], — раньше этого мода; "?имя" — необязательная зависимость
///   "game": "skirmish",             — партия по умолчанию (запись data/games.json), последний мод главнее
///   "host": "host.wasm", "client": "client.wasm",
///   "components": { "Drive": { "speed": 0, "move": "", "moving": false, "queue": null } }
/// }
/// </code>
/// Вид поля компонента — по значению по умолчанию: число, флаг, строка; объект, список или null — байты.
/// </summary>
public sealed class ModPackage
{
    private static readonly HashSet<string> Keys =
        ["name", "id", "version", "title", "abi", "requires", "game", "host", "client", "components", "about"];

    public ModPackage(string fallbackName, ReadOnlySpan<byte> manifest, IReadOnlyDictionary<string, byte[]> data, byte[]? hostWasm = null, byte[]? clientWasm = null, string? directory = null)
    {
        Directory = directory;
        HostWasm = hostWasm;
        ClientWasm = clientWasm;
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, bytes) in data)
            files[name] = bytes;
        Data = files;
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(manifest.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            root = document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new ContentException($"{fallbackName}/mod.json: {error.Message}", error);
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new ContentException($"{fallbackName}/mod.json: нужен объект");
        Manifest = root;
        Name = Text(root, "name") ?? Text(root, "id") ?? fallbackName;
        if (Name.Length == 0 || Name.Contains('/'))
            throw new ContentException($"{fallbackName}/mod.json: имя мода «{Name}»");
        string where = $"{Name}/mod.json";
        foreach (var property in root.EnumerateObject())
            if (!Keys.Contains(property.Name))
                throw new ContentException($"{where}: незнакомое поле {property.Name}");
        Version = Text(root, "version") ?? "";
        Title = Text(root, "title") ?? Name;
        Game = Text(root, "game");
        var requires = new List<string>();
        if (root.TryGetProperty("requires", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array)
                throw new ContentException($"{where}: requires — список имён");
            foreach (var item in list.EnumerateArray())
                requires.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new ContentException($"{where}: requires — список имён"));
        }
        Requires = requires;
        if (root.TryGetProperty("components", out var components))
        {
            if (components.ValueKind != JsonValueKind.Object)
                throw new ContentException($"{where}: components — объект «компонент → поля»");
            foreach (var component in components.EnumerateObject())
                if (component.Value.ValueKind != JsonValueKind.Object)
                    throw new ContentException($"{where}: components.{component.Name} — объект «поле → значение по умолчанию»");
            Components = components;
        }
    }

    /// <summary>Имя мода.</summary>
    public string Name { get; }
    public string Version { get; }
    public string Title { get; }

    /// <summary>Зависимости: «имя» или «?имя» (необязательная).</summary>
    public IReadOnlyList<string> Requires { get; }

    /// <summary>Партия по умолчанию.</summary>
    public string? Game { get; }

    /// <summary>mod.json целиком.</summary>
    public JsonElement Manifest { get; }

    /// <summary>Объявления компонентов (components из mod.json) или null.</summary>
    public JsonElement? Components { get; }

    /// <summary>data/*.json по имени файла без расширения (по порядку имён).</summary>
    public IReadOnlyDictionary<string, byte[]> Data { get; }

    public byte[]? HostWasm { get; }
    public byte[]? ClientWasm { get; }

    /// <summary>Откуда прочитан; у присланного — null.</summary>
    public string? Directory { get; }

    /// <summary>Мод из папки: mod.json, data/*.json, host.wasm и client.wasm (пути — из манифеста, если заданы).</summary>
    public static ModPackage Read(string directory)
    {
        var path = Path.Combine(directory, "mod.json");
        if (!File.Exists(path))
            throw new ContentException($"{directory}: нет mod.json");
        var manifest = File.ReadAllBytes(path);
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var dataDir = Path.Combine(directory, "data");
        if (System.IO.Directory.Exists(dataDir))
            foreach (var file in System.IO.Directory.GetFiles(dataDir, "*.json"))
                data[Path.GetFileNameWithoutExtension(file)] = File.ReadAllBytes(file);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        // Пути к wasm — из манифеста; сначала разбор без них, чтобы прочитать поля host и client.
        var probe = new ModPackage(name, manifest, data, directory: directory);
        byte[]? Wasm(string key, string fallback)
        {
            var file = Path.Combine(directory, Text(probe.Manifest, key) ?? fallback);
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }
        return new ModPackage(name, manifest, data, Wasm("host", "host.wasm"), Wasm("client", "client.wasm"), directory);
    }

    /// <summary>Моды из папки: каждая подпапка с mod.json, по имени папки.</summary>
    public static List<ModPackage> Discover(string root)
    {
        if (!System.IO.Directory.Exists(root))
            throw new ContentException($"нет папки модов {root}");
        var dirs = System.IO.Directory.GetDirectories(root).Where(dir => File.Exists(Path.Combine(dir, "mod.json"))).ToList();
        dirs.Sort(StringComparer.Ordinal);
        return [.. dirs.Select(Read)];
    }

    /// <summary>
    /// Порядок загрузки: зависимости раньше, при равенстве — по имени (crates/content/src/mods.rs). Нет обязательной
    /// зависимости или зависимости по кругу — <see cref="ContentException"/>.
    /// </summary>
    public static List<string> Order(IReadOnlyDictionary<string, IReadOnlyList<string>> requires)
    {
        var done = new List<string>();
        var left = new SortedSet<string>(requires.Keys, StringComparer.Ordinal);
        while (left.Count > 0)
        {
            string? ready = null;
            foreach (var name in left)
            {
                var ok = requires[name].All(need =>
                {
                    var optional = need.StartsWith('?');
                    var target = optional ? need[1..] : need;
                    return done.Contains(target) || (optional && !requires.ContainsKey(target));
                });
                if (ok)
                {
                    ready = name;
                    break;
                }
            }
            if (ready is null)
            {
                var missing = left.SelectMany(name => requires[name]).Where(need => !need.StartsWith('?') && !requires.ContainsKey(need)).Distinct().ToList();
                if (missing.Count > 0)
                    throw new ContentException($"нет модов: {string.Join(", ", missing)}");
                throw new ContentException($"моды зависят друг от друга по кругу: {string.Join("; ", left.Select(name => $"{name} (нужны: {string.Join(", ", requires[name])})"))}");
            }
            left.Remove(ready);
            done.Add(ready);
        }
        return done;
    }

    private static string? Text(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new ContentException($"mod.json: {key} — строка");
    }

    public override string ToString() => Version.Length > 0 ? $"{Name} {Version}" : Name;
}
