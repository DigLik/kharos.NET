using System.Buffers.Binary;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 29 Ray: стена на линии выстрела (ModAbi.ToolRay). Движок — Д1.
public static partial class Tools
{
    /// <summary>Ответ <see cref="Ray"/>: линию закрывает местность.</summary>
    public const uint RayTerrain = ModAbi.RayTerrain;

    /// <summary>Что закрывает линию: 0 — ничего, иначе здание-стена; <see cref="RayTerrain"/> — местность.</summary>
    public static uint Ray(double fromX, double fromY, double toX, double toY)
    {
        Begin().U32(1).F64(fromX).F64(fromY).F64(toX).F64(toY);
        return BinaryPrimitives.ReadUInt32LittleEndian(Call(ModAbi.ToolRay));
    }
}
