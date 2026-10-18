using Kharos.Engine.Movement;
using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 22 Plan: путь с нормой поиска тика (orderMove; формат — ModAbi.ToolPlan). Владелец — Д1.
public partial class ModTools
{
    private partial int Plan(GameWorld world, ref ToolInput r)
    {
        if (Navigation is not { } nav)
            return ModAbi.ErrorUnknownId;
        var entity = r.U32();
        var (goalX, goalY, near, limit, flags, pool, budget) = (r.I32(), r.I32(), r.F64(), r.U32(), r.U32(), r.U32(), r.U32());
        var ignore = Ignore(ref r);
        if (!world.Alive(entity))
            throw new ArgumentException($"Plan: нет сущности {entity}");
        PathPlan? plan = null;
        var tries = (flags & ModAbi.PlanJam) != 0 ? Navigation.JamTries : 0;
        var ran = nav.WithPool(pool, budget, Source.Tick, () => plan = nav.OrderMove(entity, goalX, goalY, ignore.Count == 0 ? null : ignore, tries, near, limit == 0 ? null : limit));
        WritePlan(ran && plan is not null ? plan.Result switch
        {
            Moved.Set => ModAbi.PlanFound,
            Moved.Removed => ModAbi.PlanNone,
            _ => ModAbi.PlanCut,
        } : ModAbi.PlanCut, plan);
        return 0;
    }

    /// <summary>Ответ Plan и Stand: status:u8 roads:u8 goalX:i32 goalY:i32 count:u32 (x:f64 y:f64)×count.</summary>
    private void WritePlan(byte status, PathPlan? plan)
    {
        var found = plan is { Result: Moved.Set };
        var into = Room(2);
        into[0] = status;
        into[1] = found && plan!.Roads ? (byte)1 : (byte)0;
        I32(found ? plan!.GoalX : 0);
        I32(found ? plan!.GoalY : 0);
        var points = found ? plan!.Points : null;
        U32((uint)((points?.Count ?? 0) / 2));
        if (points is not null)
            foreach (var value in points)
                F64(value);
    }
}
