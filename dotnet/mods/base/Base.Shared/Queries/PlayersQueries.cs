using MemoryPack;

namespace Base.Shared.Queries;

// Запросы раздела игроков (Host.Query: тесты, администратор, редактор) — только чтение мира.

/// <summary>Союз игрока: с кем (по возрастанию), глава (−1 — ни с кем), кому он предложил и кто предложил ему.</summary>
[MemoryPackable]
public sealed partial class TeamInfo
{
    public uint[] Allies { get; set; } = [];
    public long Head { get; set; } = -1;
    public uint[] Offered { get; set; } = [];
    public uint[] Asked { get; set; } = [];
}

/// <summary>Список игроков.</summary>
[MemoryPackable]
public sealed partial class PlayerList
{
    public uint[] Items { get; set; } = [];
}

/// <summary>Имена запросов раздела игроков.</summary>
public static class PlayersQueries
{
    /// <summary>Союз игрока (alliesOf, allyHeadOf, offered, asked): PlayerArgs → TeamInfo.</summary>
    public const string Team = "players.team";

    /// <summary>Проиграл ли игрок (isDefeated): PlayerArgs → bool.</summary>
    public const string Defeated = "players.defeated";

    /// <summary>Сколько тиков защиты новичка осталось (protectionLeft): PlayerArgs → double.</summary>
    public const string ProtectionLeft = "players.protectionLeft";

    /// <summary>Игроки под защитой новичка (protectedPlayers): → PlayerList.</summary>
    public const string Sheltered = "players.sheltered";

    /// <summary>Игроки со счетами по порядку заведения (playersOf): → PlayerList.</summary>
    public const string All = "players.all";
}
