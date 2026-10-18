using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 24 FreeTiles: свободные места рядом (freeTilesNear; формат — ModAbi.ToolFreeTiles). Владелец — Д1.
public partial class ModTools
{
    private readonly List<int> _freeTiles = [];

    private partial int FreeTiles(GameWorld world, ref ToolInput r)
    {
        if (Navigation is not { } nav)
            return ModAbi.ErrorUnknownId;
        var (x, y, count, from, flags, radius) = (r.I32(), r.I32(), r.U32(), r.U32(), r.U32(), r.F64());
        var ignore = Ignore(ref r);
        if (count > MaxToolTiles || from > 1024)
            throw new FormatException("FreeTiles: count или from слишком велики");
        var air = (flags & ModAbi.PlanAir) != 0;
        HashSet<long>? blocked = null;
        // Отрицательный корпус — стоящие не в счёт (freeTilesNear без blocked).
        if (radius >= 0)
        {
            blocked = [];
            nav.StandingUnits(ignore, radius, air, (flags & ModAbi.PlanJam) != 0, blocked);
        }
        nav.FreeTilesNear(x, y, (int)count, (int)from, blocked, air, _freeTiles, (flags & ModAbi.PlanFoot) != 0);
        U32((uint)(_freeTiles.Count / 2));
        foreach (var value in _freeTiles)
            I32(value);
        return 0;
    }

    /// <summary>Список n:u32 entity:u32×n — набором.</summary>
    private static HashSet<uint> Ignore(ref ToolInput r)
    {
        var n = r.Count(4);
        var ignore = new HashSet<uint>(n);
        for (var i = 0; i < n; i++)
            ignore.Add(r.U32());
        return ignore;
    }
}
