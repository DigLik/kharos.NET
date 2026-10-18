using System.Buffers.Binary;
using Kharos.Engine.World;

namespace Kharos.Engine.Tools;

// 12 BytesMany: поле-байты у многих сущностей разом (формат — ModAbi.ToolBytesMany). Владелец — Д5.
public partial class ModTools
{
    private partial int BytesMany(GameWorld world, ref ToolInput r)
    {
        var component = Component(r.U32());
        var index = r.U32();
        var store = world.Store(component);
        if (index >= (uint)store.Def.Fields.Count || store.Def.Fields[(int)index] is not { Kind: FieldKind.Bytes } field)
            throw new ArgumentException($"{store.Def.Name}: нет поля-байтов №{index}");
        var n = r.Count(4);
        for (var i = 0; i < n; i++)
        {
            var row = store.RowOf(r.U32());
            if (row < 0)
            {
                BinaryPrimitives.WriteInt32LittleEndian(Room(4), -1);
                continue;
            }
            var bytes = store.BytesAt(field.Slot, row);
            BinaryPrimitives.WriteInt32LittleEndian(Room(4), bytes.Length);
            bytes.CopyTo(Room(bytes.Length));
        }
        return 0;
    }
}
