using System.Globalization;
using System.Text.Json;
using Kharos.Mod.Sdk;

namespace Base.Client;

/// <summary>Числа и строки пилота (Luau): круглое и tostring как в Luau, чтобы подписи панелей совпали.</summary>
internal static class Lua
{
    /// <summary>math.round: половина — от нуля.</summary>
    public static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>tostring числа: целое без дроби, иначе 14 значащих цифр (%.14g).</summary>
    public static string ToString(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15 ? ((long)value).ToString(CultureInfo.InvariantCulture) : value.ToString("G14", CultureInfo.InvariantCulture);
}

/// <summary>
/// Вид мода «раздел/имя» (запись data/раздел.json): то, что в пилоте лежало в <c>kind.data</c>. Поля подачи пилота
/// (title, motor, pitch, exit, color) лежат в подобъекте <c>look</c>: поле ищется в записи, затем в нём.
/// </summary>
internal sealed class Kind(string path, JsonElement record)
{
    /// <summary>«Раздел/имя».</summary>
    public string Path { get; } = path;

    public string Name => Path[(Path.IndexOf('/') + 1)..];

    private bool TryGet(string field, out JsonElement value)
    {
        if (record.TryGetProperty(field, out value) && value.ValueKind != JsonValueKind.Null)
            return true;
        return record.TryGetProperty("look", out var look) && look.ValueKind == JsonValueKind.Object
            && look.TryGetProperty(field, out value) && value.ValueKind != JsonValueKind.Null;
    }

    public bool Has(string field) => TryGet(field, out _);

    public bool TryNumber(string field, out double number)
    {
        if (TryGet(field, out var value) && value.ValueKind == JsonValueKind.Number)
        {
            number = value.GetDouble();
            return true;
        }
        number = 0;
        return false;
    }

    /// <summary>Булево поле: true — только для true в данных.</summary>
    public bool Flag(string field) => TryGet(field, out var value) && value.ValueKind == JsonValueKind.True;

    public double Number(string field, double fallback = 0) => TryNumber(field, out var number) ? number : fallback;

    public string? Text(string field) => TryGet(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public string[] Strings(string field) =>
        TryGet(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
            : [];

    /// <summary>Турели на виде (data.mounts пилота).</summary>
    public Mount[] Mounts() =>
        record.TryGetProperty("mounts", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("turret", out var turret) && turret.ValueKind == JsonValueKind.String)
                .Select(item => new Mount(item.GetProperty("turret").GetString()!, Field(item, "along"), Field(item, "across")))]
            : [];

    private static double Field(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
}

/// <summary>
/// Данные мода (data/*.json после патчей, docs/dotnet.md Р12) для подачи: виды по «раздел/имя». Читается раз при
/// загрузке; правка JSON другим модом доходит до панелей без пересборки.
/// </summary>
internal sealed class ClientData
{
    private static readonly string[] Sections = ["units", "turrets", "buildings", "deposits", "games", "lands"];

    private readonly Dictionary<string, Kind> _kinds = new(StringComparer.Ordinal);

    public static ClientData Load(LoadContext ctx)
    {
        var data = new ClientData();
        foreach (var section in Sections)
        {
            if (!ctx.TryData(section, out var bytes))
                continue;
            using var document = JsonDocument.Parse(bytes.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Object)
                    data._kinds[section + "/" + property.Name] = new Kind(section + "/" + property.Name, property.Value.Clone());
        }
        return data;
    }

    /// <summary>Вид по «раздел/имя»; нет — null.</summary>
    public Kind? Find(string path) => _kinds.GetValueOrDefault(path);

    /// <summary>Идущая партия: запись data/games.json.</summary>
    public Kind? Game(string name) => Find("games/" + name);

    /// <summary>Сторона поля партии, тайлов в каждую сторону от нуля; нет — 64.</summary>
    public double FieldSize(string game) => Game(game)?.Number("size", 64) ?? 64;

    /// <summary>Зерно местности партии (lands/&lt;land&gt;.seed); нет — 0.</summary>
    public double LandSeed(string game) =>
        Game(game)?.Text("land") is { } land ? Find("lands/" + land)?.Number("seed") ?? 0 : 0;
}
