using Kharos.Mod.Sdk.Client;

namespace Base.Client.Buildings;

/// <summary>Завод стройблоков (blockPlant, 2×2) — владелец Б-А (docs/dotnet-n6.md, Н6.5).</summary>
internal static class BlockPlant
{
    // TODO(Б-А): чертёж blockPlant из packages/client/src/game/buildings/buildingArt.ts; пока — заглушка по основанию и роли.
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Placeholder(info);
}
