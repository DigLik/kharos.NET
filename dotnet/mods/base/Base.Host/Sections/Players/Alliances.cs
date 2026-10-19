using System.Buffers.Binary;
using Base.Host.Kernel;
using Base.Shared.Signals;

namespace Base.Host.Sections.Players;

/// <summary>
/// Союзы игроков (allies.ts). Союз — группа во главе с игроком: глава его предлагает и принимает, он же исключает рядовых и
/// распускает союз целиком; рядовой может только выйти сам. Кто предложил — тот и глава: у двух одиночек главой
/// становится предложивший, а входящий в готовую группу ей и подчиняется.
/// <para>
/// Группа — одинаковый Team.head ≥ 0 у счетов участников (так союз читают движок — обзор и сеть — и разделы: бой, ворота,
/// торговля); −1 — ни с кем. Список союзников в TS (Player.allies) у нас выводится из head: у всякого участника группы
/// в allies все остальные её участники по возрастанию номера — ровно то, что держит TS. Предложения — Diplomacy:
/// offered (кому игрок предложил) и asked (кто предложил ему), игроки u32 подряд.
/// </para>
/// </summary>
internal static class Alliances
{
    /// <summary>Чем кончилось предложение союза (AllyResult).</summary>
    public enum Offer
    {
        Allied,
        Offered,
        Already,
        Pending,
        Refused,
        FromMember,
        ToMember,
    }

    /// <summary>Чем кончился выход (ExitResult).</summary>
    public enum Exit
    {
        Left,
        Dissolved,
        Nothing,
    }

    /// <summary>Чем кончился разрыв (BreakResult).</summary>
    public enum Break
    {
        Kicked,
        Withdrawn,
        Declined,
        NotHead,
        Nothing,
    }

    // ===== Чтение =====

    /// <summary>Номер главы союза игрока: он сам, его глава или −1 (allyHeadOf).</summary>
    public static long HeadOf(Call call, uint player)
    {
        var account = Accounts.Find(call, player);
        return account == 0 ? -1 : (long)call.Num(account, S.Team, S.TeamHead, -1);
    }

    /// <summary>С кем у игрока союз, по возрастанию номера (alliesOf).</summary>
    public static List<uint> AlliesOf(Call call, uint player)
    {
        var list = new List<uint>();
        var head = HeadOf(call, player);
        if (head < 0)
            return list;
        foreach (var other in Accounts.Players(call))
            if (other != player && HeadOf(call, other) == head)
                list.Add(other);
        list.Sort();
        return list;
    }

    /// <summary>В союзе ли два игрока (allied).</summary>
    public static bool Allied(Call call, uint a, uint b) => a != b && HeadOf(call, a) is var head && head >= 0 && head == HeadOf(call, b);

    /// <summary>Кому игрок предложил союз (offered).</summary>
    public static List<uint> Offered(Call call, uint player) => Read(call.Bytes(Accounts.Find(call, player), S.Diplomacy, P.DiplomacyOffered));

    /// <summary>Кто предложил союз игроку (asked).</summary>
    public static List<uint> Asked(Call call, uint player) => Read(call.Bytes(Accounts.Find(call, player), S.Diplomacy, P.DiplomacyAsked));

    // ===== Предложение и согласие =====

    /// <summary>
    /// Игрок from предлагает союз игроку to; если тот уже предлагал ему — это согласие, и союз заключён (offerAlliance).
    /// Предлагают и принимают только одиночки и главы.
    /// </summary>
    public static Offer Propose(Call call, uint from, uint to)
    {
        if (from == to || Accounts.Find(call, from) == 0 || Accounts.Find(call, to) == 0)
            return Offer.Refused;
        if (Allied(call, from, to))
            return Offer.Already;
        var head = HeadOf(call, from);
        if (head >= 0 && head != from)
            return Offer.FromMember;
        if (Asked(call, from).Contains(to))
        {
            Merge(call, from, to);
            return Offer.Allied;
        }
        var theirs = HeadOf(call, to);
        if (theirs >= 0 && theirs != to)
            return Offer.ToMember;
        var offered = Offered(call, from);
        if (offered.Contains(to))
            return Offer.Pending;
        SetOffered(call, from, [.. offered, to]);
        SetAsked(call, to, [.. Asked(call, to), from]);
        return Offer.Offered;
    }

