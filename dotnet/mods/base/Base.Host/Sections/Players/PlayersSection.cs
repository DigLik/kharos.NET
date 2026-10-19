using System.Diagnostics.CodeAnalysis;
using Base.Host.Kernel;
using Base.Host.Sections.Combat;
using Base.Host.Sections.Kernel;
using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Players;

/// <summary>
/// Раздел Players (С9). Игроки: союзы (<see cref="Alliances"/>: приказы ally, breakAlly, exitAlly — команды /ally и
/// /unally хоста TS), поражение и сдача (<see cref="Defeat"/>: surrender, scuttle, событие player_respawn), сигналы
/// союзникам (<see cref="Pings"/>: ping), кто в сети (<see cref="Presence"/>: события player_online и player_left), вести о
/// поражении и защите новичка (<see cref="Watch"/>), тестовая карта партии-песочницы (<see cref="Sandbox"/>: крючок входа
/// ядра <see cref="Spawning.Sandbox"/>). Запросы players.*.
/// <para>
/// Защита новичка (shelter.ts) — Player.safe: ставит её ядро при входе (<see cref="Accounts.Protect"/>), читает и снимает
/// приказом атаки бой (<see cref="Attack"/>). Правки редактора edit.* — ядро (Kernel/Edits) и разделы-владельцы.
/// </para>
/// <para>
/// Системы раздела в TS — не системы sim.ts (их ведёт хост), поэтому в <see cref="Order.Tick"/> им места нет: они идут в
/// самом конце тика, после closeBooks.
/// </para>
/// </summary>
internal static class PlayersSection
{
    /// <summary>После какой системы тика идут системы раздела (последняя в Order.Tick).</summary>
    private const string Last = "closeBooks";

    public static void Setup(ModBuilder mod)
    {
        Commands.Handle<AllyOrder>(AllyOrder.Name, (call, player, order) => Alliances.OrderAlly(call, player, order.To));
        Commands.Handle<BreakAllyOrder>(BreakAllyOrder.Name, (call, player, order) => Alliances.OrderBreak(call, player, order.To));
        Commands.Handle<ExitAllyOrder>(ExitAllyOrder.Name, (call, player, _) => Alliances.OrderExit(call, player));
        Commands.Handle<SurrenderOrder>(SurrenderOrder.Name, (call, player, _) => Defeat.Surrender(call, player));
        Commands.Handle<ScuttleOrder>(ScuttleOrder.Name, (call, player, order) => Defeat.Scuttle(call, player, order.Units));
        Commands.Handle<PingOrder>(PingOrder.Name, (call, player, order) => Pings.Ping(call, player, order.X, order.Y));
        mod.On("player_online", Presence.OnOnline);
        mod.On("player_left", Presence.OnLeft);
        mod.On("player_respawn", Defeat.OnRespawn);
        Chat.Setup(mod);
        Spawning.Sandbox = Sandbox.Spawn;
        // Здание сменило хозяина в редакторе — сети пересчитываются (resetZones в setOwner).
        Edits.OwnersChanged = Construction.Zones.Reset;

        Watch.Declare(mod.System("players/watch", new Watch()).Key("players.watch")).After(Last);
        Pings.Expire.Declare(mod.System("players/pings", new Pings.Expire()).Key("players.pings")).After("players.watch");
        mod.System("players/sandbox", new Sandbox.Connect()).Key("players.sandbox").After("players.pings");
        Queries(mod);
    }

    private static void Queries(ModBuilder mod)
    {
        mod.Query(PlayersQueries.Team, ctx =>
        {
            var player = Args<PlayerArgs>(ctx).Player;
            var call = Call.Of(ctx);
            ctx.Reply(new TeamInfo
            {
                Allies = [.. Alliances.AlliesOf(call, player)],
                Head = Alliances.HeadOf(call, player),
                Offered = [.. Alliances.Offered(call, player)],
                Asked = [.. Alliances.Asked(call, player)],
            });
        });
        mod.Query(PlayersQueries.Defeated, ctx => ctx.Reply(Defeat.IsDefeated(Call.Of(ctx), Args<PlayerArgs>(ctx).Player)));
        mod.Query(PlayersQueries.ProtectionLeft, ctx => ctx.Reply(Attack.ProtectionLeft(Call.Of(ctx), Args<PlayerArgs>(ctx).Player)));
        mod.Query(PlayersQueries.Sheltered, ctx =>
        {
            var call = Call.Of(ctx);
            ctx.Reply(new PlayerList { Items = [.. Accounts.Players(call).Where(player => Attack.ProtectionLeft(call, player) > 0).Order()] });
        });
        mod.Query(PlayersQueries.All, ctx => ctx.Reply(new PlayerList { Items = [.. Accounts.Players(Call.Of(ctx))] }));
    }

    private static T Args<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(QueryContext ctx) where T : class, new() => ctx.Read<T>() ?? new T();
}
