using Kharos.Mod.Sdk.Client;
using static Base.Client.Buildings.KitB;

namespace Base.Client.Buildings;

/// <summary>Пушечная турель (CannonTurret) — перенос из buildingArt.ts: основание турели (emplacement) с кольцом: золотое. Башню рисует юнит-турель cannon через Mount, на портрете она стоит в центре.</summary>
internal static class CannonTurret
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) => Emplacement(g, t, Gold[1]), mount: "cannon");
}
