using Kharos.Mod.Sdk.Client;

namespace Base.Client.Buildings;

/// <summary>Главное здание (command, 3×3) — владелец Б-А (docs/dotnet-n6.md, Н6.5).</summary>
internal static class Command
{
    // TODO(Б-А): чертёж command из packages/client/src/game/buildings/buildingArt.ts; пока — заглушка по основанию и роли.
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Placeholder(info);
}
