using MemoryPack;

namespace Base.Shared.Orders;

// Правки редактора (edit.*, EditOp в packages/sim-ts/src/editOps.ts): приказы с правом редактора, ими же пользуются
// тесты. Сущности — номерами, месторождения — левым верхним тайлом (x и y подряд), виды — именами записей.

/// <summary>Кисть карты: что не задано, остаётся у тайла прежним. Terrain, Biome, Dunes — номера, как в terrain.ts.</summary>
[MemoryPackable]
public sealed partial class Brush
{
    public int? Terrain { get; set; }
    public int? Tier { get; set; }
    public bool? Cliff { get; set; }
    public int? Dunes { get; set; }
    public int? Biome { get; set; }

    /// <summary>square, circle или spray.</summary>
    public string? Shape { get; set; }
}

/// <summary>edit.paint: покрасить землю кистью размера size с серединой (x, y).</summary>
[MemoryPackable]
public sealed partial class EditPaintOrder
{
    public const string Name = "edit.paint";
    public int X { get; set; }
    public int Y { get; set; }
    public int Size { get; set; }
    public Brush Brush { get; set; } = new();
}

/// <summary>edit.building: поставить готовое здание игрока.</summary>
[MemoryPackable]
public sealed partial class EditBuildingOrder
{
    public const string Name = "edit.building";
    public string Type { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public uint Player { get; set; }
}

/// <summary>edit.unit: поставить юнит игрока в точку.</summary>
[MemoryPackable]
public sealed partial class EditUnitOrder
{
    public const string Name = "edit.unit";
    public string Type { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public uint Player { get; set; }
}

/// <summary>edit.deposit: положить месторождение с запасом reserve.</summary>
[MemoryPackable]
public sealed partial class EditDepositOrder
{
    public const string Name = "edit.deposit";
    public int X { get; set; }
    public int Y { get; set; }
    public string Kind { get; set; } = "";
    public double Reserve { get; set; }
}

/// <summary>edit.setDeposit: сменить вид или остаток месторождений; не задано — прежнее.</summary>
[MemoryPackable]
public sealed partial class EditSetDepositOrder
{
    public const string Name = "edit.setDeposit";
    public int[] Spots { get; set; } = [];
    public string? Kind { get; set; }
    public double? Left { get; set; }
}

/// <summary>edit.removeDeposit: убрать месторождения.</summary>
[MemoryPackable]
public sealed partial class EditRemoveDepositOrder
{
    public const string Name = "edit.removeDeposit";
    public int[] Spots { get; set; } = [];
}

/// <summary>edit.erase: убрать сущности.</summary>
[MemoryPackable]
public sealed partial class EditEraseOrder
{
    public const string Name = "edit.erase";
    public uint[] Entities { get; set; } = [];
}

/// <summary>edit.move: сдвинуть сущности и месторождения на (dx, dy).</summary>
[MemoryPackable]
public sealed partial class EditMoveOrder
{
    public const string Name = "edit.move";
    public uint[] Entities { get; set; } = [];
    public int Dx { get; set; }
    public int Dy { get; set; }
    public int[] Deposits { get; set; } = [];
}

/// <summary>edit.owner: отдать сущности игроку.</summary>
[MemoryPackable]
public sealed partial class EditOwnerOrder
{
    public const string Name = "edit.owner";
    public uint[] Entities { get; set; } = [];
    public uint Player { get; set; }
}

/// <summary>edit.health: прочность долей от полной.</summary>
[MemoryPackable]
public sealed partial class EditHealthOrder
{
    public const string Name = "edit.health";
    public uint[] Entities { get; set; } = [];
    public double Share { get; set; }
}

/// <summary>edit.facing: поворот сущностей.</summary>
[MemoryPackable]
public sealed partial class EditFacingOrder
{
    public const string Name = "edit.facing";
    public uint[] Entities { get; set; } = [];
    public double Angle { get; set; }
}

/// <summary>edit.turret: поворот турелей сущностей; Index — какой (нет — всех).</summary>
[MemoryPackable]
public sealed partial class EditTurretOrder
{
    public const string Name = "edit.turret";
    public uint[] Entities { get; set; } = [];
    public double Angle { get; set; }
    public int? Index { get; set; }
}

/// <summary>edit.stock: сколько груза good лежит в складе сущности.</summary>
[MemoryPackable]
public sealed partial class EditStockOrder
{
    public const string Name = "edit.stock";
    public uint Entity { get; set; }
    public string Good { get; set; } = "";
    public double Amount { get; set; }
}

/// <summary>edit.clearTasks: снять с сущностей дела и очереди.</summary>
[MemoryPackable]
public sealed partial class EditClearTasksOrder
{
    public const string Name = "edit.clearTasks";
    public uint[] Entities { get; set; } = [];
}

/// <summary>edit.order: выполнить приказ за игрока сразу (orderNow): имя приказа и его payload MemoryPack.</summary>
[MemoryPackable]
public sealed partial class EditOrderOrder
{
    public const string Name = "edit.order";
    public uint Player { get; set; }
    public string Order { get; set; } = "";
    public byte[] Payload { get; set; } = [];
}

/// <summary>edit.credits: кредиты игрока.</summary>
[MemoryPackable]
public sealed partial class EditCreditsOrder
{
    public const string Name = "edit.credits";
    public uint Player { get; set; }
    public double Amount { get; set; }
}

/// <summary>edit.camera: где камера игрока при входе; Clear — забыть.</summary>
[MemoryPackable]
public sealed partial class EditCameraOrder
{
    public const string Name = "edit.camera";
    public uint Player { get; set; }
    public bool Clear { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Zoom { get; set; }
}

/// <summary>edit.addPlayer: новый игрок без юнитов.</summary>
[MemoryPackable]
public sealed partial class EditAddPlayerOrder
{
    public const string Name = "edit.addPlayer";
}

/// <summary>edit.start: стартовый набор игрока (кредиты и юниты партии) вокруг тайла (x, y), как spawnStartingUnits.</summary>
[MemoryPackable]
public sealed partial class EditStartOrder
{
    public const string Name = "edit.start";
    public uint Player { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}
