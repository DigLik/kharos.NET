using MemoryPack;

namespace Base.Shared.Queries;

// Запросы тактики (Host.Query: тесты, администратор, редактор) — только чтение мира.

/// <summary>Тактика юнита (Tactics): есть ли компонент, стойка, звено и точки патруля (x, y подряд), место возврата.</summary>
[MemoryPackable]
public sealed partial class TacticsInfo
{
    public bool Has { get; set; }
    public string Stance { get; set; } = "defensive";
    public int Leg { get; set; }
    public bool Away { get; set; }
    public double HomeX { get; set; }
    public double HomeY { get; set; }
    public bool Resupply { get; set; }
    public double[] Patrol { get; set; } = [];
}

/// <summary>Очередь приказов юнита (Orders.list): имена приказов, номера групп и payload (без списка юнитов) по порядку.</summary>
[MemoryPackable]
public sealed partial class QueueInfo
{
    public string[] Names { get; set; } = [];
    public long[] Groups { get; set; } = [];
    public byte[][] Payloads { get; set; } = [];
}

public static class TacticsQueries
{
    /// <summary>Тактика юнита: EntityArgs → TacticsInfo.</summary>
    public const string Stance = "tactics.stance";

    /// <summary>Очередь приказов юнита: EntityArgs → QueueInfo.</summary>
    public const string Queue = "tactics.queue";
}
