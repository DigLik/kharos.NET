using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 20 Peak: вершина горы тайла (формат — ModAbi.ToolPeak). Владелец — Д3; пока заглушка: ErrorUnknownId.
public partial class ModTools
{
    private partial int Peak(GameWorld world, ref ToolInput r) => ModAbi.ErrorUnknownId;
}
