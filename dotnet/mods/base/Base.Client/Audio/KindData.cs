using System.Text.Json;
using Kharos.Mod.Sdk.Client;

namespace Base.Client.Audio;

/// <summary>Что звуку нужно знать о виде юнита (look.motor, look.pitch, speed, turn).</summary>
internal sealed record UnitSound(string Motor, double Pitch, double Speed, double Turn);

/// <summary>Что звуку нужно знать о виде здания: мощность, добыча, переработка и сборка (buildings.json).</summary>
internal sealed record BuildingSound(double Power, bool Extract, string? Refines, string? Assembles);

/// <summary>
/// Данные видов для звука: читаются из data/units.json и data/buildings.json (после патчей модов) раз, на первом кадре.
/// Ключ — «раздел/имя» вида, как у ClientContext.ProtoPath.
/// </summary>
internal sealed class KindData
{
    public Dictionary<string, UnitSound> Units { get; } = new(StringComparer.Ordinal);

    /// <summary>Мощность здания (data.power): с положительной звучит гул сети.</summary>
    public Dictionary<string, double> Power { get; } = new(StringComparer.Ordinal);

    /// <summary>Здания вида «buildings/имя»: то, от чего зависит звук работы.</summary>
    public Dictionary<string, BuildingSound> Buildings { get; } = new(StringComparer.Ordinal);

    /// <summary>Класс юнита (units.json, kind): infantry, vehicle, heavy, air… — по виду «units/имя».</summary>
    public Dictionary<string, string> UnitKinds { get; } = new(StringComparer.Ordinal);

    /// <summary>Сколько секунд пуск ракеты шахты (silo.warhead.launch).</summary>
    public double Launch { get; private set; } = 30;

    public static KindData Read(ClientContext c)
    {
        var data = new KindData();
        foreach (var (name, record) in Records(c, "units"))
        {
            // Мотор и тон — облик пилота, подобъект look.
            var look = record.TryGetProperty("look", out var value) && value.ValueKind == JsonValueKind.Object ? value : record;
            if (record.TryGetProperty("kind", out var unitKind) && unitKind.ValueKind == JsonValueKind.String)
                data.UnitKinds["units/" + name] = unitKind.GetString()!;
            if (look.TryGetProperty("motor", out var motor) && motor.ValueKind == JsonValueKind.String)
                data.Units["units/" + name] = new UnitSound(motor.GetString()!, Num(look, "pitch", 1), Num(record, "speed", 1), Num(record, "turn", 1));
        }
        foreach (var (name, record) in Records(c, "buildings"))
        {
            data.Power["buildings/" + name] = Num(record, "power", 0);
            data.Buildings["buildings/" + name] = new BuildingSound(Num(record, "power", 0), record.TryGetProperty("extract", out var extract) && extract.ValueKind == JsonValueKind.True,
                Text(record, "refines"), Text(record, "assembles"));
            if (name == "silo" && record.TryGetProperty("warhead", out var warhead) && warhead.ValueKind == JsonValueKind.Object)
                data.Launch = Num(warhead, "launch", 30);
        }
        return data;
    }

    private static List<(string Name, JsonElement Record)> Records(ClientContext c, string file)
    {
        var records = new List<(string, JsonElement)>();
        if (!c.TryData(file, out var bytes))
            return records;
        using var document = JsonDocument.Parse(bytes.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return records;
        foreach (var property in document.RootElement.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Object)
                records.Add((property.Name, property.Value.Clone()));
        return records;
    }

    private static string? Text(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double Num(JsonElement record, string name, double fallback) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;
}
