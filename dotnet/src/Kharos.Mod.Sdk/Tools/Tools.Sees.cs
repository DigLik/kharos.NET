using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 15 Sees: обзор игрока в точках (ModAbi.ToolSees).
public static partial class Tools
{
    /// <summary>Что игрок знает о точке: 0 — не разведана, 1 — разведана, 2 — видна сейчас (без тумана — 2).</summary>
    public static byte Sees(uint player, double x, double y)
    {
        Begin().U32(player).U32(1).F64(x).F64(y);
        return Call(ModAbi.ToolSees)[0];
    }

    /// <summary>То же пакетом.</summary>
    public static void Sees(uint player, ReadOnlySpan<double> x, ReadOnlySpan<double> y, Span<byte> into)
    {
        Begin().U32(player).U32((uint)x.Length);
        for (var i = 0; i < x.Length; i++)
            F64(x[i]).F64(y[i]);
        Call(ModAbi.ToolSees)[..x.Length].CopyTo(into);
    }
}
