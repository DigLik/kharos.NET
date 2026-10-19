using Base.Shared.Queries;

namespace Kharos.Base.Scenario;

// Тактика (раздел Tactics, С8): запросы tactics.* — стойка, патруль, очередь приказов Shift.
public sealed partial class Sim
{
    /// <summary>Тактика юнита (Tactics): нет компонента — Has false, стойка defensive.</summary>
    public TacticsInfo TacticsOf(uint entity) => Query<TacticsInfo>(TacticsQueries.Stance, new EntityArgs { Entity = entity });

    /// <summary>Стойка юнита (stanceOf): без тактики — defensive.</summary>
    public string StanceOf(uint entity) => TacticsOf(entity).Stance;

    /// <summary>Точки патруля юнита (x, y подряд); нет патруля — пусто.</summary>
    public double[] PatrolOf(uint entity) => TacticsOf(entity).Patrol;

    /// <summary>Очередь приказов юнита (Orders.list): имена, группы и payload приказов по порядку.</summary>
    public QueueInfo QueueOf(uint entity) => Query<QueueInfo>(TacticsQueries.Queue, new EntityArgs { Entity = entity });

    /// <summary>Очередь приказов юнита (Orders.list): имена приказов по порядку.</summary>
    public string[] QueueNames(uint entity) => QueueOf(entity).Names;

    /// <summary>Сколько приказов ждёт в очереди юнита.</summary>
    public int QueueLength(uint entity) => QueueNames(entity).Length;
}
