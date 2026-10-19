using Kharos.Mod.Sdk.Client;

namespace Base.Client.Buildings;

/// <summary>Склад кремния (siliconStore, 2×2) — владелец Б-Б (docs/dotnet-n6.md, Н6.5).</summary>
internal static class SiliconStore
{
    // TODO(Б-Б): чертёж siliconStore из packages/client/src/game/buildings/buildingArt.ts; пока — заглушка по основанию и роли.
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Placeholder(info);
}
