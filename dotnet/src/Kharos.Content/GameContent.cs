using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;

namespace Kharos.Content;

/// <summary>Вид: запись файла данных. Номер — по разделам и именам, одинаковый у всех, у кого те же моды.</summary>
public sealed record Proto(int Id, string Section, string Name)
{
    /// <summary>«раздел/имя».</summary>
    public string Path => $"{Section}/{Name}";
}

/// <summary>Объявление поля компонента модом: значение по умолчанию задаёт вид.</summary>
public sealed record ComponentDeclaration(string Mod, string Component, string Field, JsonElement Default);

/// <summary>
/// Партия — запись data/games.json: <c>size</c> — поле от −size до size по обеим осям (без него карты и обзора нет),
/// <c>fog</c> — туман войны (по умолчанию есть), <c>land</c> — местность, запись data/lands.json с параметрами генератора
/// (имена как у GeneratorConfig в TS). Остальное — правила мода, их читает его код.
/// </summary>
public sealed record GameInfo(string Name, int? Size, bool Fog, string? LandName, GeneratorConfig? Land, JsonElement Data)
{
    /// <summary>Прямоугольник карты в тайлах [left, right) × [top, bottom); нет size — null.</summary>
    public (int Left, int Top, int Right, int Bottom)? Bounds => Size is { } size ? (-size, -size, size, size) : null;
}

/// <summary>
/// Загруженные моды (docs/dotnet.md, Р12): порядок, данные после патчей, виды с номерами, объявления компонентов и
/// слоёв, партии и сумма для сверки хоста с клиентом. Код модов не запускается.
/// </summary>
public sealed class GameContent
{
    /// <summary>Файл объявлений слоёв (формат LayerSpec.ParseAll).</summary>
    public const string LayersFile = "layers";
    public const string GamesFile = "games";
    public const string LandsFile = "lands";

    private readonly Dictionary<string, Proto> _protoByPath = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, byte[]> _data = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _parsed = new(StringComparer.Ordinal);

    private GameContent(List<ModPackage> mods)
    {
        Mods = mods;
        var merged = new SortedDictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var mod in mods)
            foreach (var (name, bytes) in mod.Data)
            {
                var node = JsonPatch.Parse(bytes, $"{mod.Name}/data/{name}.json");
                merged[name] = merged.TryGetValue(name, out var target) ? JsonPatch.Merge(target, node)! : node;
            }
        foreach (var (name, node) in merged)
        {
            _data[name] = JsonPatch.Write(node);
            using var document = JsonDocument.Parse(_data[name]);
            _parsed[name] = document.RootElement.Clone();
        }

