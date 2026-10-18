using System.Text.Json;
using Kharos.Engine.World;

namespace Kharos.Content;

/// <summary>Ошибка загрузки модов: где и что.</summary>
public sealed class ContentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Объявление компонента модом (из <c>components</c> в mod.json или из schema/*.json): поля по порядку со значениями
/// по умолчанию и атрибуты (null — не заданы). Без полей и атрибутов — метка.
/// </summary>
public sealed record ComponentDeclaration(
    string Mod,
    string File,
    string Component,
    IReadOnlyList<(string Field, JsonElement Default)> Fields,
    NetMode? Net,
    IReadOnlyList<string> Private,
    bool? Save)
{
    /// <summary>Из mod.json: такие объявления клиент получает с пакетом мода и сверяет суммой.</summary>
    public bool FromManifest => File == "mod.json";
}

/// <summary>
/// Мод (docs/dotnet.md, Р12) — папка: манифест <c>mod.json</c>, данные <c>data/*.json</c>, объявления компонентов
/// <c>schema/*.json</c> (по файлу на раздел, только хосту: клиент получает схему в приветствии), код хоста
/// <c>host.wasm</c> и клиента <c>client.wasm</c>. Данные движок читает без запуска кода. Пакет можно собрать и из байтов
/// (мод, присланный сервером).
/// <code>
/// mod.json:
/// {
///   "name": "base",                 — имя (иначе "id", иначе имя папки)
///   "version": "0.1.0", "title": "Kharos",
///   "requires": ["core", "?extra"], — раньше этого мода; "?имя" — необязательная зависимость
///   "game": "skirmish",             — партия по умолчанию (запись data/games.json), последний мод главнее
///   "host": "host.wasm", "client": "client.wasm",
///   "components": { "Drive": { "speed": 0, "move": "", "moving": false, "queue": null }, "Drop": {} }
/// }
/// schema/&lt;раздел&gt;.json — то же, что components:
/// { "Repair": { "radius": 5, "rate": 1, "target": 0, "@net": "owner" }, "Armed": { "target": 0, "@private": ["target"] } }
/// </code>
/// Вид поля компонента — по значению по умолчанию: число, флаг, строка; объект, список или null — байты. Ключи с
/// <c>@</c> — атрибуты: <c>@net</c> (all, owner, none — кому уходит по сети), <c>@private</c> (скрытые от чужих поля),
/// <c>@save</c> (идёт ли в сохранение). Компонент без полей (<c>{}</c>) — метка.
/// </summary>
public sealed class ModPackage
{
    private static readonly HashSet<string> Keys =
        ["name", "id", "version", "title", "abi", "requires", "game", "host", "client", "components", "about"];

    public ModPackage(string fallbackName, ReadOnlySpan<byte> manifest, IReadOnlyDictionary<string, byte[]> data, byte[]? hostWasm = null, byte[]? clientWasm = null, string? directory = null, IReadOnlyDictionary<string, byte[]>? schema = null)
    {
        Directory = directory;
        HostWasm = hostWasm;
        ClientWasm = clientWasm;
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, bytes) in data)
            files[name] = bytes;
        Data = files;
        var schemaFiles = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, bytes) in schema ?? new Dictionary<string, byte[]>())
            schemaFiles[name] = bytes;
        Schema = schemaFiles;
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
        var declarations = new List<ComponentDeclaration>();
        // Сначала schema/*.json по именам файлов, потом components из mod.json: так номера компонентов одни у всех.
        foreach (var (file, bytes) in Schema)
        {
            JsonElement section;
            try
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                section = document.RootElement.Clone();
            }
            catch (JsonException error)
            {
                throw new ContentException($"{Name}/schema/{file}.json: {error.Message}", error);
            }
            if (section.ValueKind != JsonValueKind.Object)
                throw new ContentException($"{Name}/schema/{file}.json: нужен объект «компонент → поля»");
            Declare(declarations, $"schema/{file}.json", section);
        }
        if (root.TryGetProperty("components", out var components))
        {
            if (components.ValueKind != JsonValueKind.Object)
                throw new ContentException($"{where}: components — объект «компонент → поля»");
            Components = components;
            Declare(declarations, "mod.json", components);
        }
        Declarations = declarations;
    }

    /// <summary>Объявления компонентов из объекта «компонент → поля и атрибуты».</summary>
    private void Declare(List<ComponentDeclaration> declarations, string file, JsonElement components)
    {
        var where = $"{Name}/{file}";
        foreach (var component in components.EnumerateObject())
        {
            if (component.Name.Length == 0 || component.Name.StartsWith('@'))
                throw new ContentException($"{where}: имя компонента «{component.Name}»");
            if (component.Value.ValueKind != JsonValueKind.Object)
                throw new ContentException($"{where}: {component.Name} — объект «поле → значение по умолчанию»");
            var fields = new List<(string, JsonElement)>();
            NetMode? net = null;
            bool? save = null;
            var hidden = new List<string>();
            foreach (var property in component.Value.EnumerateObject())
            {
                var value = property.Value;
                var at = $"{where}: {component.Name}.{property.Name}";
                switch (property.Name)
                {
                    case "@net":
                        net = value.ValueKind == JsonValueKind.String ? value.GetString() switch
                        {
                            "all" => NetMode.All,
                            "owner" => NetMode.Owner,
                            "none" => NetMode.None,
                            _ => throw new ContentException($"{at} — all, owner или none"),
                        } : throw new ContentException($"{at} — all, owner или none");
                        break;
                    case "@save":
                        save = value.ValueKind switch
                        {
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => throw new ContentException($"{at} — true или false"),
                        };
                        break;
                    case "@private":
                        if (value.ValueKind != JsonValueKind.Array)
                            throw new ContentException($"{at} — список полей");
                        foreach (var item in value.EnumerateArray())
                            hidden.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new ContentException($"{at} — список полей"));
                        break;
                    default:
                        if (property.Name.StartsWith('@'))
                            throw new ContentException($"{at}: незнакомый атрибут (есть @net, @private, @save)");
                        if (property.Name.Length == 0)
                            throw new ContentException($"{where}: {component.Name}: поле без имени");
                        fields.Add((property.Name, value.Clone()));
                        break;
                }
            }
            declarations.Add(new ComponentDeclaration(Name, file, component.Name, fields, net, hidden, save));
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

    /// <summary>Все объявления компонентов мода: schema/*.json по именам файлов, затем components из mod.json.</summary>
    public IReadOnlyList<ComponentDeclaration> Declarations { get; }

    /// <summary>data/*.json по имени файла без расширения (по порядку имён).</summary>
    public IReadOnlyDictionary<string, byte[]> Data { get; }

    /// <summary>schema/*.json по имени файла без расширения (по порядку имён); у присланного мода пусто.</summary>
    public IReadOnlyDictionary<string, byte[]> Schema { get; }

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
        var schema = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var schemaDir = Path.Combine(directory, "schema");
        if (System.IO.Directory.Exists(schemaDir))
            foreach (var file in System.IO.Directory.GetFiles(schemaDir, "*.json"))
                schema[Path.GetFileNameWithoutExtension(file)] = File.ReadAllBytes(file);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        // Пути к wasm — из манифеста; сначала разбор без них, чтобы прочитать поля host и client.
        var probe = new ModPackage(name, manifest, data, directory: directory, schema: schema);
        byte[]? Wasm(string key, string fallback)
        {
            var file = Path.Combine(directory, Text(probe.Manifest, key) ?? fallback);
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }
        return new ModPackage(name, manifest, data, Wasm("host", "host.wasm"), Wasm("client", "client.wasm"), directory, schema);
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
