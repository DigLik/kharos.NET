using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 14 Children: что держится на носителе — Mount.parent (ModAbi.ToolChildren).
public static partial class Tools
{
    /// <summary>Сущности с Mount.parent = entity, по возрастанию номеров.</summary>
    public static uint[] Children(uint entity) => Children([entity])[0];

    /// <summary>То же пакетом.</summary>
    public static uint[][] Children(ReadOnlySpan<uint> entities)
    {
        Begin();
        Entities(entities);
        var output = Call(ModAbi.ToolChildren);
        var lists = new uint[entities.Length][];
        var at = 0;
        for (var i = 0; i < lists.Length; i++)
        {
            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(output[at..]);
            lists[i] = MemoryMarshal.Cast<byte, uint>(output.Slice(at + 4, count * 4)).ToArray();
            at += 4 + count * 4;
        }
        return lists;
    }
}
