using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 25 Reach: заливка проходимых (vehicleReach) (формат — ModAbi.ToolReach). Владелец — Д1; пока заглушка: ErrorUnknownId.
public partial class ModTools
{
    private partial int Reach(GameWorld world, ref ToolInput r) => ModAbi.ErrorUnknownId;
}
