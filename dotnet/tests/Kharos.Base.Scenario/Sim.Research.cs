using System.Buffers.Binary;
using Base.Shared;
using Base.Shared.Queries;

namespace Kharos.Base.Scenario;

/// <summary>Цена темы по правилам партии (topicPrice): кредиты, сырьё по порядку данных, секунды.</summary>
public sealed record TopicPriceState(double Cost, Dictionary<string, double> Materials, double Seconds);

// Исследования и уровни (раздел Research, С6): план, прогресс и открытые темы игрока — запросы research.*; выдать темы
// прямо в мир (learn и grantResearch в тестах TS) — запись Science.done на сущности счёта.
public sealed partial class Sim
{
    /// <summary>План исследований игрока, первая — головная (planOf).</summary>
    public string[] PlanOf(uint player) => Query<NameList>(ResearchQueries.Plan, new PlayerArgs { Player = player }).Items;

    /// <summary>Открытые темы игрока по порядку (researchOf).</summary>
    public string[] ResearchOf(uint player) => Query<NameList>(ResearchQueries.Done, new PlayerArgs { Player = player }).Items;

    /// <summary>Сколько темы игрок исследовал, доля (progressOf).</summary>
    public double ProgressOf(uint player, string topic) => Query<double>(ResearchQueries.Progress, new TopicArgs { Player = player, Topic = topic });

    /// <summary>Открыта ли игроку тема (hasResearch; без дерева технологий — да).</summary>
    public bool HasResearch(uint player, string topic) => Query<bool>(ResearchQueries.Has, new TopicArgs { Player = player, Topic = topic });

    /// <summary>Тема, которую лаборатории игрока ведут сейчас (pickTopic); нет — null.</summary>
    public string? CurrentTopic(uint player) => Query<string>(ResearchQueries.Current, new PlayerArgs { Player = player }) is { Length: > 0 } topic ? topic : null;

    /// <summary>Цена темы по правилам партии (topicPrice).</summary>
    public TopicPriceState TopicPrice(string topic)
    {
        var info = Query<TopicPriceInfo>(ResearchQueries.Price, new TopicArgs { Topic = topic });
        return new TopicPriceState(info.Cost, info.Goods.Zip(info.Amounts).ToDictionary(pair => pair.First, pair => pair.Second), info.Seconds);
    }

    /// <summary>Сырьё нынешней и головной тем игрока, сколько ещё потратить (researchNeeds).</summary>
    public Dictionary<string, double> ResearchNeeds(uint player)
    {
        var list = Query<AmountList>(ResearchQueries.Needs, new PlayerArgs { Player = player });
        return list.Goods.Zip(list.Amounts).ToDictionary(pair => pair.First, pair => pair.Second);
    }

    /// <summary>Чего не хватает на следующий уровень здания (upgradeLack): research, credits, materials; всего хватает — null.</summary>
    public string? UpgradeLack(uint player, uint building) =>
        Query<string>(ResearchQueries.UpgradeLack, new TargetArgs { Player = player, Target = building }) is { Length: > 0 } lack ? lack : null;

    /// <summary>Поднимают ли здание игрока сейчас (isUpgradable).</summary>
    public bool Upgradable(uint player, uint building) => Query<bool>(ResearchQueries.Upgradable, new TargetArgs { Player = player, Target = building });

    /// <summary>Уровень здания (levelOf): у стройки и не здания — 1.</summary>
    public double LevelOf(uint entity) => Has(entity, Names.Building.Name) ? Num(entity, "Building.level") : 1;

    /// <summary>
    /// Открывает игроку темы без исследования прямо в мире (learn, grantResearch): дописывает в Science.done на сущности
    /// счёта (она должна быть — <see cref="SetCredits"/>). Без списка — все темы.
    /// </summary>
    public void Learn(uint player, params string[] topics)
    {
        var wallet = Wallet(player);
        if (wallet == 0)
            throw new InvalidOperationException($"у игрока {player} нет счёта");
        if (topics.Length == 0)
            topics = [.. Host.Content.Protos.Where(proto => proto.Section == "research").Select(proto => proto.Name)];
        var bytes = Bytes(wallet, "Science.done");
        var ids = new List<int>();
        for (var at = 0; at + 4 <= bytes.Length; at += 4)
            ids.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at)));
        foreach (var topic in topics)
            if (Host.Content.FindProto("research/" + topic) is { } proto && !ids.Contains(proto.Id))
                ids.Add(proto.Id);
        var packed = new byte[ids.Count * 4];
        for (var i = 0; i < ids.Count; i++)
            BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(i * 4), ids[i]);
        Set(wallet, "Science.done", packed);
    }

    /// <summary>Состояние лаборатории (Research): скорость до сотых и чего не хватает.</summary>
    public (double Speed, string Lack) LabOf(uint lab) => (Num(lab, "Research.speed"), Text(lab, "Research.lack"));
}
