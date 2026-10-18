using System.Buffers.Binary;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 27 Stand: подъезд к цели (ModAbi.ToolStand). Движок — Д1.
public static partial class Tools
{
    /// <summary>Тайл, с которого entity достанет до target (здания), — с проверкой кармана и квотой тика; false — нет.</summary>
    public static bool Stand(uint entity, uint target, out int x, out int y, PathFlags flags = PathFlags.None)
    {
        Begin().U32(entity).U32(target).U32((uint)flags);
        var output = Call(ModAbi.ToolStand);
        x = BinaryPrimitives.ReadInt32LittleEndian(output[1..]);
        y = BinaryPrimitives.ReadInt32LittleEndian(output[5..]);
        return output[0] != 0;
    }
}
