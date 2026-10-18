using MemoryPack;

namespace Hello;

/// <summary>Приказ hello.spawn: сколько юнитов и где. Payload — MemoryPack (тот же файл — в тестах хоста).</summary>
[MemoryPackable]
public sealed partial class SpawnOrder
{
    public int Count { get; set; }
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>Разброс вокруг точки, тайлы (по умолчанию ±0,5).</summary>
    public double Spread { get; set; } = 1;
}

/// <summary>Приказ hello.move: своим юнитам из списка — ехать к точке.</summary>
[MemoryPackable]
public sealed partial class MoveOrder
{
    public uint[] Entities { get; set; } = [];
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>Приказ hello.probe: спросить инструменты движка о точке и сущности; ответ — сигнал hello.probe.</summary>
[MemoryPackable]
public sealed partial class ProbeOrder
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Radius { get; set; }
    public uint Entity { get; set; }
}

/// <summary>Что ответили инструменты (payload сигнала hello.probe).</summary>
[MemoryPackable]
public sealed partial class ProbeReport
{
    /// <summary>Near(x, y, r, Drive).</summary>
    public uint[] Near { get; set; } = [];
    public bool HasLand { get; set; }
    public byte Terrain { get; set; }
    public byte Biome { get; set; }
    public bool Foot { get; set; }
    /// <summary>Count(Drive).</summary>
    public int Units { get; set; }
    public int GameProto { get; set; }
    public string? FirstProto { get; set; }
    public bool Alive { get; set; }
    public bool HasDrive { get; set; }
    /// <summary>Get(entity, Position): x, y; нет — пусто.</summary>
    public double[] Position { get; set; } = [];
    /// <summary>Text(StringId("hello")).</summary>
    public string? Text { get; set; }
    public byte[]? Storage { get; set; }
    public uint StorageEntity { get; set; }
}

/// <summary>
/// Приказ hello.follow: своим юнитам из списка — держаться за ведущим со сдвигом (Follow); ответ — сигнал hello.staged
/// хозяину: сколько из них сцена (Stage) видит с Follow сразу после записи.
/// </summary>
[MemoryPackable]
public sealed partial class FollowOrder
{
    public uint Leader { get; set; }
    public uint[] Followers { get; set; } = [];
    public double Dx { get; set; }
    public double Dy { get; set; }
}

/// <summary>Приказ hello.layer (Д3): канал слоя-сетки в прямоугольнике — командой Layer; owner 0 — слой мира.</summary>
[MemoryPackable]
public sealed partial class LayerOrder
{
    public string Layer { get; set; } = "";
    public uint Owner { get; set; }
    public int Channel { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 1;
    public int H { get; set; } = 1;
    public double Value { get; set; }
}

/// <summary>Приказ hello.item: положить предмет в слой предметов или убрать лежащие ровно в точке — командой Item.</summary>
[MemoryPackable]
public sealed partial class ItemOrder
{
    public string Layer { get; set; } = "";
    public uint Owner { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double[] Fields { get; set; } = [];
    public bool Delete { get; set; }
}

/// <summary>Приказ hello.land: правка тайла земли (Peak = false) или вершины горы — командой Land.</summary>
[MemoryPackable]
public sealed partial class LandOrder
{
    public bool Peak { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public byte Terrain { get; set; }
    public byte Biome { get; set; }
    public byte Tier { get; set; }
    public bool Cliff { get; set; }
    public byte Dunes { get; set; }
    public double PeakX { get; set; }
    public double PeakY { get; set; }
    public double Radius { get; set; }
}

/// <summary>
/// Приказ hello.read: что скажут инструменты Layer, Items и Peak; ответ — сигнал hello.read хозяину
/// (<see cref="ReadReport"/>). Слой или предметы можно не спрашивать, оставив имя пустым.
/// </summary>
[MemoryPackable]
public sealed partial class ReadOrder
{
    public string Layer { get; set; } = "";
    public uint Owner { get; set; }
    public int Channel { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 1;
    public int H { get; set; } = 1;
    public string ItemLayer { get; set; } = "";
    public uint ItemOwner { get; set; }
    public double ItemX { get; set; }
    public double ItemY { get; set; }
    public double ItemRadius { get; set; }
    public bool Peak { get; set; }
    public int PeakX { get; set; }
    public int PeakY { get; set; }
}

/// <summary>Ответ hello.read: значения канала, предметы (x, y и поля подряд) и вершина.</summary>
[MemoryPackable]
public sealed partial class ReadReport
{
    public double[] Values { get; set; } = [];
    public double[] Items { get; set; } = [];
    public int ItemCount { get; set; }
    public bool HasPeak { get; set; }
    public double PeakX { get; set; }
    public double PeakY { get; set; }
    public double PeakRadius { get; set; }
}
