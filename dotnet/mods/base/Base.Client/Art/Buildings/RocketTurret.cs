using Kharos.Mod.Sdk.Client;
using static Base.Client.Buildings.KitB;

namespace Base.Client.Buildings;

/// <summary>Ракетная турель (RocketTurret) — перенос из buildingArt.ts: основание турели (emplacement) с кольцом: ржаво-красное. Башню рисует юнит-турель rocket через Mount, на портрете она стоит в центре.</summary>
internal static class RocketTurret
{
    public static LookSpec Look(BuildingInfo info) => KitB.Look(info.Width, info.Height, (g, t, _, _) => Emplacement(g, t, BuildingKit.Rust[1]), mount: "rocket");
}