    /// <summary>
    /// Союз заключён (merge): группы from и to сливаются. Главой остаётся глава принявшей стороны — у двух одиночек это
    /// предложивший, у готовой группы её глава. Предложения внутрь группы из списков уходят.
    /// </summary>
    private static void Merge(Call call, uint from, uint to)
    {
        var (mine, theirs) = (AlliesOf(call, from), AlliesOf(call, to));
        var group = new SortedSet<uint>([from, to, .. mine, .. theirs]);
        var head = theirs.Count > 0 ? HeadOf(call, to) : mine.Count > 0 ? HeadOf(call, from) : to;
        foreach (var member in group)
        {
            var account = Accounts.Find(call, member);
            if (account == 0)
                continue;
            call.Set(account, S.Team).Number(S.TeamHead, head);
            SetOffered(call, member, [.. Offered(call, member).Where(other => !group.Contains(other))]);
            SetAsked(call, member, [.. Asked(call, member).Where(other => !group.Contains(other))]);
        }
    }

    /// <summary>
    /// Рядовой (не глава) покидает группу (detach): у оставшихся связи друг с другом целы, а оставшемуся одному глава больше
    /// не нужен — он снова одиночка.
    /// </summary>
    private static void Detach(Call call, uint member)
    {
        var rest = AlliesOf(call, member);
        SetHead(call, member, -1);
        if (rest.Count == 1)
            SetHead(call, rest[0], -1);
    }

    /// <summary>Игрок покидает союз сам (exitAlliance): рядовой выходит, глава распускает союз.</summary>
    public static Exit Leave(Call call, uint player)
    {
        if (Accounts.Find(call, player) == 0)
            return Exit.Nothing;
        var allies = AlliesOf(call, player);
        if (allies.Count == 0)
            return Exit.Nothing;
        if (HeadOf(call, player) == player)
        {
            foreach (var member in (uint[])[player, .. allies])
                SetHead(call, member, -1);
            return Exit.Dissolved;
        }
        Detach(call, player);
        return Exit.Left;
    }

    /// <summary>
    /// Игрок from рвёт с игроком to всё, что между ними есть (breakAlliance): союз (только глава — исключает рядового),
    /// своё предложение или его предложение.
    /// </summary>
    public static Break Sever(Call call, uint from, uint to)
    {
        if (Accounts.Find(call, from) == 0)
            return Break.Nothing;
        if (Allied(call, from, to))
        {
            if (HeadOf(call, from) != from)
                return Break.NotHead;
            Detach(call, to);
            return Break.Kicked;
        }
        var result = Offered(call, from).Contains(to) ? Break.Withdrawn : Asked(call, from).Contains(to) ? Break.Declined : Break.Nothing;
        if (result == Break.Nothing)
            return result;
        foreach (var (a, b) in (ReadOnlySpan<(uint, uint)>)[(from, to), (to, from)])
        {
            if (Accounts.Find(call, a) == 0)
                continue;
            SetOffered(call, a, [.. Offered(call, a).Where(other => other != b)]);
            SetAsked(call, a, [.. Asked(call, a).Where(other => other != b)]);
        }
        return result;
    }

    /// <summary>Игрок начинает заново (forgetAlliances): все его союзы и предложения пропадают у обеих сторон.</summary>
    public static void Forget(Call call, uint player)
    {
        if (Accounts.Find(call, player) == 0)
            return;
        var others = Offered(call, player).Concat(Asked(call, player)).Distinct().ToList();
        Leave(call, player);
        foreach (var other in others)
            Sever(call, player, other);
    }

    // ===== Приказы ally, breakAlly, exitAlly (команды /ally и /unally хоста TS) =====

