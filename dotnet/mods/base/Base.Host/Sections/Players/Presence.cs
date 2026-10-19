using System.Buffers.Binary;
using Base.Host.Kernel;
using Base.Host.Sections.Energy;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Players;

/// <summary>
/// Кто в сети (sim.online в TS): Player.online по событиям движка player_online и player_left. С первого такого события
/// хост следит, кто в сети, — это помнит память мода под ключом <see cref="Income.Key"/> (ключ прежний: его читает доход
/// вне сети раздела Energy, <see cref="Income.Tracked"/>). Пока хост ни разу не сказал, кто в сети (сценарии, тесты), в
/// сети все.
/// </summary>
internal static class Presence
{
    public static void OnOnline(EventContext ctx) => Mark(ctx, true);

    public static void OnLeft(EventContext ctx) => Mark(ctx, false);

    private static void Mark(EventContext ctx, bool online)
    {
        if (ctx.Payload.Length < 4)
            return;
        var player = BinaryPrimitives.ReadUInt32LittleEndian(ctx.Payload);
        var call = Call.Of(ctx);
        if (!Income.Tracked(call))
            Memory.Of(call).Put(call, Income.Key, [1]);
        var account = Accounts.Ensure(call, player);
        if (account != 0)
            call.Set(account, S.Player).Flag(P.PlayerOnline, online);
    }
}
