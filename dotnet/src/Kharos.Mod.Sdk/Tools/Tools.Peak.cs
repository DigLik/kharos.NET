using System.Buffers.Binary;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 20 Peak: вершина горы тайла (ModAbi.ToolPeak). Движок — Д3.
public static partial class Tools
{
    /// <summary>Вершина горы, частью которой стоит тайл; false — тайл не гора.</summary>
    public static bool Peak(int x, int y, out double peakX, out double peakY, out double radius)
    {
        Begin().U32(1).I32(x).I32(y);
        var output = Call(ModAbi.ToolPeak);
        peakX = BinaryPrimitives.ReadDoubleLittleEndian(output[1..]);
        peakY = BinaryPrimitives.ReadDoubleLittleEndian(output[9..]);
        radius = BinaryPrimitives.ReadDoubleLittleEndian(output[17..]);
        return output[0] != 0;
    }
}
