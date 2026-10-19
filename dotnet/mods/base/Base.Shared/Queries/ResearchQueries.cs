using MemoryPack;

namespace Base.Shared.Queries;

// Запросы исследований и уровней (Host.Query: тесты, администратор, подсказки интерфейса) — только чтение мира.

/// <summary>Аргументы «игрок Player и тема Topic» (ключ research.json).</summary>
[MemoryPackable]
public sealed partial class TopicArgs
{
    public uint Player { get; set; }
    public string Topic { get; set; } = "";
}

/// <summary>Цена темы по правилам партии (topicPrice): кредиты, сырьё (груз и сколько, по порядку данных), секунды.</summary>
[MemoryPackable]
public sealed partial class TopicPriceInfo
{
    public double Cost { get; set; }
    public string[] Goods { get; set; } = [];
    public double[] Amounts { get; set; } = [];
    public double Seconds { get; set; }
}

/// <summary>Сколько чего: грузы и количества по порядку.</summary>
[MemoryPackable]
public sealed partial class AmountList
{
    public string[] Goods { get; set; } = [];
    public double[] Amounts { get; set; } = [];
}

/// <summary>Имена запросов исследований.</summary>
public static class ResearchQueries
{
    /// <summary>Открытые темы игрока по порядку (researchOf): PlayerArgs → NameList.</summary>
    public const string Done = "research.done";

    /// <summary>План исследований игрока, первая — головная (planOf): PlayerArgs → NameList.</summary>
    public const string Plan = "research.plan";

    /// <summary>Сколько темы игрок уже исследовал, доля 0…1 (progressOf): TopicArgs → double.</summary>
    public const string Progress = "research.progress";

    /// <summary>Открыта ли игроку тема (hasResearch; без дерева технологий — всё): TopicArgs → bool.</summary>
    public const string Has = "research.has";

    /// <summary>Тема, которую лаборатории игрока ведут сейчас (currentTopic / pickTopic); нет — "": PlayerArgs → string.</summary>
    public const string Current = "research.current";

    /// <summary>Цена темы по правилам партии (topicPrice): TopicArgs → TopicPriceInfo.</summary>
    public const string Price = "research.price";

    /// <summary>Сырьё нынешней и головной тем, сколько ещё потратить (researchNeeds): PlayerArgs → AmountList.</summary>
    public const string Needs = "research.needs";

    /// <summary>Чего не хватает на следующий уровень здания (upgradeLack): research, credits, materials; всего хватает — "": TargetArgs → string.</summary>
    public const string UpgradeLack = "research.upgradeLack";

    /// <summary>Поднимают ли это здание игрока сейчас (isUpgradable): TargetArgs → bool.</summary>
    public const string Upgradable = "research.upgradable";
}
