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
