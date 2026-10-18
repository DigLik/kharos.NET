using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 18 Layer: значения канала слоя-сетки в прямоугольнике (формат — ModAbi.ToolLayer). Владелец — Д3; пока заглушка: ErrorUnknownId.
public partial class ModTools
{
    private partial int Layer(GameWorld world, ref ToolInput r) => ModAbi.ErrorUnknownId;
}
