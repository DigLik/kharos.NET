namespace Base.Shared.Data;

/// <summary>Местность (lands.json): параметры генератора, имена — как у GeneratorConfig в TS. Землю строит движок.</summary>
public sealed class LandSpec
{
    public string? About { get; set; }
    public int Seed { get; set; }
    public double ZoneScale { get; set; }
    public double ZoneWarp { get; set; }
    public double SwampLevel { get; set; }
    public double RockLevel { get; set; }
    public double BiomeScale { get; set; }
    public double PeakChance { get; set; }
    public double PeakMinRadius { get; set; }
    public double PeakMaxRadius { get; set; }
    public double DoubleChance { get; set; }
    public double TripleChance { get; set; }
    public double SaltSwampShift { get; set; }
    public double SaltRockShift { get; set; }
    public double SaltPeakFactor { get; set; }
    public double RedSwampShift { get; set; }
    public double RedRockShift { get; set; }
    public double RedPeakFactor { get; set; }
    public double MarshSwampShift { get; set; }
    public double MarshRockShift { get; set; }
    public double MarshPeakFactor { get; set; }
    public double TierStep { get; set; }
    public double CliffScale { get; set; }
    public double CliffShare { get; set; }
    public double DepositChance { get; set; }
    public double DepositRichness { get; set; }
    public bool Blank { get; set; }
}

/// <summary>Слой (layers.json, docs/layers.md): объявление для движка (LayerSpec), здесь — для проверки данных.</summary>
public sealed class LayerRecord
{
    public string? About { get; set; }
    public string Scope { get; set; } = "";
    public string Shape { get; set; } = "";
    public string[] Channels { get; set; } = [];
    public double? Cell { get; set; }
    public Dictionary<string, string> Fields { get; set; } = [];
    public bool Save { get; set; }
    public bool Send { get; set; } = true;
    public bool Remember { get; set; }
    public bool Allies { get; set; }
}

/// <summary>Земля партии (games.json, настройка land): имя записи lands.json или готовая запись прямо в настройке.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(LandRefConverter))]
public sealed class LandRef
{
    /// <summary>Имя записи lands.json; у встроенной записи — null.</summary>
    public string? Name { get; init; }

    /// <summary>Запись, заданная объектом; у именованной — null.</summary>
    public LandSpec? Inline { get; init; }
}

/// <summary>Читает и пишет land строкой (имя) или объектом (запись).</summary>
public sealed class LandRefConverter : System.Text.Json.Serialization.JsonConverter<LandRef>
{
    public override LandRef? Read(ref System.Text.Json.Utf8JsonReader reader, Type type, System.Text.Json.JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            System.Text.Json.JsonTokenType.String => new LandRef { Name = reader.GetString() },
            System.Text.Json.JsonTokenType.StartObject => new LandRef { Inline = System.Text.Json.JsonSerializer.Deserialize(ref reader, BaseJson.Default.LandSpec) },
            System.Text.Json.JsonTokenType.Null => null,
            _ => throw new System.Text.Json.JsonException("land: имя записи или объект"),
        };

    public override void Write(System.Text.Json.Utf8JsonWriter writer, LandRef value, System.Text.Json.JsonSerializerOptions options)
    {
        if (value.Inline is { } inline)
            System.Text.Json.JsonSerializer.Serialize(writer, inline, BaseJson.Default.LandSpec);
        else
            writer.WriteStringValue(value.Name);
    }
}
