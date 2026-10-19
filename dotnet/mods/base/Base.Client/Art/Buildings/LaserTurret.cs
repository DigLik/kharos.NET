using Kharos.Mod.Sdk.Client;
using static Base.Client.Buildings.KitB;

namespace Base.Client.Buildings;

/// <summary>Лазерная турель (LaserTurret) — перенос из buildingArt.ts: основание турели (emplacement) с кольцом: голубое. Башню рисует юнит-турель laser через Mount, на портрете она стоит в центре.</summary>
internal static class LaserTurret
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) => Emplacement(g, t, Arc[1]), mount: "laser");
}
