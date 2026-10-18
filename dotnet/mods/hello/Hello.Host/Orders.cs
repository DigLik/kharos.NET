using MemoryPack;

namespace Hello;

/// <summary>Приказ hello.spawn: сколько юнитов и где. Payload — MemoryPack (тот же файл — в тестах хоста).</summary>
[MemoryPackable]
public sealed partial class SpawnOrder
{
    public int Count { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}
