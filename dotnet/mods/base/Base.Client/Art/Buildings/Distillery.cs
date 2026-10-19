using Kharos.Mod.Sdk.Client;

namespace Base.Client.Buildings;

/// <summary>Нефтеперегонка (distillery, 2×3) — владелец Б-А (docs/dotnet-n6.md, Н6.5).</summary>
internal static class Distillery
{
    // TODO(Б-А): чертёж distillery из packages/client/src/game/buildings/buildingArt.ts; пока — заглушка по основанию и роли.
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Placeholder(info);
}
