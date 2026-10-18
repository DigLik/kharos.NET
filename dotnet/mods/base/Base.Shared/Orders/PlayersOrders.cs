using MemoryPack;

namespace Base.Shared.Orders;

// Приказы игроков (Players): поражение, сигналы, союзы (команды хоста ally, unally в TS — приказы мода).

/// <summary>surrender: сдаться — все свои юниты и здания исчезают.</summary>
[MemoryPackable]
public sealed partial class SurrenderOrder
{
    public const string Name = "surrender";
}

/// <summary>scuttle: взорвать своих юнитов на месте, никому не вредя.</summary>
[MemoryPackable]
public sealed partial class ScuttleOrder
{
    public const string Name = "scuttle";
    public uint[] Units { get; set; } = [];
}

/// <summary>ping: метка союзникам в точке (x, y), тайлы.</summary>
[MemoryPackable]
public sealed partial class PingOrder
{
    public const string Name = "ping";
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>ally: предложить союз игроку To или принять его предложение (offerAlliance).</summary>
[MemoryPackable]
public sealed partial class AllyOrder
{
    public const string Name = "ally";
    public uint To { get; set; }
}

/// <summary>breakAlly: глава исключает игрока To из союза или снимает предложение (breakAlliance).</summary>
[MemoryPackable]
public sealed partial class BreakAllyOrder
{
    public const string Name = "breakAlly";
    public uint To { get; set; }
}

/// <summary>exitAlly: выйти из союза самому (exitAlliance).</summary>
[MemoryPackable]
public sealed partial class ExitAllyOrder
{
    public const string Name = "exitAlly";
}
