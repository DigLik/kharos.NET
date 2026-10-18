using System.Buffers.Binary;
using Kharos.Mod.Sdk;

namespace Base.Host.Kernel;

/// <summary>
/// Счета игроков (economy.ts): сущность счёта — Player, Owner, Team, Science, Diplomacy (schema/players.json); какая
/// она у игрока, помнит <see cref="Memory"/>. Кредиты целые, доли дохода копятся в Player.earned. Награды
/// (rewards.json) — номера Proto «rewards/ключ» i32 подряд в Player.rewards; открытые темы — номера Proto
/// «research/тема» i32 подряд в Science.done (формат общий с разделом Research).
/// </summary>
internal static class Accounts
{
    /// <summary>Сущность счёта игрока; нет — 0.</summary>
    public static uint Find(Call call, uint player)
    {
        if (player == 0)
            return 0;
        var memory = Memory.Of(call);
        var at = Array.IndexOf(memory.Players, player);
        return at >= 0 && call.Alive(memory.Accounts[at]) ? memory.Accounts[at] : 0;
    }

    /// <summary>Игроки со счетами, по порядку заведения.</summary>
    public static IReadOnlyList<uint> Players(Call call) => Memory.Of(call).Players;

    /// <summary>Сущность счёта игрока; нет — заводит (игрок появляется в мире, как в addCredits).</summary>
    public static uint Ensure(Call call, uint player)
    {
        if (player == 0)
            return 0;
        var found = Find(call, player);
        if (found != 0 || !call.Writes)
            return found;
        var entity = call.Spawn();
        call.Add(entity, S.Player).Number(S.PlayerId, player);
        call.Add(entity, S.Owner).Number(S.OwnerPlayer, player);
        call.Add(entity, S.Team).Number(S.TeamPlayer, player).Number(S.TeamHead, -1);
        call.Add(entity, S.Science);
        call.Add(entity, S.Diplomacy);
        var memory = Memory.Of(call);
        var at = Array.IndexOf(memory.Players, player);
        if (at >= 0)
            memory.Accounts[at] = entity;
        else
        {
            memory.Players = [.. memory.Players, player];
            memory.Accounts = [.. memory.Accounts, entity];
        }
        memory.Save(call);
        return entity;
    }

    /// <summary>Кредиты игрока; неизвестного — 0.</summary>
    public static double Credits(Call call, uint player) => call.Num(Find(call, player), S.Player, S.PlayerCredits);

    /// <summary>Начисляет кредиты (addCredits); игрок без счёта при этом появляется.</summary>
    public static void Add(Call call, uint player, double amount)
    {
        var entity = Ensure(call, player);
        if (entity != 0)
            call.Set(entity, S.Player).Number(S.PlayerCredits, call.Num(entity, S.Player, S.PlayerCredits) + amount);
    }

    /// <summary>Ровно столько кредитов (setCredits редактора): целое не меньше нуля.</summary>
    public static void SetCredits(Call call, uint player, double credits) =>
        Add(call, player, Math.Max(0, Math.Floor(credits + 0.5)) - Credits(call, player));

    /// <summary>Списывает кредиты, если хватает (pay).</summary>
    public static bool Pay(Call call, uint player, double amount)
    {
        var entity = Find(call, player);
        if (entity == 0)
            return false;
        var credits = call.Num(entity, S.Player, S.PlayerCredits);
        if (credits < amount)
            return false;
        call.Set(entity, S.Player).Number(S.PlayerCredits, credits - amount);
        return true;
    }

    /// <summary>Начисляет сумму с дробной частью: доли копятся в earned, на счёт — целые кредиты (earn).</summary>
    public static void Earn(Call call, uint player, double amount)
    {
        var entity = Find(call, player);
        if (entity == 0 || amount <= 0)
            return;
        var total = call.Num(entity, S.Player, S.PlayerEarned) + amount;
        var whole = Math.Floor(total);
        call.Set(entity, S.Player).Number(S.PlayerCredits, call.Num(entity, S.Player, S.PlayerCredits) + whole).Number(S.PlayerEarned, total - whole);
    }

    /// <summary>Списывает сумму с дробной частью, если хватает: доли — из earned, недостающее — разменом кредита (spend).</summary>
    public static bool Spend(Call call, uint player, double amount)
    {
        var entity = Find(call, player);
        if (entity == 0)
            return false;
        var credits = call.Num(entity, S.Player, S.PlayerCredits);
        var left = call.Num(entity, S.Player, S.PlayerEarned) - amount;
        var whole = left < 0 ? Math.Ceiling(-left) : 0;
        if (credits < whole)
            return false;
        call.Set(entity, S.Player).Number(S.PlayerCredits, credits - whole).Number(S.PlayerEarned, left + whole);
        return true;
    }

    /// <summary>Награды игрока (ключи rewards.json) по порядку получения.</summary>
    public static List<string> Rewards(Call call, uint player)
    {
        var list = new List<string>();
        foreach (var id in Ids(call.Bytes(Find(call, player), S.Player, S.PlayerRewards)))
            if (Core.ProtoName(id, "rewards") is { } key)
                list.Add(key);
        return list;
    }

    /// <summary>Выдаёт награду, если игрок её ещё не получал (reward).</summary>
    public static void Reward(Call call, uint player, string key)
    {
        var entity = Find(call, player);
        if (entity == 0 || !Core.Tables.Rewards.TryGetValue(key, out var spec))
            return;
        var id = Core.Proto("rewards", key);
        var have = Ids(call.Bytes(entity, S.Player, S.PlayerRewards));
        if (id < 0 || have.Contains(id))
            return;
        call.Set(entity, S.Player)
            .Number(S.PlayerCredits, call.Num(entity, S.Player, S.PlayerCredits) + spec.Credits)
            .Bytes(S.PlayerRewards, Pack([.. have, id]));
    }

    /// <summary>Открыта ли игроку тема (hasResearch): без дерева технологий и без темы — да.</summary>
    public static bool Researched(Call call, uint player, string? topic)
    {
        if (topic is null || !Core.Rules.TechTree)
            return true;
        var id = Core.Proto("research", topic);
        return id >= 0 && Ids(call.Bytes(Find(call, player), S.Science, S.ScienceDone)).Contains(id);
    }

    /// <summary>Защита новичка на rules.protection минут (protect в shelter.ts).</summary>
    public static void Protect(Call call, uint player)
    {
        var entity = Find(call, player);
        if (entity == 0)
            return;
        var ticks = Core.Ticks(Core.Rules.Protection * 60);
        call.Set(entity, S.Player).Number(S.PlayerSafe, ticks > 0 ? call.Tick + (ulong)ticks : 0);
    }

    /// <summary>Номера i32 подряд.</summary>
    public static List<int> Ids(byte[]? bytes)
    {
        var list = new List<int>();
        if (bytes is null)
            return list;
        for (var at = 0; at + 4 <= bytes.Length; at += 4)
            list.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at)));
        return list;
    }

    /// <summary>Номера i32 подряд — в байты.</summary>
    public static byte[] Pack(IReadOnlyList<int> ids)
    {
        var bytes = new byte[ids.Count * 4];
        for (var i = 0; i < ids.Count; i++)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), ids[i]);
        return bytes;
    }
}
