using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 16 Tiles: тайлы прямоугольника — карта, проходимость, здания, ворота, юниты (формат — ModAbi.ToolTiles). Владелец — Д2; пока заглушка: ErrorUnknownId.
public partial class ModTools
{
    private partial int Tiles(GameWorld world, ref ToolInput r) => ModAbi.ErrorUnknownId;
}
