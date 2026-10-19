using System.Buffers.Binary;
using Base.Host.Kernel;
using Base.Shared.Rules;

namespace Base.Host.Sections.Research;

/// <summary>
/// Какую тему лаборатории игрока ведут сейчас (pickTopic, currentTopic, researchNeeds). Головная тема плана уступает,
/// только простояв без сырья <see cref="ResearchRules.StallSeconds"/>: тогда берётся следующая открытая тема плана, у
/// которой сырьё под рукой. Сверяется раз в <see cref="ResearchRules.PickSeconds"/>; тему плана сменили — сразу.
/// <para>
/// Простой головной темы и выбор — в памяти мода (ключ <see cref="Key"/>): в TS это WeakMap, не мир; здесь память
/// переживает выброс экземпляра, так что CLR и wasm выбирают одинаково.
/// </para>
/// </summary>
internal static class Picks
{
    /// <summary>Ключ памяти раздела: на игрока — игрок u32, головная тема i32, простой с тика i64 (-1 — сырьё есть), сверено u64, выбор i32 (-1 — головная).</summary>
    public const string Key = "research.picks";

    private const int Width = 4 + 4 + 8 + 8 + 4;

    /// <summary>Простой и выбор игрока.</summary>
    public sealed record Stall(int Head, long Since, ulong Checked, int Pick);

    public static Dictionary<uint, Stall> Read(Call call)
    {
        var all = new Dictionary<uint, Stall>();
        var bytes = Memory.Of(call).Get(Key);
        for (var at = 0; bytes is not null && at + Width <= bytes.Length; at += Width)
        {
            var span = bytes.AsSpan(at);
            all[BinaryPrimitives.ReadUInt32LittleEndian(span)] = new Stall(
                BinaryPrimitives.ReadInt32LittleEndian(span[4..]),
                BinaryPrimitives.ReadInt64LittleEndian(span[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(span[16..]),
                BinaryPrimitives.ReadInt32LittleEndian(span[24..]));
        }
        return all;
    }

    private static void Write(Call call, Dictionary<uint, Stall> all)
    {
        var bytes = new byte[all.Count * Width];
        var at = 0;
        foreach (var (player, stall) in all.OrderBy(pair => pair.Key))
        {
            var span = bytes.AsSpan(at);
            BinaryPrimitives.WriteUInt32LittleEndian(span, player);
            BinaryPrimitives.WriteInt32LittleEndian(span[4..], stall.Head);
            BinaryPrimitives.WriteInt64LittleEndian(span[8..], stall.Since);
            BinaryPrimitives.WriteUInt64LittleEndian(span[16..], stall.Checked);
            BinaryPrimitives.WriteInt32LittleEndian(span[24..], stall.Pick);
            at += Width;
        }
        var memory = Memory.Of(call);
        if (!bytes.AsSpan().SequenceEqual(memory.Get(Key) ?? []))
            memory.Put(call, Key, bytes);
    }

    /// <summary>Выбор лабораторий игрока вместо головной темы; нет — null.</summary>
    private static string? PickOf(Call call, uint player) =>
        Read(call).TryGetValue(player, out var stall) && stall.Pick >= 0 ? Core.ProtoName(stall.Pick, "research") : null;

    /// <summary>
    /// Тема, которую лаборатории игрока ведут сейчас (currentTopic): головная тема плана или та, что её подменяет;
    /// null — вести нечего. Только читает — выбор делает <see cref="Pick"/>.
    /// </summary>
    public static string? Current(Call call, uint player)
    {
        var account = Accounts.Find(call, player);
        return account == 0 ? null : ResearchRules.Current(Knowledge.Open(call, account), PickOf(call, player));
    }

    /// <summary>
    /// Выбирает тему, которую лаборатории игрока (labs — его готовые невыключенные лаборатории) ведут сейчас, и
    /// запоминает выбор (pickTopic).
    /// </summary>
    public static string? Pick(Call call, uint player, uint account, IReadOnlyList<uint> labs)
    {
        var open = Knowledge.Open(call, account);
        var all = Read(call);
        if (open.Count == 0)
        {
            if (all.Remove(player))
                Write(call, all);
            return null;
        }
        var head = Knowledge.Id(open[0]);
        var tick = call.Tick;
        var found = all.TryGetValue(player, out var last);
        var same = found && last!.Head == head;
        var pick = found && last!.Pick >= 0 ? Core.ProtoName(last.Pick, "research") : null;
        if (same && tick >= last!.Checked && (tick - last.Checked) * Core.Step < ResearchRules.PickSeconds)
            return ResearchRules.Current(open, pick);
        // Сверка — по сети до этого вызова плюс записи вызова; выбор прошлой сверки — для нужд лабораторий в ней.
        var since = Supplied(call, open[0], labs) ? -1 : same && last!.Since >= 0 && (ulong)last.Since <= tick ? last.Since : (long)tick;
        var stalled = since >= 0 && (tick - (ulong)since) * Core.Step >= ResearchRules.StallSeconds;
        pick = stalled ? open.Skip(1).FirstOrDefault(topic => Supplied(call, topic, labs)) : null;
        all[player] = new Stall(head, since, tick, pick is null ? -1 : Knowledge.Id(pick));
        Write(call, all);
        return ResearchRules.Current(open, pick);
    }

    /// <summary>Есть ли хоть у одной из лабораторий всё сырьё темы: на её складе или в её сети труб (supplied).</summary>
    private static bool Supplied(Call call, string topic, IReadOnlyList<uint> labs)
    {
        var goods = ResearchRules.Price(Knowledge.Topics, topic, Core.Rules).Materials.Select(item => item.Good).ToList();
        if (goods.Count == 0)
            return true;
        var networks = new Networks(call);
        return labs.Any(lab => goods.All(good => Mining.Stocks.Amount(call, lab, good) + networks.Stock(lab, good) > ResearchRules.Epsilon));
    }

    /// <summary>
    /// Сколько чего ещё потратят до конца нынешняя тема игрока и головная тема плана, если ведут не её: цена за вычетом
    /// сделанного (researchNeeds). Пусто — темы нет.
    /// </summary>
    public static List<(string Good, double Amount)> Needs(Call call, uint player)
    {
        var needs = new List<(string Good, double Amount)>();
        var account = Accounts.Find(call, player);
        if (account == 0)
            return needs;
        var open = Knowledge.Open(call, account);
        if (ResearchRules.Current(open, PickOf(call, player)) is not { } current)
            return needs;
        var progress = Knowledge.Progress(call, account);
        foreach (var topic in new[] { open[0], current }.Distinct())
        {
            var left = 1 - progress.FirstOrDefault(pair => pair.Topic == topic).Done;
            foreach (var (good, amount) in ResearchRules.Price(Knowledge.Topics, topic, Core.Rules).Materials)
            {
                var at = needs.FindIndex(need => need.Good == good);
                if (at >= 0)
                    needs[at] = (good, needs[at].Amount + amount * left);
                else
                    needs.Add((good, amount * left));
            }
        }
        return needs;
    }
}
