using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 25 Reach: куда доедет техника — заливка проходимых (ModAbi.ToolReach, vehicleReach в TS). Движок — Д1.
public static partial class Tools
{
    /// <summary>Тайлы, куда наземный доедет из (x, y), не больше limit (сам тайл — первым).</summary>
    public static (int X, int Y)[] Reach(int x, int y, int limit, PathFlags flags = PathFlags.None)
    {
        Begin().I32(x).I32(y).U32((uint)limit).U32((uint)flags);
        return TilePairs(Call(ModAbi.ToolReach));
    }
}
