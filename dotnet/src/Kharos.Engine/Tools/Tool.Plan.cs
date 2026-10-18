using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 22 Plan: путь с квотой тика (orderMove) (формат — ModAbi.ToolPlan). Владелец — Д1; пока заглушка: ErrorUnknownId.
public partial class ModTools
{
    private partial int Plan(GameWorld world, ref ToolInput r) => ModAbi.ErrorUnknownId;
}
