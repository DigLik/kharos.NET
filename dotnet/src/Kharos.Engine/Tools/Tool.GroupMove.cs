using Kharos.Engine.Movement;
using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 23 GroupMove: места группе у цели (orderGroupMove; формат — ModAbi.ToolGroupMove). Владелец — Д1.
public partial class ModTools
{
    private partial int GroupMove(GameWorld world, ref ToolInput r)
    {
        if (Navigation is not { } nav)
            return ModAbi.ErrorUnknownId;
        var (x, y) = (r.I32(), r.I32());
        var n = r.Count(4);
        var units = new uint[n];
        for (var i = 0; i < n; i++)
            units[i] = r.U32();
        var slots = new (int X, int Y, Navigation.GroupMode Mode)[n];
        nav.GroupMove(units, x, y, slots);
        foreach (var (goalX, goalY, mode) in slots)
        {
            I32(goalX);
            I32(goalY);
            Room(1)[0] = (byte)mode;
        }
        return 0;
    }
}
