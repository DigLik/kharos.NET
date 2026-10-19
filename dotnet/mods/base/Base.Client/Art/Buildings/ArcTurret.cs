using Kharos.Mod.Sdk.Client;
using static Base.Client.Buildings.KitB;

namespace Base.Client.Buildings;

/// <summary>Разрядная турель (ArcTurret) — перенос из buildingArt.ts: основание турели (emplacement) с кольцом: тёмно-синее. Башню рисует юнит-турель arc через Mount, на портрете она стоит в центре.</summary>
internal static class ArcTurret
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) => Emplacement(g, t, Arc[0]), mount: "arc");
}
