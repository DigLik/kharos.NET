using Kharos.Mod.Sdk.Client;

namespace Base.Client.Buildings;

/// <summary>Топливные баки (fuelTank, 2×2) — владелец Б-Б (docs/dotnet-n6.md, Н6.5).</summary>
internal static class FuelTank
{
    // TODO(Б-Б): чертёж fuelTank из packages/client/src/game/buildings/buildingArt.ts; пока — заглушка по основанию и роли.
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Placeholder(info);
}
