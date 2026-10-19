using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Base.Shared.Signals;
using MemoryPack;

namespace Kharos.Base.Scenario;

// Раздел игроков (С9): союзы, поражение, защита новичка, сигналы. Приказы идут «сразу» (Flush, без тика), как прямые
// вызовы offerAlliance, breakAlliance, exitAlliance, protect в тестах TS; итог — по вести ally, что пришла игроку.
public sealed partial class Sim
{
    /// <summary>Приказ игрока сразу (без тика) и вести ally, что пришли в ответ (кому, ключ, о ком).</summary>
    private List<(uint To, string Kind, uint About)> Diplomacy<T>(uint player, T order) where T : class
    {
        var before = Signals.Count;
        Send(player, order);
        Flush();
        return [.. Signals.Skip(before).Where(signal => signal.Name == AllySignal.Name)
            .Select(signal => (signal.To is [var to, ..] ? to : 0u, MemoryPackSerializer.Deserialize<AllySignal>(signal.Payload)!.Kind,
                MemoryPackSerializer.Deserialize<AllySignal>(signal.Payload)!.Player))];
    }

    /// <summary>Что сказали самому игроку: первая весть ему.</summary>
    private static string Reply(List<(uint To, string Kind, uint About)> heard, uint player) =>
        heard.FirstOrDefault(item => item.To == player).Kind ?? "";

    /// <summary>
    /// Игрок from предлагает союз игроку to (offerAlliance), итог — как в TS: allied, offered, already, pending, refused,
    /// fromMember, toMember.
    /// </summary>
    public string Offer(uint from, uint to) => Reply(Diplomacy(from, new AllyOrder { To = to }), from) switch
    {
        "allyFormed" => "allied",
        "allyOffered" => "offered",
        "allyAlready" => "already",
        "allyPending" => "pending",
        "allyNotHead" => "fromMember",
        "allyToMember" => "toMember",
        _ => "refused",
    };

    /// <summary>Игрок from рвёт с to (breakAlliance): kicked, withdrawn, declined, notHead, nothing.</summary>
    public string BreakAlly(uint from, uint to) => Reply(Diplomacy(from, new BreakAllyOrder { To = to }), from) switch
    {
        "allyKicked" => "kicked",
        "allyWithdrawn" => "withdrawn",
        "allyDeclined" => "declined",
        "allyKickNotHead" => "notHead",
        _ => "nothing",
    };

    /// <summary>Игрок выходит из союза сам (exitAlliance): left, dissolved, nothing.</summary>
    public string ExitAlly(uint player) => Reply(Diplomacy(player, new ExitAllyOrder()), player) switch
    {
        "allyExited" => "left",
        "allyDissolved" => "dissolved",
        _ => "nothing",
    };

    /// <summary>Приказ союза и все вести ally в ответ (кому, ключ сообщения, о ком): как строки чата /ally и /unally.</summary>
    public List<(uint To, string Kind, uint About)> Tells<T>(uint player, T order) where T : class => Diplomacy(player, order);

    /// <summary>Союз игрока: союзники, глава, предложения (players.team).</summary>
    public TeamInfo TeamOf(uint player) => Query<TeamInfo>(PlayersQueries.Team, new PlayerArgs { Player = player });

    /// <summary>С кем у игрока союз, по возрастанию (alliesOf).</summary>
    public uint[] AlliesOf(uint player) => TeamOf(player).Allies;

    /// <summary>Глава союза игрока; −1 — ни с кем (allyHeadOf).</summary>
    public long HeadOf(uint player) => TeamOf(player).Head;

    /// <summary>В союзе ли игроки (allied).</summary>
    public bool Allied(uint a, uint b) => a != b && AlliesOf(a).Contains(b);

    /// <summary>Проиграл ли игрок (isDefeated).</summary>
    public bool IsDefeated(uint player) => Query<bool>(PlayersQueries.Defeated, new PlayerArgs { Player = player });

    /// <summary>Сколько тиков защиты новичка осталось (protectionLeft).</summary>
    public double ProtectionLeft(uint player) => Query<double>(PlayersQueries.ProtectionLeft, new PlayerArgs { Player = player });

    /// <summary>
    /// Защита новичка на minutes минут от нынешнего тика (protect с rules.protection = minutes): Player.safe — прямо в мир;
    /// без срока защиты нет.
    /// </summary>
    public void Protect(uint player, double minutes)
    {
        var ticks = (long)Math.Floor(minutes * 60 / Dt + 0.5);
        Set(Wallet(player), "Player.safe", ticks > 0 ? Tick + (ulong)ticks : 0);
    }

    /// <summary>Сколько сущностей у игрока, кроме его счёта (Owner, как owned в тестах TS: у счёта TS хозяина нет).</summary>
    public int Owned(uint player) => With(Names.Owner.Name).Count(entity => OwnerOf(entity) == player && !Has(entity, Names.Player.Name));

    /// <summary>Игрок просит начать заново (событие player_respawn); forced — велел администратор.</summary>
    public void Respawn(uint player, bool forced = false)
    {
        Host.Respawn(player, forced);
        Drain();
        Throw();
    }
}
