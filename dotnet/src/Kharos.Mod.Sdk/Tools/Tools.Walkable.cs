using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 21 Walkable: может ли стоять наземный (ModAbi.ToolWalkable). Движок — Д1.
public static partial class Tools
{
    /// <summary>Может ли наземный стоять в тайле (isWalkable в TS); Foot — пехота, ей можно и к подножию обрыва.</summary>
    public static bool Walkable(int x, int y, PathFlags flags = PathFlags.None)
    {
        Begin().U32((uint)flags).U32(1).I32(x).I32(y);
        return Call(ModAbi.ToolWalkable)[0] != 0;
    }

    /// <summary>То же пакетом: x, y тайлов подряд.</summary>
    public static void Walkable(ReadOnlySpan<int> xy, Span<bool> into, PathFlags flags = PathFlags.None)
    {
        Begin().U32((uint)flags).U32((uint)(xy.Length / 2));
        foreach (var value in xy)
            I32(value);
        var output = Call(ModAbi.ToolWalkable);
        for (var i = 0; i < xy.Length / 2; i++)
            into[i] = output[i] != 0;
    }
}
