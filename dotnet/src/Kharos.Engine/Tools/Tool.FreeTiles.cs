using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 24 FreeTiles: свободные места рядом (freeTilesNear) (формат — ModAbi.ToolFreeTiles). Владелец — Д1; пока заглушка: ErrorUnknownId.
public partial class ModTools
{
    private partial int FreeTiles(GameWorld world, ref ToolInput r) => ModAbi.ErrorUnknownId;
}
