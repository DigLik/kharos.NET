using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 21 Walkable: может ли стоять наземный (формат — ModAbi.ToolWalkable). Владелец — Д1; пока заглушка: ErrorUnknownId.
public partial class ModTools
{
    private partial int Walkable(GameWorld world, ref ToolInput r) => ModAbi.ErrorUnknownId;
}
