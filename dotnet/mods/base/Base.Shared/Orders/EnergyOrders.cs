using MemoryPack;

namespace Base.Shared.Orders;

// Приказы энергии (Energy).

/// <summary>work: включить или выключить своё здание — потребителя энергии.</summary>
[MemoryPackable]
public sealed partial class WorkOrder
{
    public const string Name = "work";
    public uint Building { get; set; }
    public bool On { get; set; }
}
