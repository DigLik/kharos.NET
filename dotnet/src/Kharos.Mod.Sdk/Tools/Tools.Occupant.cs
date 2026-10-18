using System.Buffers.Binary;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 17 Occupant: здание на тайле (ModAbi.ToolOccupant). Движок — Д2.
public static partial class Tools
{
    /// <summary>Здание (Footprint) на тайле; 0 — свободно.</summary>
    public static uint Occupant(int x, int y)
    {
        Begin().U32(1).I32(x).I32(y);
        return BinaryPrimitives.ReadUInt32LittleEndian(Call(ModAbi.ToolOccupant));
    }

    /// <summary>То же пакетом: x, y тайлов подряд.</summary>
    public static void Occupants(ReadOnlySpan<int> xy, Span<uint> into)
    {
        Begin().U32((uint)(xy.Length / 2));
        foreach (var value in xy)
            I32(value);
        var output = Call(ModAbi.ToolOccupant);
        for (var i = 0; i < xy.Length / 2; i++)
            into[i] = BinaryPrimitives.ReadUInt32LittleEndian(output[(i * 4)..]);
    }
}