        // Виды: записи-объекты всех файлов данных, по разделам и именам.
        var protos = new List<Proto>();
        foreach (var (section, root) in _parsed)
        {
            if (root.ValueKind != JsonValueKind.Object)
                continue;
            var names = root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object).Select(p => p.Name).ToList();
            names.Sort(StringComparer.Ordinal);
            foreach (var name in names)
            {
                var proto = new Proto(protos.Count, section, name);
                protos.Add(proto);
                _protoByPath[proto.Path] = proto;
            }
        }
        Protos = protos;

        var components = new List<ComponentDeclaration>();
        foreach (var mod in mods)
            if (mod.Components is { } declared)
                foreach (var component in declared.EnumerateObject())
                    foreach (var field in component.Value.EnumerateObject())
                        components.Add(new ComponentDeclaration(mod.Name, component.Name, field.Name, field.Value.Clone()));
        Components = components;

        try
        {
            Layers = _data.TryGetValue(LayersFile, out var layers) ? LayerSpec.ParseAll(Encoding.UTF8.GetString(layers)) : [];
        }
        catch (FormatException error)
        {
            throw new ContentException($"data/{LayersFile}.json: {error.Message}", error);
        }
        DefaultGame = mods.LastOrDefault(mod => mod.Game is not null)?.Game;
        Checksum = ComputeChecksum();
    }

    /// <summary>Моды в порядке загрузки.</summary>
    public IReadOnlyList<ModPackage> Mods { get; }

    /// <summary>Виды по номерам.</summary>
    public IReadOnlyList<Proto> Protos { get; }

    /// <summary>Объявления компонентов в порядке модов, компонентов и полей.</summary>
    public IReadOnlyList<ComponentDeclaration> Components { get; }

    /// <summary>Слои из data/layers.json (после патчей).</summary>
    public IReadOnlyList<LayerSpec> Layers { get; }

    /// <summary>Партия по умолчанию: game последнего мода, где он задан.</summary>
    public string? DefaultGame { get; }

    /// <summary>SHA-256 того, что должно совпасть у хоста и клиента: моды и версии, данные, компоненты, client.wasm.</summary>
    public byte[] Checksum { get; }

    /// <summary>Имена файлов данных по порядку.</summary>
    public IEnumerable<string> DataNames => _data.Keys;

    /// <summary>Канонические байты файла данных после патчей; нет — null.</summary>
    public byte[]? DataBytes(string name) => _data.GetValueOrDefault(name);

    /// <summary>Файл данных после патчей; нет — null.</summary>
    public JsonElement? Data(string name) => _parsed.TryGetValue(name, out var root) ? root : null;

    /// <summary>Вид по «раздел/имя»; нет — null.</summary>
    public Proto? FindProto(string path) => _protoByPath.GetValueOrDefault(path);

    /// <summary>Моды из папки: <see cref="ModPackage.Discover"/> и <see cref="Load(IEnumerable{ModPackage})"/>.</summary>
    public static GameContent Load(string root) => Load(ModPackage.Discover(root));

    /// <summary>Порядок модов по requires, затем данные, виды и объявления. Два мода с одним именем — ошибка.</summary>
    public static GameContent Load(IEnumerable<ModPackage> packages)
    {
        var byName = new Dictionary<string, ModPackage>(StringComparer.Ordinal);
        foreach (var package in packages)
            if (!byName.TryAdd(package.Name, package))
                throw new ContentException($"два мода с именем {package.Name}");
        var order = ModPackage.Order(byName.ToDictionary(pair => pair.Key, pair => pair.Value.Requires, StringComparer.Ordinal));
        return new GameContent([.. order.Select(name => byName[name])]);
    }

    /// <summary>Объявляет в схеме компоненты модов (по порядку). Одно поле с разными видами — <see cref="ContentException"/>.</summary>
    public void DeclareComponents(Schema schema)
    {
        foreach (var declaration in Components)
        {
            try
            {
                schema.Declare(declaration.Component, declaration.Field, declaration.Default);
            }
            catch (ArgumentException error)
            {
                throw new ContentException($"{declaration.Mod}/mod.json: {error.Message}", error);
            }
        }
    }

    /// <summary>Партия по имени (null — по умолчанию). Нет такой — <see cref="ContentException"/>.</summary>
    public GameInfo Game(string? name = null)
    {
        name ??= DefaultGame ?? throw new ContentException("партия не задана: нет game ни в одном mod.json");
        if (Data(GamesFile) is not { ValueKind: JsonValueKind.Object } games || !games.TryGetProperty(name, out var game) || game.ValueKind != JsonValueKind.Object)
            throw new ContentException($"нет партии {name} в data/{GamesFile}.json");
        int? size = null;
        var fog = true;
        string? landName = null;
        if (game.TryGetProperty("size", out var sizeValue))
            size = sizeValue.TryGetInt32(out var number) && number > 0 ? number : throw new ContentException($"games.{name}.size — целое больше нуля");
        if (game.TryGetProperty("fog", out var fogValue))
            fog = fogValue.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new ContentException($"games.{name}.fog — true или false"),
            };
        GeneratorConfig? land = null;
        if (game.TryGetProperty("land", out var landValue))
        {
            landName = landValue.ValueKind == JsonValueKind.String ? landValue.GetString()! : throw new ContentException($"games.{name}.land — имя местности");
            if (Data(LandsFile) is not { ValueKind: JsonValueKind.Object } lands || !lands.TryGetProperty(landName, out var config))
                throw new ContentException($"games.{name}: нет местности {landName} в data/{LandsFile}.json");
            land = Generator(config, $"lands.{landName}");
        }
        return new GameInfo(name, size, fog, landName, land, game.Clone());
    }

    /// <summary>Параметры генератора из JSON (имена — как у GeneratorConfig в TS); незнакомое поле — ошибка.</summary>
    public static GeneratorConfig Generator(JsonElement json, string where = "генератор")
    {
        if (json.ValueKind != JsonValueKind.Object)
            throw new ContentException($"{where}: нужен объект");
        var config = new GeneratorConfig();
        foreach (var property in json.EnumerateObject())
        {
            var value = property.Value;
            double Number() => value.ValueKind == JsonValueKind.Number ? value.GetDouble() : throw new ContentException($"{where}.{property.Name} — число");
            config = property.Name switch
            {
                "seed" => config with { Seed = value.TryGetInt32(out var seed) ? seed : throw new ContentException($"{where}.seed — целое") },
                "zoneScale" => config with { ZoneScale = Number() },
                "zoneWarp" => config with { ZoneWarp = Number() },
                "swampLevel" => config with { SwampLevel = Number() },
                "rockLevel" => config with { RockLevel = Number() },
                "biomeScale" => config with { BiomeScale = Number() },
                "peakChance" => config with { PeakChance = Number() },
                "peakMinRadius" => config with { PeakMinRadius = Number() },
                "peakMaxRadius" => config with { PeakMaxRadius = Number() },
                "doubleChance" => config with { DoubleChance = Number() },
                "tripleChance" => config with { TripleChance = Number() },
                "saltSwampShift" => config with { SaltSwampShift = Number() },
                "saltRockShift" => config with { SaltRockShift = Number() },
                "saltPeakFactor" => config with { SaltPeakFactor = Number() },
                "redSwampShift" => config with { RedSwampShift = Number() },
                "redRockShift" => config with { RedRockShift = Number() },
                "redPeakFactor" => config with { RedPeakFactor = Number() },
                "marshSwampShift" => config with { MarshSwampShift = Number() },
                "marshRockShift" => config with { MarshRockShift = Number() },
                "marshPeakFactor" => config with { MarshPeakFactor = Number() },
                "tierStep" => config with { TierStep = Number() },
                "cliffScale" => config with { CliffScale = Number() },
                "cliffShare" => config with { CliffShare = Number() },
                "depositChance" => config with { DepositChance = Number() },
                "depositRichness" => config with { DepositRichness = Number() },
                "blank" => config with { Blank = value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.False ? false : throw new ContentException($"{where}.blank — true или false")) },
                "about" => config,
                _ => throw new ContentException($"{where}: незнакомое поле {property.Name}"),
            };
        }
        return config;
    }

    private byte[] ComputeChecksum()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Text(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Blob(bytes);
        }
        void Blob(ReadOnlySpan<byte> bytes)
        {
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        Text("KHC1");
        foreach (var mod in Mods)
        {
            Text(mod.Name);
            Text(mod.Version);
            Blob(mod.ClientWasm is { } wasm ? SHA256.HashData(wasm) : []);
        }
        foreach (var (name, bytes) in _data)
        {
            Text(name);
            Blob(bytes);
        }
        foreach (var declaration in Components)
        {
            Text(declaration.Component);
            Text(declaration.Field);
            Text(declaration.Default.GetRawText());
        }
        return hash.GetHashAndReset();
    }
}
