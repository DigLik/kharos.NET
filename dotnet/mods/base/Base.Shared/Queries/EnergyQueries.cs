using MemoryPack;

namespace Base.Shared.Queries;

// Запросы энергии (Host.Query: тесты, администратор, редактор) — только чтение мира.

/// <summary>
/// Хозяйство энергосети (Economy в income.ts): выработка с отданным накопителями, нынешний спрос, полная нагрузка, запас
/// накопителей под неё, доход в секунду, сколько тесных зданий, сколько отдают накопители.
/// </summary>
[MemoryPackable]
public sealed partial class EconomyInfo
{
    public double Produced { get; set; }
    public double Demand { get; set; }
    public double Peak { get; set; }
    public double Spare { get; set; }
    public double Income { get; set; }
    public double Crowd { get; set; }
    public double Stored { get; set; }
}

/// <summary>Хозяйства энергосетей игрока по порядку его сетей.</summary>
[MemoryPackable]
public sealed partial class EconomyList
{
    public EconomyInfo[] Items { get; set; } = [];
}

/// <summary>Доли энергии потребителей (powerSupply): сущность — доля, по одному индексу.</summary>
[MemoryPackable]
public sealed partial class SupplyList
{
    public uint[] Entities { get; set; } = [];
    public double[] Shares { get; set; } = [];
}

/// <summary>Здания без энергии (powerStates): сущность — «overload» (станция перегружена) или «starved» (потребитель).</summary>
[MemoryPackable]
public sealed partial class PowerStateList
{
    public uint[] Entities { get; set; } = [];
    public string[] States { get; set; } = [];
}

/// <summary>Имена запросов энергии.</summary>
public static class EnergyQueries
{
    /// <summary>Итог по всем сетям игрока (economyOf): PlayerArgs → EconomyInfo.</summary>
    public const string Economy = "energy.economy";

    /// <summary>Хозяйства сетей игрока (zoneEconomies): PlayerArgs → EconomyList.</summary>
    public const string Zones = "energy.zones";

    /// <summary>Доли энергии всех потребителей (powerSupply): без аргументов → SupplyList.</summary>
    public const string Supply = "energy.supply";

    /// <summary>Здания, которым не хватает энергии (powerStates): без аргументов → PowerStateList.</summary>
    public const string States = "energy.states";

    /// <summary>Станции под нагрузкой (loadedPlants): без аргументов → EntityList.</summary>
    public const string Loaded = "energy.loaded";

    /// <summary>Станции зон, где потребителей больше, чем потянут и целые (overbuiltPlants): без аргументов → EntityList.</summary>
    public const string Overbuilt = "energy.overbuilt";

    /// <summary>Доходные здания в сетях без главного (coreless): без аргументов → EntityList.</summary>
    public const string Coreless = "energy.coreless";

    /// <summary>Горит ли в реакторе стержень (isBurning): EntityArgs → bool.</summary>
    public const string Burning = "energy.burning";
}
