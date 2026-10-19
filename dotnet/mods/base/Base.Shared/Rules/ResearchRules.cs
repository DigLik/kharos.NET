using System.Runtime.CompilerServices;
using Base.Shared.Data;

namespace Base.Shared.Rules;

/// <summary>Цена темы целиком (TopicPrice): кредиты, сырьё по порядку записи данных и секунды работы одной лаборатории.</summary>
public sealed record TopicPrice(double Cost, IReadOnlyList<(string Good, double Amount)> Materials, double Seconds);

/// <summary>
/// Дерево технологий, общее у хоста и клиента (topics.ts, research.ts, upgrades.ts): ярусы и цены тем, путь к теме,
/// темы плана, которые можно вести, уровни зданий. Только данные (research.json) и списки тем — мир читает тот, кто зовёт.
/// Темы-открытия (trigger) лаборатории не ведут: они открываются сами; ярус они не добавляют.
/// </summary>
public static class ResearchRules
{
    /// <summary>Цена темы первого яруса в кредитах и её срок в секундах (TIER_CREDITS, TIER_SECONDS).</summary>
    public const double TierCredits = 100, TierSeconds = 20;

    /// <summary>Сколько секунд головная тема плана стоит без сырья, прежде чем лаборатории возьмутся за следующую (STALL_SECONDS).</summary>
    public const double StallSeconds = 10;

    /// <summary>Как часто сверяется, есть ли темам сырьё, в секундах (PICK_SECONDS).</summary>
    public const double PickSeconds = 1;

    /// <summary>Доля погрешности: столько от темы считается сделанным, а сырья — найденным (EPSILON в research.ts).</summary>
    public const double Epsilon = 1e-9;

    /// <summary>Темы уровней зданий: второй и третий (levelTopic).</summary>
    public const string Levels2 = "levels2", Levels3 = "levels3";

    /// <summary>Какое исследование открывает подъём здания с уровня level (levelTopic).</summary>
    public static string LevelTopic(double level) => level >= 2 ? Levels3 : Levels2;

    /// <summary>Тема ли это (isTopic).</summary>
    public static bool IsTopic(IReadOnlyDictionary<string, TopicSpec> topics, string? topic) => topic is not null && topics.ContainsKey(topic);

    /// <summary>Тема-открытие ли это: её не исследуют, она открывается сама (isTrigger).</summary>
    public static bool IsTrigger(IReadOnlyDictionary<string, TopicSpec> topics, string topic) => topics.TryGetValue(topic, out var spec) && spec.Trigger is not null;

    /// <summary>Предшественники темы: без них её не начать (topicsBefore).</summary>
    public static IReadOnlyList<string> Before(IReadOnlyDictionary<string, TopicSpec> topics, string topic) =>
        topics.TryGetValue(topic, out var spec) ? spec.After : [];

    private static readonly ConditionalWeakTable<IReadOnlyDictionary<string, TopicSpec>, Dictionary<string, int>> Tiers = new();

    /// <summary>
    /// Ярус сложности темы (tierOf): 1 — без предшественников, дальше — на единицу глубже самого глубокого из них.
    /// Открытия — ярус 1 и сложности не добавляют. Кэш — по таблице тем.
    /// </summary>
    public static int Tier(IReadOnlyDictionary<string, TopicSpec> topics, string topic)
    {
        var tiers = Tiers.GetValue(topics, _ => []);
        lock (tiers)
            return Tier(topics, topic, tiers, 0);
    }

    private static int Tier(IReadOnlyDictionary<string, TopicSpec> topics, string topic, Dictionary<string, int> tiers, int depth)
    {
        if (tiers.TryGetValue(topic, out var tier))
            return tier;
        var deepest = 0;
        // Круг в данных ярус не зацикливает: тест данных его ловит, здесь — просто стоп.
        if (depth < 64)
            foreach (var before in Before(topics, topic))
                if (!IsTrigger(topics, before))
                    deepest = Math.Max(deepest, Tier(topics, before, tiers, depth + 1));
        tiers[topic] = tier = 1 + deepest;
        return tier;
    }

