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

/// <summary>Итог поиска пути (<see cref="Tools.Plan"/>) и подъезда (<see cref="Tools.Stand"/>).</summary>
public enum PlanStatus : byte
{
    /// <summary>Путь есть (к цели или к тайлу рядом): положить в Path.</summary>
    Found = ModAbi.PlanFound,
    /// <summary>Прежний путь оставить: норма тика вышла, затор без пути или юнит врос. У Stand — не промах.</summary>
    Cut = ModAbi.PlanCut,
    /// <summary>Пути (места) нет: снять Path.</summary>
    None = ModAbi.PlanNone,
    /// <summary>Stand: юнит уже на месте — Path не трогать.</summary>
    Here = 3,
}

/// <summary>
/// Путь (<see cref="Tools.Plan"/>, <see cref="Tools.Stand"/>): итог, цель (могла сместиться на свободный тайл), точки
/// x, y подряд и Roads — путь проложен с дорогой в виду (Path.roads).
/// </summary>
public sealed record PlanResult(PlanStatus Status, int GoalX, int GoalY, double[] Points, bool Roads = false);

/// <summary>Как юнит идёт на своё место в группе (<see cref="Tools.GroupMove"/>).</summary>
public enum GroupMode : byte
{
    /// <summary>Летающий: Path из одной точки — центра тайла, direct false.</summary>
    Air = 0,
    /// <summary>Наземный: напрямую (Path.direct), путь — только если упрётся.</summary>
    Direct = 1,
    /// <summary>Не трогать: врос, нет места или нет Position.</summary>
    Skip = 2,
}

/// <summary>Место юнита в группе (<see cref="Tools.GroupMove"/>): тайл и как к нему идти.</summary>
public readonly record struct GroupSlot(int X, int Y, GroupMode Mode)
{
    /// <summary>Ехать напрямую.</summary>
    public bool Direct => Mode == GroupMode.Direct;
}
