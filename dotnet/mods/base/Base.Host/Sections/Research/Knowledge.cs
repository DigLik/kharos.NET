using System.Buffers.Binary;
using Base.Host.Kernel;
using Base.Shared.Data;
using Base.Shared.Rules;

namespace Base.Host.Sections.Research;

/// <summary>
/// Наука игрока на его сущности счёта (Science; Player.research, plan и progress в TS): открытые темы (done) и план
/// (plan) — номера Proto «research/тема» i32 подряд (формат общий с <see cref="Accounts.Researched"/>); прогресс начатых
/// тем — пары «тема i32, доля f64» по порядку начала. Запись — командой вызова, следующее чтение в том же вызове её видит.
/// </summary>
internal static class Knowledge
{
    /// <summary>Темы research.json.</summary>
    public static IReadOnlyDictionary<string, TopicSpec> Topics => Core.Tables.Research;

    /// <summary>Номер вида темы; нет — -1.</summary>
    public static int Id(string topic) => Core.Proto("research", topic);

    /// <summary>Темы по номерам видов (неизвестные — пропускаются).</summary>
    public static List<string> Names(byte[]? bytes)
    {
        var list = new List<string>();
        foreach (var id in Accounts.Ids(bytes))
            if (Core.ProtoName(id, "research") is { } topic)
                list.Add(topic);
        return list;
    }

    /// <summary>Темы — в номера видов.</summary>
    public static byte[] Pack(IEnumerable<string> topics) => Accounts.Pack([.. topics.Select(Id).Where(id => id >= 0)]);

    /// <summary>Открытые темы игрока по порядку (researchOf).</summary>
    public static List<string> Done(Call call, uint account) => Names(call.Bytes(account, S.Science, S.ScienceDone));

    /// <summary>План игрока (planOf).</summary>
    public static List<string> Plan(Call call, uint account) => Names(call.Bytes(account, S.Science, R.SciencePlan));

    /// <summary>Прогресс начатых тем по порядку начала.</summary>
    public static List<(string Topic, double Done)> Progress(Call call, uint account)
    {
        var list = new List<(string, double)>();
        var bytes = call.Bytes(account, S.Science, R.ScienceProgress);
        for (var at = 0; bytes is not null && at + 12 <= bytes.Length; at += 12)
            if (Core.ProtoName(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at)), "research") is { } topic)
                list.Add((topic, BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(at + 4))));
        return list;
    }

    /// <summary>Сколько темы сделано, доля (progressOf).</summary>
    public static double ProgressOf(Call call, uint account, string topic)
    {
        foreach (var (name, done) in Progress(call, account))
            if (name == topic)
                return done;
        return 0;
    }

    public static void SetDone(Call call, uint account, IEnumerable<string> topics) => call.Set(account, S.Science).Bytes(S.ScienceDone, Pack(topics));

    public static void SetPlan(Call call, uint account, IEnumerable<string> topics) => call.Set(account, S.Science).Bytes(R.SciencePlan, Pack(topics));

    public static void SetProgress(Call call, uint account, IReadOnlyList<(string Topic, double Done)> progress)
    {
        var bytes = new byte[progress.Count * 12];
        var at = 0;
        foreach (var (topic, done) in progress)
        {
            if (Id(topic) is var id and >= 0)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), id);
                BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(at + 4), done);
                at += 12;
            }
        }
        call.Set(account, S.Science).Bytes(R.ScienceProgress, bytes[..at]);
    }

    /// <summary>Темы плана, которые можно вести сейчас, по порядку плана (openTopics).</summary>
    public static List<string> Open(Call call, uint account) => account == 0 ? [] : ResearchRules.Open(Topics, Plan(call, account), Done(call, account));

    /// <summary>
    /// Ставит тему в план вместе с неоткрытыми предшественниками (planResearch): в конец плана, front — в начало. Тему,
    /// которой не хватает неслучившегося открытия, не поставить.
    /// </summary>
    public static bool PlanResearch(Call call, uint player, string topic, bool front)
    {
        var account = Accounts.Find(call, player);
        if (!ResearchRules.IsTopic(Topics, topic) || account == 0)
            return false;
        var plan = Plan(call, account);
        var path = ResearchRules.PathTo(Topics, topic, Done(call, account));
        if (path.Count == 0 || path.Any(step => ResearchRules.IsTrigger(Topics, step)))
            return false;
        List<string> next = front ? [.. path, .. plan.Where(queued => !path.Contains(queued))] : [.. plan, .. path.Where(step => !plan.Contains(step))];
        if (next.SequenceEqual(plan))
            return false;
        SetPlan(call, account, next);
        return true;
    }

    /// <summary>Убирает тему из плана вместе с темами плана, которым она нужна (dequeueResearch).</summary>
    public static bool Dequeue(Call call, uint player, string topic)
    {
        var account = Accounts.Find(call, player);
        if (!ResearchRules.IsTopic(Topics, topic) || account == 0)
            return false;
        var plan = Plan(call, account);
        var next = plan.Where(queued => queued != topic && !ResearchRules.Under(Topics, queued).Contains(topic)).ToList();
        if (next.Count == plan.Count)
            return false;
        SetPlan(call, account, next);
        return true;
    }

    /// <summary>Очищает план (cancelResearch): начатые темы помнят прогресс.</summary>
    public static bool Cancel(Call call, uint player)
    {
        var account = Accounts.Find(call, player);
        if (account == 0 || Plan(call, account).Count == 0)
            return false;
        SetPlan(call, account, []);
        return true;
    }

    /// <summary>Добавляет игроку открытую тему, если её ещё нет (learn).</summary>
    public static void Learn(Call call, uint account, string topic)
    {
        var done = Done(call, account);
        if (!done.Contains(topic))
            SetDone(call, account, [.. done, topic]);
    }

    /// <summary>
    /// Первая тема плана, которую можно вести (nextTopic): открытые уже темы и темы без открытых предшественников план
    /// сбрасывает с головы. null — вести нечего.
    /// </summary>
    public static string? Next(Call call, uint account)
    {
        var plan = Plan(call, account);
        var done = Done(call, account);
        var skip = 0;
        while (skip < plan.Count && !ResearchRules.Ready(Topics, plan[skip], done))
            skip++;
        if (skip > 0)
            SetPlan(call, account, plan.Skip(skip));
        return skip < plan.Count ? plan[skip] : null;
    }

    /// <summary>Записывает прогресс темы; законченная переходит в открытые и уходит из плана (finish).</summary>
    public static void Finish(Call call, uint account, string topic, double done)
    {
        var progress = Progress(call, account);
        var at = progress.FindIndex(pair => pair.Topic == topic);
        if (done < 1 - ResearchRules.Epsilon)
        {
            if (at >= 0 && progress[at].Done == done)
                return;
            if (at >= 0)
                progress[at] = (topic, done);
            else
                progress.Add((topic, done));
            SetProgress(call, account, progress);
            return;
        }
        if (at >= 0)
            progress.RemoveAt(at);
        SetProgress(call, account, progress);
        SetDone(call, account, [.. Done(call, account), topic]);
        SetPlan(call, account, Plan(call, account).Where(next => next != topic));
    }
}
