using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 17 Occupant: здание на тайле (формат — ModAbi.ToolOccupant). Владелец — Д2; пока заглушка: ErrorUnknownId.
public partial class ModTools
{
    private partial int Occupant(GameWorld world, ref ToolInput r) => ModAbi.ErrorUnknownId;
}
