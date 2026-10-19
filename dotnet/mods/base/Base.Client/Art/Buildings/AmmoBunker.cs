using Kharos.Mod.Sdk.Client;

namespace Base.Client.Buildings;

/// <summary>Бункер боеприпасов (ammoBunker, 2×1) — владелец Б-Б (docs/dotnet-n6.md, Н6.5).</summary>
internal static class AmmoBunker
{
    // TODO(Б-Б): чертёж ammoBunker из packages/client/src/game/buildings/buildingArt.ts; пока — заглушка по основанию и роли.
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Placeholder(info);
}
