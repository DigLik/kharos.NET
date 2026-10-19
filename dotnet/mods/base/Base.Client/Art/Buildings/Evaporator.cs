using Kharos.Mod.Sdk.Client;

namespace Base.Client.Buildings;

/// <summary>Выпарка лития (evaporator, 3×2) — владелец Б-А (docs/dotnet-n6.md, Н6.5).</summary>
internal static class Evaporator
{
    // TODO(Б-А): чертёж evaporator из packages/client/src/game/buildings/buildingArt.ts; пока — заглушка по основанию и роли.
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Placeholder(info);
}
