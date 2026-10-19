using Kharos.Mod.Sdk.Client;

namespace Base.Client.Buildings;

/// <summary>Труба (pipe, 1×1) — владелец Б-Б (docs/dotnet-n6.md, Н6.5).</summary>
internal static class Pipe
{
    // TODO(Б-Б): чертёж pipe из packages/client/src/game/buildings/buildingArt.ts; пока — заглушка по основанию и роли.
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Placeholder(info);
}
