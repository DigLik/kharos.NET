using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 23 GroupMove: места группе (orderGroupMove) (формат — ModAbi.ToolGroupMove). Владелец — Д1; пока заглушка: ErrorUnknownId.
public partial class ModTools
{
    private partial int GroupMove(GameWorld world, ref ToolInput r) => ModAbi.ErrorUnknownId;
}
