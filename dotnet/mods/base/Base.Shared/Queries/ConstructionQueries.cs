using MemoryPack;

namespace Base.Shared.Queries;

// Запросы стройки (Host.Query: тесты, администратор, редактор, подсказки клиента) — только чтение мира.

/// <summary>Аргументы «игрок, вид здания или работы Type, тайл (X, Y)».</summary>
[MemoryPackable]
public sealed partial class BuildArgs
{
    public uint Player { get; set; }
    public string Type { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>Аргументы протяжки: игрок, тайлы x, y подряд, ворота ли.</summary>
[MemoryPackable]
public sealed partial class StrokeArgs
{
    public uint Player { get; set; }
    public int[] Tiles { get; set; } = [];
    public bool Gate { get; set; }
}

/// <summary>Аргументы «тайл (X, Y)».</summary>
[MemoryPackable]
public sealed partial class TileArgs
{
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>Ответ-список флагов по порядку.</summary>
[MemoryPackable]
public sealed partial class FlagList
{
    public bool[] Items { get; set; } = [];
}

/// <summary>Сеть (зона строительства или энергосеть): здания и трубы, подключённые стройки, круги x, y, радиус подряд.</summary>
[MemoryPackable]
public sealed partial class ZoneInfo
{
    public uint[] Buildings { get; set; } = [];
    public uint[] Sites { get; set; } = [];
    public double[] Circles { get; set; } = [];
}

/// <summary>Ответ-список сетей игрока: с главным зданием — первыми.</summary>
[MemoryPackable]
public sealed partial class ZoneList
{
    public ZoneInfo[] Items { get; set; } = [];
}

/// <summary>Имена запросов стройки.</summary>
public static class ConstructionQueries
{
    /// <summary>Может ли игрок заложить здание (canBuild): BuildArgs → bool.</summary>
    public const string CanBuild = "construction.canBuild";

    /// <summary>Открыто ли здание игроку (isUnlocked): BuildArgs → bool.</summary>
    public const string Unlocked = "construction.unlocked";

    /// <summary>Можно ли положить покрытие Type в тайл (canPave): BuildArgs → bool.</summary>
    public const string CanPave = "construction.canPave";

    /// <summary>Можно ли начать землеройную работу Type (peak, cliff) в тайле (canDig): BuildArgs → bool.</summary>
    public const string CanDig = "construction.canDig";

    /// <summary>Свободные кредиты игрока (freeCredits): PlayerArgs → double.</summary>
    public const string FreeCredits = "construction.freeCredits";

    /// <summary>Какие тайлы протяжки стены или ворот можно заложить (wallStroke): StrokeArgs → FlagList.</summary>
    public const string WallStroke = "construction.wallStroke";

    /// <summary>Зоны строительства игрока (zonesOf): PlayerArgs → ZoneList.</summary>
    public const string Zones = "construction.zones";

    /// <summary>Энергосети игрока (gridsOf): PlayerArgs → ZoneList.</summary>
    public const string Grids = "construction.grids";

    /// <summary>Во сколько раз быстрее голой скалы строится здание Type в (X, Y) (buildSpeed): BuildArgs → double.</summary>
    public const string BuildSpeed = "construction.buildSpeed";

    /// <summary>Ждёт ли стройка материалов (awaitsMaterials): EntityArgs → bool.</summary>
    public const string AwaitsMaterials = "construction.awaitsMaterials";

    /// <summary>Стройка, основание которой накрывает тайл (siteAt): TileArgs → uint (0 — нет).</summary>
    public const string SiteAt = "construction.siteAt";

    /// <summary>Работа над покрытием на тайле — укладка или разбор (paving.at): TileArgs → uint (0 — нет).</summary>
    public const string PaveAt = "construction.paveAt";

    /// <summary>Стройки игрока, которым не хватит кредитов (sitesShortOfCredits): PlayerArgs → EntityList.</summary>
    public const string ShortOfCredits = "construction.short";
}

/// <summary>Ответ-список сущностей.</summary>
[MemoryPackable]
public sealed partial class EntityList
{
    public uint[] Items { get; set; } = [];
}
