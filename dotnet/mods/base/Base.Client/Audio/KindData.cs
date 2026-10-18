using System.Text.Json;
using Kharos.Mod.Sdk.Client;

namespace Base.Client.Audio;

/// <summary>Что звуку нужно знать о виде юнита (look.motor, look.pitch, speed, turn).</summary>
internal sealed record UnitSound(string Motor, double Pitch, double Speed, double Turn);

/// <summary>
/// Данные видов для звука: читаются из data/units.json и data/buildings.json (после патчей модов) раз, на первом кадре.
/// Ключ — «раздел/имя» вида, как у ClientContext.ProtoPath.
/// </summary>
internal sealed class KindData
{
    public Dictionary<string, UnitSound> Units { get; } = new(StringComparer.Ordinal);

    /// <summary>Мощность здания (data.power): с положительной звучит гул сети.</summary>
    public Dictionary<string, double> Power { get; } = new(StringComparer.Ordinal);

    public static KindData Read(ClientContext c)
    {
        var data = new KindData();
        foreach (var (name, record) in Records(c, "units"))
        {
            // Мотор и тон — облик пилота, подобъект look.
            var look = record.TryGetProperty("look", out var value) && value.ValueKind == JsonValueKind.Object ? value : record;
            if (look.TryGetProperty("motor", out var motor) && motor.ValueKind == JsonValueKind.String)
                data.Units["units/" + name] = new UnitSound(motor.GetString()!, Num(look, "pitch", 1), Num(record, "speed", 1), Num(record, "turn", 1));
        }
        foreach (var (name, record) in Records(c, "buildings"))
            data.Power["buildings/" + name] = Num(record, "power", 0);
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

    private static double Num(JsonElement record, string name, double fallback) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;
}
