using Kharos.Mod.Sdk.Client;

namespace Base.Client.Buildings;

/// <summary>Урановый контейнер (uraniumCask, 1×1) — владелец Б-Б (docs/dotnet-n6.md, Н6.5).</summary>
internal static class UraniumCask
{
    // TODO(Б-Б): чертёж uraniumCask из packages/client/src/game/buildings/buildingArt.ts; пока — заглушка по основанию и роли.
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Placeholder(info);
}
