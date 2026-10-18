using Base.Shared.Data;
using Base.Shared.Queries;
using Base.Shared.Rules;
using Kharos.Engine.Layers;

namespace Kharos.Base.Scenario;

// Стройка (раздел Construction, С1): запросы construction.*, слой покрытия, занятость и проходимость тайлов движка.
public sealed partial class Sim
{
    /// <summary>Таблицы base из mods/base/data (виды юнитов для ходовой).</summary>
    private static readonly Lazy<BaseTables> Tables = new(() => BaseTables.Load(name =>
        File.Exists(Path.Combine(ModDir, "data", name + ".json")) ? File.ReadAllBytes(Path.Combine(ModDir, "data", name + ".json")) : null));

    // ===== Запросы мода =====

    /// <summary>Может ли игрок заложить здание (canBuild).</summary>
    public bool CanBuild(uint player, string type, int x, int y) => Query<bool>(ConstructionQueries.CanBuild, new BuildArgs { Player = player, Type = type, X = x, Y = y });

    /// <summary>Открыто ли игроку здание (isUnlocked).</summary>
    public bool Unlocked(uint player, string type) => Query<bool>(ConstructionQueries.Unlocked, new BuildArgs { Player = player, Type = type });

    /// <summary>Можно ли положить покрытие (canPave).</summary>
    public bool CanPave(uint player, string kind, int x, int y) => Query<bool>(ConstructionQueries.CanPave, new BuildArgs { Player = player, Type = kind, X = x, Y = y });

    /// <summary>Можно ли начать землеройную работу (canDig).</summary>
    public bool CanDig(uint player, string kind, int x, int y) => Query<bool>(ConstructionQueries.CanDig, new BuildArgs { Player = player, Type = kind, X = x, Y = y });

    /// <summary>Свободные кредиты игрока (freeCredits).</summary>
    public double FreeCredits(uint player) => Query<double>(ConstructionQueries.FreeCredits, new PlayerArgs { Player = player });

    /// <summary>Какие тайлы протяжки стены или ворот можно заложить (wallStroke).</summary>
    public bool[] WallStroke(uint player, int[] tiles, bool gate = false) =>
        Query<FlagList>(ConstructionQueries.WallStroke, new StrokeArgs { Player = player, Tiles = tiles, Gate = gate }).Items;

    /// <summary>Зоны строительства игрока (zonesOf).</summary>
    public ZoneInfo[] Zones(uint player) => Query<ZoneList>(ConstructionQueries.Zones, new PlayerArgs { Player = player }).Items;

    /// <summary>Энергосети игрока (gridsOf).</summary>
    public ZoneInfo[] Grids(uint player) => Query<ZoneList>(ConstructionQueries.Grids, new PlayerArgs { Player = player }).Items;

    /// <summary>Во сколько раз быстрее голой скалы строится здание здесь (buildSpeed).</summary>
    public double BuildSpeed(string type, int x, int y) => Query<double>(ConstructionQueries.BuildSpeed, new BuildArgs { Type = type, X = x, Y = y });

    /// <summary>Ждёт ли стройка материалов (awaitsMaterials).</summary>
    public bool AwaitsMaterials(uint site) => Query<bool>(ConstructionQueries.AwaitsMaterials, new EntityArgs { Entity = site });

    /// <summary>Стройка, накрывающая тайл (siteAt); нет — 0.</summary>
    public uint SiteAt(int x, int y) => Query<uint>(ConstructionQueries.SiteAt, new TileArgs { X = x, Y = y });

    /// <summary>Работа над покрытием на тайле (paving.at); нет — 0.</summary>
    public uint PaveAt(int x, int y) => Query<uint>(ConstructionQueries.PaveAt, new TileArgs { X = x, Y = y });

    // ===== Покрытие (слой paving) =====

    /// <summary>Код готового покрытия тайла (PaveRules.Code); нет — 0.</summary>
    public int PavingCode(int x, int y) => Host.Layers.Find("paving") is { } id ? (int)Host.Layers.Get(id, LayerOwner.World, x, y, 0) : 0;

    /// <summary>Лежит ли на тайле готовое покрытие этого вида (isPaved).</summary>
    public bool IsPaved(string kind, int x, int y) => PaveRules.KindOf(PavingCode(x, y)) == kind;

    /// <summary>Хозяин готового покрытия; нет покрытия — null (paveOwnerAt).</summary>
    public uint? PaveOwnerAt(int x, int y) => PavingCode(x, y) is var code and not 0 ? PaveRules.OwnerOf(code) : null;

    /// <summary>Кладёт готовое покрытие прямо в слой, без строителей (paving.lay в тестах TS).</summary>
    public void Lay(string kind, int x, int y, uint owner = 1) => Host.Layers.Set(Host.Layers.Id("paving"), LayerOwner.World, x, y, 0, PaveRules.Code(kind, owner));

    // ===== Тайлы движка =====

    /// <summary>Здание на тайле (occupancy.at); свободен — 0.</summary>
    public uint OccupantAt(int x, int y) => Host.Tools.Navigation!.Occupancy.At(x, y);

    /// <summary>Может ли стоять наземный (isWalkable); foot — пехота.</summary>
    public bool Walkable(int x, int y, bool foot = false) => Host.Tools.Navigation!.IsWalkable(x, y, foot);

    /// <summary>Доля скорости вида юнита на тайле (terrainSpeed).</summary>
    public double TerrainSpeed(string unit, int x, int y)
    {
        var spec = Tables.Value.Units[unit];
        var navigation = Host.Tools.Navigation!;
        return navigation.TerrainSpeed(navigation.Chassis.Of(spec.Chassis), spec.Radius, x, y);
    }
}
