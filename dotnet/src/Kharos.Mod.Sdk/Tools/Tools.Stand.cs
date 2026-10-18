using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 27 Stand: подъезд к цели на длину луча (ModAbi.ToolStand, approach в inventory.ts). Движок — Д1.
public static partial class Tools
{
    /// <summary>
    /// Подъезд: место, откуда луч длиной reach достаёт до цели — прямоугольника w×h от (x, y) (здание, дроп) или круга
    /// радиуса r с центром (x, y) (юнит); ближнее к юниту, не в кармане и не на площадке. Found — путь положить в Path,
    /// Here — уже на месте, None — места нет (снять Path, промах), Cut — норма pool вышла до поиска (не промах).
    /// </summary>
    public static PlanResult Stand(uint entity, double x, double y, double w, double h, double r, double reach, uint pool = 0, uint budget = 0)
    {
        Begin().U32(entity).F64(x).F64(y).F64(w).F64(h).F64(r).F64(reach).U32(pool).U32(budget);
        return ReadPlan(Call(ModAbi.ToolStand));
    }
}
