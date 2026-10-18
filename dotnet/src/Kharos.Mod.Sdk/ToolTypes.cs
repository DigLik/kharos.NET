using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

/// <summary>Ревизии мира (<see cref="Tools.Revision"/>): ключ кэша, производного от мира.</summary>
/// <param name="World">Состав: сущности и компоненты.</param>
/// <param name="Positions">Любое изменение Position.</param>
/// <param name="Land">Правки земли.</param>
/// <param name="Layers">Изменения слоёв.</param>
/// <param name="Tick">Тик.</param>
public readonly record struct WorldRevision(ulong World, ulong Positions, ulong Land, ulong Layers, ulong Tick);

/// <summary>Предмет слоя (<see cref="Tools.Items"/>): место и поля по порядку объявления.</summary>
public sealed record MapItem(double X, double Y, double[] Fields);

/// <summary>Флаги поиска пути и места (ModAbi.Plan*).</summary>
[Flags]
public enum PathFlags : uint
{
    None = 0,
    /// <summary>Пехота: можно и к подножию обрыва.</summary>
    Foot = ModAbi.PlanFoot,
    /// <summary>Затор: путь в обход застрявших.</summary>
    Jam = ModAbi.PlanJam,
    /// <summary>Летающие: годится любой тайл карты.</summary>
    Air = ModAbi.PlanAir,
}

/// <summary>Биты тайла (<see cref="Tools.Tiles"/>).</summary>
[Flags]
public enum TileFlags : byte
{
    None = 0,
    OnMap = ModAbi.TileOnMap,
    Passable = ModAbi.TilePassable,
    CliffFoot = ModAbi.TileCliffFoot,
    Buildable = ModAbi.TileBuildable,
    Building = ModAbi.TileBuilding,
    OpenGate = ModAbi.TileOpenGate,
    Unit = ModAbi.TileUnit,
}

/// <summary>Итог поиска пути.</summary>
public enum PlanStatus : byte
{
    /// <summary>Путь есть (к цели или к тайлу рядом).</summary>
    Found = ModAbi.PlanFound,
    /// <summary>Поиск оборвала квота тика: прежний путь оставить, попробовать в следующем тике.</summary>
    Cut = ModAbi.PlanCut,
    /// <summary>Пути нет.</summary>
    None = ModAbi.PlanNone,
}

/// <summary>Путь (<see cref="Tools.Plan"/>): итог, цель (могла сместиться на свободный тайл) и точки x, y подряд.</summary>
public sealed record PlanResult(PlanStatus Status, int GoalX, int GoalY, double[] Points);

/// <summary>Место юнита в группе (<see cref="Tools.GroupMove"/>): тайл и ехать ли напрямую.</summary>
public readonly record struct GroupSlot(int X, int Y, bool Direct);