    /// <summary>
    /// Цена темы по правилам мира (priceOf): ярус умножает цену и срок на researchGrowth, researchScale множит цену,
    /// researchSlow — срок. Степень — умножением: так она одинакова в CLR и wasm.
    /// </summary>
    public static TopicPrice Price(IReadOnlyDictionary<string, TopicSpec> topics, string topic, RulesSpec rules)
    {
        if (!topics.TryGetValue(topic, out var spec) || spec.Trigger is not null)
            return new TopicPrice(0, [], 0);
        var factor = 1.0;
        for (var i = 1; i < Tier(topics, topic); i++)
            factor *= rules.ResearchGrowth;
        var materials = new List<(string, double)>();
        foreach (var (good, amount) in spec.Uses)
            materials.Add((good, BuildRules.Round(amount * factor * rules.ResearchScale)));
        return new TopicPrice(BuildRules.Round(TierCredits * factor * rules.ResearchScale), materials, TierSeconds * factor * rules.ResearchSlow);
    }

    /// <summary>
    /// Темы, которые надо исследовать, чтобы открыть эту, вместе с ней самой, предшественники раньше; уже открытые
    /// пропускаются (pathTo).
    /// </summary>
    public static List<string> PathTo(IReadOnlyDictionary<string, TopicSpec> topics, string topic, IReadOnlyCollection<string> done)
    {
        var path = new List<string>();
        void Visit(string next, int depth)
        {
            if (done.Contains(next) || path.Contains(next) || depth > 64)
                return;
            foreach (var before in Before(topics, next))
                Visit(before, depth + 1);
            path.Add(next);
        }
        Visit(topic, 0);
        return path;
    }

    /// <summary>Все темы, которые ведут к этой: её предшественники, их предшественники и так далее (topicsUnder).</summary>
    public static HashSet<string> Under(IReadOnlyDictionary<string, TopicSpec> topics, string topic)
    {
        var found = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(topic);
        while (stack.Count > 0)
            foreach (var before in Before(topics, stack.Pop()))
                if (found.Add(before))
                    stack.Push(before);
        return found;
    }

    /// <summary>Можно ли вести тему сейчас: не открыта, все предшественники открыты.</summary>
    public static bool Ready(IReadOnlyDictionary<string, TopicSpec> topics, string topic, IReadOnlyCollection<string> done) =>
        IsTopic(topics, topic) && !done.Contains(topic) && Before(topics, topic).All(done.Contains);

    /// <summary>
    /// Темы плана, которые можно вести сейчас, в порядке плана: ещё не открытые, с открытыми предшественниками
    /// (openTopics). Первая — головная.
    /// </summary>
    public static List<string> Open(IReadOnlyDictionary<string, TopicSpec> topics, IEnumerable<string> plan, IReadOnlyCollection<string> done) =>
        [.. plan.Where(topic => Ready(topics, topic, done))];

    /// <summary>
    /// Тема, которую лаборатории ведут сейчас (currentTopic): pick — выбор вместо головной (простой без сырья), если
    /// его ещё можно вести; иначе головная. null — вести нечего.
    /// </summary>
    public static string? Current(IReadOnlyList<string> open, string? pick) =>
        pick is not null && open.Contains(pick) ? pick : open.Count > 0 ? open[0] : null;

    /// <summary>Что открывает тема: виды зданий и юнитов (unlockedBy).</summary>
    public static List<string> UnlockedBy(BaseTables tables, string topic) =>
        [.. tables.Buildings.Where(pair => pair.Value.Research == topic).Select(pair => pair.Key),
         .. tables.Units.Where(pair => pair.Value.Research == topic).Select(pair => pair.Key)];
}
