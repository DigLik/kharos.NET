using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 29 Ray: стена на линии выстрела (формат — ModAbi.ToolRay). Владелец — Д1; пока заглушка: ErrorUnknownId.
public partial class ModTools
{
    private partial int Ray(GameWorld world, ref ToolInput r) => ModAbi.ErrorUnknownId;
}
