using System.Buffers.Binary;
using Kharos.Mod.Sdk;

namespace Base.Host;

/// <summary>
/// mechanics/players + lib/wallet: игроки и их кредиты. Игрок — сущность без вида с компонентами <c>Player</c> и <c>Owner</c> (сеть шлёт её только хозяину); её
/// номер хранится в памяти мода (<c>ctx.storage.players</c> пилота): пары «игрок:u32, сущность:u32» подряд.
/// Вошедшему — кошелёк со стартовыми кредитами партии (<c>credits</c> в data/games.json), дальше — доход системой
/// <c>mechanics/players.income</c> раз в секунду.
/// </summary>
internal sealed class Players(BaseMod mod) : ISystem
{
    private Column _credits;

    /// <summary>player_joined { player }: кошелёк игроку, если ещё нет.</summary>
    public void OnJoined(EventContext ctx)
    {
        var player = BinaryPrimitives.ReadUInt32LittleEndian(ctx.Payload);
        var list = Tools.ReadStorage(out _);
        // Хост без памяти мода: держать кошелёк негде.
        if (list is null || Wallet.Find(list, player) is not null)
            return;
        var p = mod.P;
        var credits = mod.D.Games.TryGetValue(mod.Game, out var game) ? game.Credits : 0;
        var entity = ctx.Commands.Spawn();
        ctx.Commands.Add(entity, p.Player).Number(p.Player["id"], player).Number(p.Player["credits"], credits);
        // Владелец — сам игрок: счёт получает только он (чужие кредиты другим не уходят, VisionView).
        ctx.Commands.Add(entity, p.Owner).Number(p.Owner["player"], player);
        var next = new byte[list.Length + 8];
        list.CopyTo(next, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(next.AsSpan(list.Length), player);
        BinaryPrimitives.WriteUInt32LittleEndian(next.AsSpan(list.Length + 4), entity);
        Tools.WriteStorage(ctx.Commands, next);
    }

    public void Bind(SystemBinding binding) => _credits = binding.Column("Player", "credits");

    /// <summary>Доход: раз в 20 тиков каждому игроку income × dt × 20 (кредитов в секунду × секунд).</summary>
    public void Run(SystemContext ctx)
    {
        var credits = ctx.Column(_credits);
        var gain = mod.D.Income * ctx.Dt * 20;
        for (var i = 0; i < ctx.Count; i++)
            credits[i] += gain;
    }
}

/// <summary>Кошельки игроков (lib/wallet.luau): списание и возврат кредитов командами.</summary>
internal static class Wallet
{
    /// <summary>Сущность игрока в списке памяти мода; нет — null.</summary>
    internal static uint? Find(byte[] list, uint player)
    {
        for (var at = 0; at + 8 <= list.Length; at += 8)
            if (BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(at)) == player)
                return BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(at + 4));
        return null;
    }

    /// <summary>Сущность игрока по номеру; нет — null.</summary>
    public static uint? Of(uint player) => Tools.ReadStorage(out _) is { } list ? Find(list, player) : null;

    /// <summary>Кредиты игрока сейчас.</summary>
    public static double Credits(Parts p, uint player) =>
        Of(player) is { } entity && Tools.Get(entity, p.Player) is { } state ? state.Number(p.Player["credits"]) : 0;

    /// <summary>Списывает amount кредитов, если хватает: true — списано.</summary>
    public static bool Pay(Parts p, CommandWriter commands, uint player, double amount)
    {
        if (Of(player) is not { } entity || Tools.Get(entity, p.Player) is not { } state)
            return false;
        var credits = state.Number(p.Player["credits"]);
        if (credits < amount)
            return false;
        commands.Set(entity, p.Player).Number(p.Player["credits"], credits - amount);
        return true;
    }

    public static void Refund(Parts p, CommandWriter commands, uint player, double amount)
    {
        if (Of(player) is { } entity && Tools.Get(entity, p.Player) is { } state)
            commands.Set(entity, p.Player).Number(p.Player["credits"], state.Number(p.Player["credits"]) + amount);
    }
}
