using Base.Host.Sections.Logistics;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Pipes;

/// <summary>
/// Поля схемы, нужные трубам сверх <see cref="Kernel.S"/> и <see cref="L"/>: пачка (Batch) и правило видимости (Sighting).
/// Берутся привязкой системы pipeFlow (<see cref="Load"/> — до первого тика, приказа и запроса).
/// </summary>
internal static class P
{
    public static ComponentRef Batch = null!, Sighting = null!;
    public static FieldRef BatchFrom, BatchTo, BatchResource, BatchAmount, BatchSent, BatchArrive, BatchPush, BatchPath, SightingRule;

    /// <summary>Хозяин и союзники (Sightings.Allies движка): так пачку видят в TS (vision.ts).</summary>
    public const int Allies = 1;

    public static void Load(Func<string, ComponentRef> component)
    {
        L.Load(component);
        Batch = component(Names.Batch.Name);
        Sighting = component(Names.Sighting.Name);
        BatchFrom = Batch[Names.Batch.From];
        BatchTo = Batch[Names.Batch.To];
        BatchResource = Batch[Names.Batch.Resource];
        BatchAmount = Batch[Names.Batch.Amount];
        BatchSent = Batch[Names.Batch.Sent];
        BatchArrive = Batch[Names.Batch.Arrive];
        BatchPush = Batch[Names.Batch.Push];
        BatchPath = Batch[Names.Batch.Path];
        SightingRule = Sighting[Names.Sighting.Rule];
    }
}
