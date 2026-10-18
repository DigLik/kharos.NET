using System.Security.Cryptography;
using System.Text.Json;
using Kharos.Engine.Terrain;

namespace Kharos.Engine.Tests.Terrain;

/// <summary>
/// Эталоны из <c>dotnet/tools/golden/terrain.ts</c> (bun dotnet/tools/golden/terrain.ts). Лежат в исходниках тестов, а
/// не в выходном каталоге: каталог ищется вверх от сборки тестов до <c>Kharos.slnx</c>.
/// </summary>
internal static class Golden
{
    private static readonly Lazy<string> Tests = new(() =>
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Kharos.slnx")))
                return Path.Combine(directory.FullName, "tests", "Kharos.Engine.Tests");
        throw new DirectoryNotFoundException($"Kharos.slnx не найден выше {AppContext.BaseDirectory}");
    });

    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Путь к файлу эталона: area — папка тестов (Terrain, Math), name — имя в её golden/.</summary>
    public static string PathOf(string area, string name) => Path.Combine(Tests.Value, area, "golden", name);

    public static JsonElement ReadJson(string area, string name) => JsonDocument.Parse(File.ReadAllBytes(PathOf(area, name))).RootElement;

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Сумма чанков подряд — как sha(...parts) в эталонном скрипте.</summary>
    public static string Sha256(IEnumerable<byte[]> parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts) hash.AppendData(part);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static GeneratorConfig Config(JsonElement element) => element.Deserialize<GeneratorConfig>(Json)!;

    public static LandArea Area(JsonElement element) =>
        new(element.GetProperty("left").GetInt32(), element.GetProperty("top").GetInt32(), element.GetProperty("width").GetInt32(), element.GetProperty("height").GetInt32());
}
