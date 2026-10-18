namespace Base.Shared.Data;

/// <summary>Ресурс (resources.json, ResourceSpec в resources.ts): цена продажи, добывается ли, тема для закупки.</summary>
public sealed class ResourceSpec
{
    public string? About { get; set; }
    public double Price { get; set; }
    public bool Mined { get; set; }
    public string? Research { get; set; }
}

/// <summary>Руда (ores.json): какой ресурс из неё выходит.</summary>
public sealed class OreSpec
{
    public string? About { get; set; }
    public string Resource { get; set; } = "";
}

/// <summary>Изделие цеха (products.json, ProductSpec): рецепт и выход за сборку.</summary>
public sealed class ProductSpec
{
    public string? About { get; set; }
    public Dictionary<string, double> Recipe { get; set; } = [];
    public double Yield { get; set; }
}

/// <summary>Тема исследования (research.json, TopicSpec в topics.ts).</summary>
public sealed class TopicSpec
{
    public string? About { get; set; }
    public string[] After { get; set; } = [];
    public Dictionary<string, double> Uses { get; set; } = [];
    public string? Trigger { get; set; }
}

/// <summary>Месторождение (deposits.json, DepositSpec в deposits.ts): запас, добыча, доли по биомам.</summary>
public sealed class DepositSpec
{
    public string? About { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double Rate { get; set; }
    public Dictionary<string, double> Biomes { get; set; } = [];
    public LookSpec? Look { get; set; }
}

/// <summary>Награда за первое действие (rewards.json, REWARDS в economy.ts).</summary>
public sealed class RewardSpec
{
    public string? About { get; set; }
    public double Credits { get; set; }
}
