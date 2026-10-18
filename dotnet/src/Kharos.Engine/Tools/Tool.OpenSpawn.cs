using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 26 OpenSpawn: место, не запертое обрывами (openSpawn; формат — ModAbi.ToolOpenSpawn). Владелец — Д1.
public partial class ModTools
{
    private partial int OpenSpawn(GameWorld world, ref ToolInput r)
    {
        if (Navigation is not { } nav)
            return ModAbi.ErrorUnknownId;
        var (x, y, radius) = (r.I32(), r.I32(), r.U32());
        if (radius > 1024)
            throw new FormatException("OpenSpawn: radius больше 1024");
        var (spotX, spotY) = nav.OpenSpawn(x, y, (int)radius);
        I32(spotX);
        I32(spotY);
        return 0;
    }
}
