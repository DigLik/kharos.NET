using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 28 Clear: наземные юниты в прямоугольнике (unitsIn) (формат — ModAbi.ToolClear). Владелец — Д1; пока заглушка: ErrorUnknownId.
public partial class ModTools
{
    private partial int Clear(GameWorld world, ref ToolInput r) => ModAbi.ErrorUnknownId;
}
