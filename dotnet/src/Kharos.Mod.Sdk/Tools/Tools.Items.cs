using System.Buffers.Binary;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 19 Items: предметы слоя в круге (ModAbi.ToolItems). Движок — Д3.
public static partial class Tools
{
    /// <summary>Предметы слоя с местом в круге (x, y, r): по ячейкам, в ячейке — по порядку записи; owner 0 — мир.</summary>
    public static MapItem[] Items(LayerRef layer, uint owner, double x, double y, double radius)
    {
        Begin().U32((uint)layer.Id).U32(owner).F64(x).F64(y).F64(radius);
        var output = Call(ModAbi.ToolItems);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(output);
        var fields = layer.Fields.Count;
        var items = new MapItem[count];
        var at = 4;
        for (var i = 0; i < count; i++)
        {
            var values = new double[fields];
            for (var f = 0; f < fields; f++)
                values[f] = BinaryPrimitives.ReadDoubleLittleEndian(output[(at + 16 + f * 8)..]);
            items[i] = new MapItem(BinaryPrimitives.ReadDoubleLittleEndian(output[at..]), BinaryPrimitives.ReadDoubleLittleEndian(output[(at + 8)..]), values);
            at += 16 + fields * 8;
        }
        return items;
    }
}
