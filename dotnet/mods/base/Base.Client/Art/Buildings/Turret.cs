using Kharos.Mod.Sdk.Client;
using static Base.Client.Buildings.KitB;

namespace Base.Client.Buildings;

/// <summary>Пулемётная турель (Turret) — перенос из buildingArt.ts: основание турели (emplacement) с кольцом: серое. Башню рисует юнит-турель gunner через Mount, на портрете она стоит в центре.</summary>
internal static class Turret
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) => Emplacement(g, t, BuildingKit.Iron[3]), mount: "gunner");
}
