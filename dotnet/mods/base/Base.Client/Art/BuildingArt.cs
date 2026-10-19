using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>Основание и роль здания из данных (data/buildings.json): по ним строятся заглушки.</summary>
internal readonly record struct BuildingInfo(int Width, int Height, BuildingKit.Role Role);

/// <summary>
/// Перечень обликов зданий: вид → чертёж Art/Buildings/&lt;Вид&gt;.cs (по файлу на вид, docs/dotnet-n6.md Н6.5). Файл
/// заморожен после волны 0: пакеты правят только файлы своих видов и <see cref="BuildingKit"/>. Б-А — от command до
/// barracks (<see cref="PartA"/>, и BuildingKit), Б-Б — от mine до cannonTurret (<see cref="PartB"/>).
/// </summary>
internal static class BuildingArt
{
    /// <summary>Виды пакета Б-А, порядок data/buildings.json.</summary>
    public static readonly string[] PartA =
    [
        "command", "smelter", "siliconWorks", "distillery", "enricher", "evaporator", "uraniumMill", "blockPlant",
        "ammoPlant", "partsPlant", "rodPlant", "factory", "lab", "repairStation", "supplyDepot", "airfield",
        "generator", "accumulator", "reactor", "silo", "interceptor", "matter", "radar", "windtrap", "barracks",
    ];

    /// <summary>Виды пакета Б-Б, порядок data/buildings.json.</summary>
    public static readonly string[] PartB =
    [
        "mine", "metalYard", "siliconStore", "fuelTank", "khariteVault", "lithiumStore", "uraniumCask", "blockYard",
        "ammoBunker", "partsLocker", "rodRack", "requestStation", "spaceport", "wall", "gate", "pipe", "well",
        "turret", "rocketTurret", "laserTurret", "arcTurret", "shieldGenerator", "cannonTurret",
    ];

    /// <summary>Все виды зданий base в порядке data/buildings.json: у каждого есть облик (чертёж или заглушка).</summary>
    public static readonly string[] Names = [.. PartA, .. PartB];

    /// <summary>Облик вида name; info — основание и роль из данных. Неизвестный вид — заглушка.</summary>
    public static LookSpec Look(string name, BuildingInfo info) => name switch
    {
        "command" => Buildings.Command.Look(info),
        "smelter" => Buildings.Smelter.Look(info),
        "siliconWorks" => Buildings.SiliconWorks.Look(info),
        "distillery" => Buildings.Distillery.Look(info),
        "enricher" => Buildings.Enricher.Look(info),
        "evaporator" => Buildings.Evaporator.Look(info),
        "uraniumMill" => Buildings.UraniumMill.Look(info),
        "blockPlant" => Buildings.BlockPlant.Look(info),
        "ammoPlant" => Buildings.AmmoPlant.Look(info),
        "partsPlant" => Buildings.PartsPlant.Look(info),
        "rodPlant" => Buildings.RodPlant.Look(info),
        "factory" => Buildings.Factory.Look(info),
        "lab" => Buildings.Lab.Look(info),
        "repairStation" => Buildings.RepairStation.Look(info),
        "supplyDepot" => Buildings.SupplyDepot.Look(info),
        "airfield" => Buildings.Airfield.Look(info),
        "generator" => Buildings.Generator.Look(info),
        "accumulator" => Buildings.Accumulator.Look(info),
        "reactor" => Buildings.Reactor.Look(info),
        "silo" => Buildings.Silo.Look(info),
        "interceptor" => Buildings.Interceptor.Look(info),
        "matter" => Buildings.Matter.Look(info),
        "radar" => Buildings.Radar.Look(info),
        "windtrap" => Buildings.Windtrap.Look(info),
        "barracks" => Buildings.Barracks.Look(info),
        "mine" => Buildings.Mine.Look(info),
        "metalYard" => Buildings.MetalYard.Look(info),
        "siliconStore" => Buildings.SiliconStore.Look(info),
        "fuelTank" => Buildings.FuelTank.Look(info),
        "khariteVault" => Buildings.KhariteVault.Look(info),
        "lithiumStore" => Buildings.LithiumStore.Look(info),
        "uraniumCask" => Buildings.UraniumCask.Look(info),
        "blockYard" => Buildings.BlockYard.Look(info),
        "ammoBunker" => Buildings.AmmoBunker.Look(info),
        "partsLocker" => Buildings.PartsLocker.Look(info),
        "rodRack" => Buildings.RodRack.Look(info),
        "requestStation" => Buildings.RequestStation.Look(info),
        "spaceport" => Buildings.Spaceport.Look(info),
        "wall" => Buildings.Wall.Look(info),
        "gate" => Buildings.Gate.Look(info),
        "pipe" => Buildings.Pipe.Look(info),
        "well" => Buildings.Well.Look(info),
        "turret" => Buildings.Turret.Look(info),
        "rocketTurret" => Buildings.RocketTurret.Look(info),
        "laserTurret" => Buildings.LaserTurret.Look(info),
        "arcTurret" => Buildings.ArcTurret.Look(info),
        "shieldGenerator" => Buildings.ShieldGenerator.Look(info),
        "cannonTurret" => Buildings.CannonTurret.Look(info),
        _ => BuildingKit.Placeholder(info),
    };
}
