using MemoryPack;

namespace Base.Shared.Orders;

// Приказы труб (Pipes).

/// <summary>pipes: наземная труба по тайлам (x и y подряд, по порядку).</summary>
[MemoryPackable]
public sealed partial class PipesOrder
{
    public const string Name = "pipes";
    public int[] Tiles { get; set; } = [];
    public uint[] Builders { get; set; } = [];
    public bool Queue { get; set; }
}

/// <summary>wells: колодец (x, y) или пара — концы подземной трубы.</summary>
[MemoryPackable]
public sealed partial class WellsOrder
{
    public const string Name = "wells";
    public int[] Tiles { get; set; } = [];
    public uint[] Builders { get; set; } = [];
    public bool Queue { get; set; }
}

/// <summary>
/// edit.connect: подключить готовое здание Building к ближайшей другой своей сети готовой трубой, без стройки
/// (connectBuilding в piping.ts). Право редактора.
/// </summary>
[MemoryPackable]
public sealed partial class EditConnectOrder
{
    public const string Name = "edit.connect";
    public uint Building { get; set; }
}

/// <summary>
/// edit.connectAll: соединить готовой трубой одну сеть игрока Player с сетью главного здания (шаг connectAll в piping.ts).
/// Сети считаются по миру до вызова, поэтому за раз соединяется одна; правку повторяют, пока число зданий растёт. Право
/// редактора.
/// </summary>
[MemoryPackable]
public sealed partial class EditConnectAllOrder
{
    public const string Name = "edit.connectAll";
    public uint Player { get; set; }
}
