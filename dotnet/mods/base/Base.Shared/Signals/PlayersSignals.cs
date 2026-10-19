using MemoryPack;

namespace Base.Shared.Signals;

// Вести игрокам (адресно, SignalTo, и всем, Signal): союзы, поражение, защита новичка. В TS их шлёт host.ts строками чата
// (hostText): ключи Kind — те же ключи раздела host словаря.

/// <summary>
/// ally: весть о союзе. Kind — ключ сообщения (allyOffered, allyAsked, allyFormed, allyNotHead, allyToMember, allyAlready,
/// allyPending, allySelf, noPlayer, allyNoAlliance, allyExited, allyLeftBy, allyDissolved, allyDissolvedBy, allyNothing,
/// allyKickNotHead, allyKicked, allyKickedBy, allyWithdrawn, allyDeclined, allyDeclinedBy); Player — о ком она (имя в
/// тексте), 0 — ни о ком.
/// </summary>
[MemoryPackable]
public sealed partial class AllySignal
{
    public const string Name = "ally";
    public string Kind { get; set; } = "";
    public uint Player { get; set; }
}

/// <summary>defeated: игрок проиграл — у него не осталось ни зданий, ни MCV. Всем, один раз на поражение.</summary>
[MemoryPackable]
public sealed partial class DefeatedSignal
{
    public const string Name = "defeated";
    public uint Player { get; set; }
}

/// <summary>
/// player_respawned: игрок начат заново (событие движка player_respawn) — ему одному. По нему хост закрывает игроку туман и
/// забывает его память о зданиях, клиент — начинает как в новом мире.
/// </summary>
[MemoryPackable]
public sealed partial class RespawnedSignal
{
    public const string Name = "player_respawned";
    public uint Player { get; set; }
}

/// <summary>sheltered: кто сейчас под защитой новичка — всем, когда список поменялся (safe в списке игроков TS).</summary>
[MemoryPackable]
public sealed partial class ShelteredSignal
{
    public const string Name = "sheltered";
    public uint[] Players { get; set; } = [];
}
