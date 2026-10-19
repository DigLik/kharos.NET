using Kharos.Mod.Sdk.Client;

namespace Base.Client.Buildings;

/// <summary>Аэродром (airfield, 4×3) — владелец Б-А (docs/dotnet-n6.md, Н6.5).</summary>
internal static class Airfield
{
    // TODO(Б-А): чертёж airfield из packages/client/src/game/buildings/buildingArt.ts; пока — заглушка по основанию и роли.
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Placeholder(info);
}
