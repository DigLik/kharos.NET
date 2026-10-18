using MemoryPack;

namespace Base.Shared.Signals;

/// <summary>built: здание достроено (событие built в sim.ts) — тем, кто видит место; звук и подсказки.</summary>
[MemoryPackable]
public sealed partial class BuiltSignal
{
    public const string Name = "built";
    public uint Entity { get; set; }
    public uint Player { get; set; }
    public string Building { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
}
