using Kharos.Mod.Sdk.Client;

namespace Base.Client.Buildings;

/// <summary>Плавильня (smelter, 3×3) — владелец Б-А (docs/dotnet-n6.md, Н6.5).</summary>
internal static class Smelter
{
    // TODO(Б-А): чертёж smelter из packages/client/src/game/buildings/buildingArt.ts; пока — заглушка по основанию и роли.
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Placeholder(info);
}