    /// <summary>Приказ ally: предложить союз или принять предложение; вести — как в /ally.</summary>
    public static bool OrderAlly(Call call, uint from, uint to)
    {
        var result = Propose(call, from, to);
        switch (result)
        {
            case Offer.Refused:
                Tell(call, from, from == to ? "allySelf" : "noPlayer", to);
                return false;
            case Offer.FromMember:
                Tell(call, from, "allyNotHead", 0);
                return false;
            case Offer.ToMember:
                Tell(call, from, "allyToMember", to);
                return false;
            case Offer.Already:
                Tell(call, from, "allyAlready", to);
                return false;
            case Offer.Pending:
                Tell(call, from, "allyPending", to);
                return false;
        }
        var allied = result == Offer.Allied;
        Tell(call, from, allied ? "allyFormed" : "allyOffered", to);
        Tell(call, to, allied ? "allyFormed" : "allyAsked", from);
        return true;
    }

    /// <summary>Приказ exitAlly: выйти из союза самому (/unally без имени).</summary>
    public static bool OrderExit(Call call, uint from)
    {
        var group = AlliesOf(call, from);
        var result = Leave(call, from);
        if (result == Exit.Nothing)
        {
            Tell(call, from, "allyNoAlliance", 0);
            return false;
        }
        var left = result == Exit.Left;
        Tell(call, from, left ? "allyExited" : "allyDissolved", 0);
        foreach (var member in group)
            Tell(call, member, left ? "allyLeftBy" : "allyDissolvedBy", from);
        return true;
    }

    /// <summary>Приказ breakAlly: исключить рядового, отозвать своё предложение или отклонить чужое (/unally имя).</summary>
    public static bool OrderBreak(Call call, uint from, uint to)
    {
        // По своему имени — выйти самому, как /unally без имени.
        if (to == from)
            return OrderExit(call, from);
        var group = new List<uint> { from };
        group.AddRange(AlliesOf(call, from));
        group.Remove(to);
        var result = Sever(call, from, to);
        switch (result)
        {
            case Break.Nothing:
                Tell(call, from, "allyNothing", to);
                return false;
            case Break.NotHead:
                Tell(call, from, "allyKickNotHead", 0);
                return false;
            case Break.Kicked:
                foreach (var member in group)
                    Tell(call, member, "allyKicked", to);
                Tell(call, to, "allyKickedBy", from);
                return true;
        }
        Tell(call, from, result == Break.Withdrawn ? "allyWithdrawn" : "allyDeclined", to);
        if (result != Break.Withdrawn)
            Tell(call, to, "allyDeclinedBy", from);
        return true;
    }

    private static void Tell(Call call, uint to, string kind, uint about)
    {
        if (call.Writes && to != 0)
            call.SignalTo(AllySignal.Name, new AllySignal { Kind = kind, Player = about }, to);
    }

    // ===== Запись =====

    private static void SetHead(Call call, uint player, long head)
    {
        var account = Accounts.Find(call, player);
        if (account != 0)
            call.Set(account, S.Team).Number(S.TeamHead, head);
    }

    private static void SetOffered(Call call, uint player, List<uint> list) => Write(call, player, list, P.DiplomacyOffered);

    private static void SetAsked(Call call, uint player, List<uint> list) => Write(call, player, list, P.DiplomacyAsked);

    private static void Write(Call call, uint player, List<uint> list, Kharos.Mod.Sdk.FieldRef field)
    {
        var account = Accounts.Find(call, player);
        if (account == 0)
            return;
        var bytes = new byte[list.Count * 4];
        for (var i = 0; i < list.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), list[i]);
        if (!call.Has(account, S.Diplomacy))
            call.Add(account, S.Diplomacy);
        call.Set(account, S.Diplomacy).Bytes(field, bytes);
    }

    private static List<uint> Read(byte[]? bytes)
    {
        var list = new List<uint>();
        if (bytes is null)
            return list;
        for (var at = 0; at + 4 <= bytes.Length; at += 4)
            list.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)));
        return list;
    }
}
