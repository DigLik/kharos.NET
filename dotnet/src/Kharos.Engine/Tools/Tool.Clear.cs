using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 28 Clear: наземные юниты в прямоугольнике (unitsIn; формат — ModAbi.ToolClear). Владелец — Д1.
public partial class ModTools
{
    private readonly List<uint> _inside = [];

    private partial int Clear(GameWorld world, ref ToolInput r)
    {
        if (Navigation is not { } nav)
            return ModAbi.ErrorUnknownId;
        var (x, y, w, h, ignore) = (r.I32(), r.I32(), r.U32(), r.U32(), r.U32());
        nav.UnitsIn(x, y, w, h, ignore, _inside);
        U32((uint)_inside.Count);
        foreach (var entity in _inside)
            U32(entity);
        return 0;
    }
}
