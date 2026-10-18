using Kharos.Engine.Movement;
using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 25 Reach: заливка проходимых (vehicleReach; формат — ModAbi.ToolReach). Владелец — Д1.
public partial class ModTools
{
    private readonly HashSet<long> _reached = [];
    private readonly List<long> _reachOrder = [];

    private partial int Reach(GameWorld world, ref ToolInput r)
    {
        if (Navigation is not { } nav)
            return ModAbi.ErrorUnknownId;
        var (x, y, limit, flags) = (r.I32(), r.I32(), r.U32(), r.U32());
        if (limit > MaxToolTiles)
            throw new FormatException($"Reach: limit больше {MaxToolTiles}");
        nav.VehicleReach(x, y, (int)limit, (flags & ModAbi.PlanFoot) != 0, _reached, _reachOrder);
        U32((uint)_reachOrder.Count);
        foreach (var key in _reachOrder)
        {
            var (tileX, tileY) = Navigation.Unkey(key);
            I32(tileX);
            I32(tileY);
        }
        return 0;
    }
}
