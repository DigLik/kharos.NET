using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 27 Stand: подъезд к цели с карманом и квотой (формат — ModAbi.ToolStand). Владелец — Д1; пока заглушка: ErrorUnknownId.
public partial class ModTools
{
    private partial int Stand(GameWorld world, ref ToolInput r) => ModAbi.ErrorUnknownId;
}
