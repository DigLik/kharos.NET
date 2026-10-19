using System.Buffers.Binary;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>Доступность темы в окне (TopicView.status в hud.ts).</summary>
internal enum TopicStatus { Available, Soon, Locked, Done }

/// <summary>Тема для окна: ярус, цена, готовность, место в очереди и что открывает (TopicView).</summary>
internal sealed record TopicView(
    string Topic, int Tier, string[] After, double Cost, (string Good, double Amount)[] Materials, string Time, double Progress,
    TopicStatus Status, string? Trigger, int Order, string[] Buildings, string[] Units, string[] Paves);

/// <summary>Лаборатории игрока: сколько, сколько работает, общая скорость и чего не хватает (TechState.labs).</summary>
internal sealed record LabsView(int Count, int Working, double Speed, IReadOnlyList<(string Lack, int Count)> Lacks);

/// <summary>Дерево технологий игрока для окна (TechState в hud.ts): темы, план, ведущаяся тема, лаборатории и срок.</summary>
internal sealed record TechState(IReadOnlyList<TopicView> Topics, IReadOnlyList<string> Plan, string? Current, LabsView Labs, string? Left)
{
    public static readonly TechState Empty = new([], [], null, new LabsView(0, 0, 0, []), null);

    /// <summary>Срок для интерфейса: минуты и секунды, от часа — часы и минуты (clockOf).</summary>
    public static string Clock(double seconds)
    {
        var whole = (long)Math.Floor(seconds + 0.5);
        return whole < 3600
            ? $"{whole / 60}:{whole % 60:00}"
            : $"{whole / 3600}:{whole % 3600 / 60:00}:{whole % 60:00}";
    }
}

/// <summary>
/// Чтение науки из реплики: Science на сущности своего счёта (@net owner) — открытые темы, план и прогресс; лаборатории —
/// свои здания с флагом researches и их Research. Поле lack у Research — строка мира, клиенту она недоступна, поэтому
/// «чего не хватает» выводится из того, что клиент видит: выключена, нет кредитов, нужно сырьё, иначе — энергия.
/// </summary>
internal static class TechReader
{
    public static TechState Read(ClientContext c, BaseState s)
    {
        var data = s.Data.Research;
        if (data.Topics.Count == 0 || Account(c, s) is not { } account)
            return TechState.Empty;
        var science = account.Science;
        var known = Names(c, science.Bytes(s.P.Science["done"]));
        var plan = Names(c, science.Bytes(s.P.Science["plan"]));
        var progress = Progress(c, science.Bytes(s.P.Science["progress"]));
        var open = ResearchRules.Open(data.Topics, plan, known);
        // Ведут головную тему плана; выбор вместо неё при простое без сырья делает хост и клиенту его не сообщает.
        var current = ResearchRules.Current(open, null);

        var (labs, working, speed, lackKinds) = (0, 0, 0.0, new Dictionary<string, int>());
        var currentPrice = current is null ? null : ResearchRules.Price(data.Topics, current, data.Rules);
        foreach (var entity in c.Each(s.P.Building))
        {
            if (!c.Mine(entity) || s.KindOf(c, entity) is not { } kind || !kind.Flag("researches"))
                continue;
            labs++;
            if (c.Get(entity, s.P.Research) is not { } lab)
                continue;
            var rate = lab.Number(s.P.Research["speed"]);
            speed += rate;
            if (rate > 0)
                working++;
            else if (current is not null && Lack(c, s, entity, account.Credits, currentPrice!) is { } lack)
                lackKinds[lack] = lackKinds.GetValueOrDefault(lack) + 1;
        }

        var topics = new List<TopicView>(data.Topics.Count);
        foreach (var (topic, spec) in data.Topics)
        {
            var price = ResearchRules.Price(data.Topics, topic, data.Rules);
            var before = ResearchRules.Before(data.Topics, topic);
            var status = known.Contains(topic) ? TopicStatus.Done
                : spec.Trigger is not null ? TopicStatus.Locked
                : before.All(known.Contains) ? TopicStatus.Available
                : before.All(b => known.Contains(b) || plan.Contains(b)) ? TopicStatus.Soon
                : TopicStatus.Locked;
            var (buildings, units, paves) = data.Unlocks(topic);
            topics.Add(new TopicView(topic, ResearchRules.Tier(data.Topics, topic), [.. before], price.Cost, [.. price.Materials],
                TechState.Clock(price.Seconds), Math.Round(progress.GetValueOrDefault(topic) * 100) / 100, status, spec.Trigger, plan.IndexOf(topic) + 1,
                buildings, units, paves));
        }
        string? left = null;
        if (current is not null && speed > 0)
            left = TechState.Clock((1 - progress.GetValueOrDefault(current)) * currentPrice!.Seconds / speed);
        return new TechState(topics, plan, current, new LabsView(labs, working, Math.Round(speed * 100) / 100, [.. lackKinds.Select(pair => (pair.Key, pair.Value))]), left);
    }

    /// <summary>Чего ждёт стоящая лаборатория: «off», «credits», груз темы или «power».</summary>
    private static string? Lack(ClientContext c, BaseState s, uint lab, double credits, TopicPrice price)
    {
        if (c.Has(lab, s.P.Off))
            return "off";
        if (credits < 1 && price.Cost > 0)
            return "credits";
        return price.Materials.Count > 0 ? price.Materials[0].Good : "power";
    }

    private readonly record struct Held(ComponentValues Science, double Credits);

    private static Held? Account(ClientContext c, BaseState s)
    {
        foreach (var entity in c.Each(s.P.Player))
            if (c.Get(entity, s.P.Player) is { } player && player.Number(s.P.Player["id"]) == c.Player && c.Get(entity, s.P.Science) is { } science)
                return new Held(science, player.Number(s.P.Player["credits"]));
        return null;
    }

    /// <summary>Номера видов i32 подряд → имена тем.</summary>
    private static List<string> Names(ClientContext c, byte[] bytes)
    {
        var list = new List<string>(bytes.Length / 4);
        for (var at = 0; at + 4 <= bytes.Length; at += 4)
            if (c.ProtoPath(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at))) is { } path && path.StartsWith("research/", StringComparison.Ordinal))
                list.Add(path["research/".Length..]);
        return list;
    }

    /// <summary>Прогресс: пары «тема i32, доля f64».</summary>
    private static Dictionary<string, double> Progress(ClientContext c, byte[] bytes)
    {
        var map = new Dictionary<string, double>();
        for (var at = 0; at + 12 <= bytes.Length; at += 12)
            if (c.ProtoPath(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at))) is { } path && path.StartsWith("research/", StringComparison.Ordinal))
                map[path["research/".Length..]] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(at + 4));
        return map;
    }
}
