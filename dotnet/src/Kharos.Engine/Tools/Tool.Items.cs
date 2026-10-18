using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 19 Items: предметы слоя в круге (формат — ModAbi.ToolItems). Владелец — Д3; пока заглушка: ErrorUnknownId.
public partial class ModTools
{
    private partial int Items(GameWorld world, ref ToolInput r) => ModAbi.ErrorUnknownId;
}
