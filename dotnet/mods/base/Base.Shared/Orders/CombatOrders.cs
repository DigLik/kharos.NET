using MemoryPack;

namespace Base.Shared.Orders;

// Приказы боя (Combat).

/// <summary>attack: свои вооружённые юниты атакуют чужой юнит или здание. Только изнутри (цель по номеру).</summary>
[MemoryPackable]
public sealed partial class AttackOrder
{
    public const string Name = "attack";
    public uint[] Units { get; set; } = [];
    public uint Target { get; set; }
    public bool Queue { get; set; }
}
