using Kharos.Engine.Movement;
using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 27 Stand: подъезд к цели на длину луча, с проверкой кармана и нормой поиска тика (approach в inventory.ts; формат —
// ModAbi.ToolStand). Владелец — Д1.
public partial class ModTools
{
    private partial int Stand(GameWorld world, ref ToolInput r)
    {
        if (Navigation is not { } nav)
            return ModAbi.ErrorUnknownId;
        var entity = r.U32();
        var (x, y, w, h, radius, reach) = (r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64());
        var (pool, budget) = (r.U32(), r.U32());
        if (!world.Alive(entity))
            throw new ArgumentException($"Stand: нет сущности {entity}");
        var result = Navigation.StandResult.Cut;
        PathPlan? plan = null;
        nav.WithPool(pool, budget, Source.Tick, () => result = nav.Approach(entity, x, y, w, h, radius, reach, out plan));
        WritePlan((byte)result, plan);
        return 0;
    }
}
