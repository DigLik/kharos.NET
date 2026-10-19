using Kharos.Mod.Sdk.Client;

namespace Base.Client.Buildings;

/// <summary>Лазерная турель (laserTurret, 1×1) — владелец Б-Б (docs/dotnet-n6.md, Н6.5).</summary>
internal static class LaserTurret
{
    // TODO(Б-Б): чертёж laserTurret из packages/client/src/game/buildings/buildingArt.ts; пока — заглушка по основанию и роли.
    public static LookSpec Look(BuildingInfo info) => BuildingKit.Placeholder(info);
}
