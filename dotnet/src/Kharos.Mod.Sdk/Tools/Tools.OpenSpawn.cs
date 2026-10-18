using System.Buffers.Binary;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 26 OpenSpawn: место, не запертое обрывами (ModAbi.ToolOpenSpawn, openSpawn в TS). Движок — Д1.
public static partial class Tools
{
    /// <summary>Ближайшее к (x, y) место появления, откуда техника выедет; не нашлось в radius — сама точка.</summary>
    public static (int X, int Y) OpenSpawn(int x, int y, int radius = 64)
    {
        Begin().I32(x).I32(y).U32((uint)radius);
        var output = Call(ModAbi.ToolOpenSpawn);
        return (BinaryPrimitives.ReadInt32LittleEndian(output), BinaryPrimitives.ReadInt32LittleEndian(output[4..]));
    }
}
