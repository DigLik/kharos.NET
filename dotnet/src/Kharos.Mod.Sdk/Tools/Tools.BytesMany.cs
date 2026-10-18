using System.Buffers.Binary;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 12 BytesMany: поле-байты у многих сущностей разом (ModAbi.ToolBytesMany).
public static partial class Tools
{
    /// <summary>Байты поля у каждой сущности; нет компонента — null.</summary>
    public static byte[]?[] BytesMany(ReadOnlySpan<uint> entities, ComponentRef component, FieldRef field)
    {
        if (field.Kind != FieldKind.Bytes)
            throw new ArgumentException($"{component.Name}.{field.Name} — не байты");
        Begin().U32((uint)component.Id).U32((uint)field.Index);
        Entities(entities);
        var output = Call(ModAbi.ToolBytesMany);
        var result = new byte[]?[entities.Length];
        var at = 0;
        for (var i = 0; i < result.Length; i++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(output[at..]);
            at += 4;
            if (length < 0)
                continue;
            result[i] = output.Slice(at, length).ToArray();
            at += length;
        }
        return result;
    }
}
