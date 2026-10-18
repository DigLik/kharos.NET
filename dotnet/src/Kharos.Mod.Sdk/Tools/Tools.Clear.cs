using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 28 Clear: наземные юниты в прямоугольнике (ModAbi.ToolClear, unitsIn в TS). Движок — Д1.
public static partial class Tools
{
    /// <summary>Наземные юниты с центром в прямоугольнике w×h с (x, y), кроме ignore, — по возрастанию номеров.</summary>
    public static uint[] Clear(int x, int y, int w, int h, uint ignore = 0)
    {
        Begin().I32(x).I32(y).U32((uint)w).U32((uint)h).U32(ignore);
        var output = Call(ModAbi.ToolClear);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(output);
        return MemoryMarshal.Cast<byte, uint>(output.Slice(4, count * 4)).ToArray();
    }
}
