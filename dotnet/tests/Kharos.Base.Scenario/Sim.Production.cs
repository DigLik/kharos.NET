using Base.Shared.Queries;

namespace Kharos.Base.Scenario;

// Производство (раздел Production, С4): запросы production.*.
public sealed partial class Sim
{
    /// <summary>Есть ли у цеха место под готовое одной сборки (hasRoom).</summary>
    public bool HasRoom(uint workshop) => Query<bool>(ProductionQueries.HasRoom, new EntityArgs { Entity = workshop });
}
