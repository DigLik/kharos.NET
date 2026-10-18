using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 26 OpenSpawn: незапертое место (openSpawn) (формат — ModAbi.ToolOpenSpawn). Владелец — Д1; пока заглушка: ErrorUnknownId.
public partial class ModTools
{
    private partial int OpenSpawn(GameWorld world, ref ToolInput r) => ModAbi.ErrorUnknownId;
}
