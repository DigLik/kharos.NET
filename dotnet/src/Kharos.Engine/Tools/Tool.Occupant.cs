using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 17 Occupant: здание на тайле (occupancy.at; формат — ModAbi.ToolOccupant). Владелец — Д2.
public partial class ModTools
{
    private partial int Occupant(GameWorld world, ref ToolInput r)
    {
        if (Navigation is not { } nav)
            return ModAbi.ErrorUnknownId;
        var n = r.Count(8);
        for (var i = 0; i < n; i++)
        {
            var (x, y) = (r.I32(), r.I32());
            U32(nav.Occupancy.At(x, y));
        }
        return 0;
    }
}
