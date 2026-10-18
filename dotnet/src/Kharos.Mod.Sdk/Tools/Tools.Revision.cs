using System.Buffers.Binary;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 13 Revision: ревизии мира — ключ кэша мода (ModAbi.ToolRevision).
public static partial class Tools
{
    /// <summary>
    /// Ревизии мира, мест, земли, слоёв и тик. Кэш, производный от мира (зоны, графы сетей), держат в экземпляре мода с
    /// этим ключом: экземпляр могут выбросить, но пересчёт по ключу даст то же.
    /// </summary>
    public static WorldRevision Revision()
    {
        Begin();
        var output = Call(ModAbi.ToolRevision);
        return new WorldRevision(
            BinaryPrimitives.ReadUInt64LittleEndian(output),
            BinaryPrimitives.ReadUInt64LittleEndian(output[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(output[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(output[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(output[32..]));
    }
}
