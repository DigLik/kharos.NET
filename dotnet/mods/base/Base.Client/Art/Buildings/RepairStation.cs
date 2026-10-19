using Kharos.Mod.Sdk.Client;

namespace Base.Client.Buildings;

/// <summary>Ремонтная станция (repairStation, 3×3) — владелец Б-А (docs/dotnet-n6.md, Н6.5).</summary>
internal static class RepairStation
{
    // TODO(Б-А): чертёж repairStation из packages/client/src/game/buildings/buildingArt.ts; пока — заглушка по основанию и роли.
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Placeholder(info);
}
