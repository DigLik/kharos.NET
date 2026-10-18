using System.Buffers.Binary;
using Kharos.Engine.World;

namespace Kharos.Engine.Tools;

// 11 Columns: пакетное чтение столбцов чужих сущностей (формат — ModAbi.ToolColumns). Владелец — Д5.
public partial class ModTools
{
    private partial int Columns(GameWorld world, ref ToolInput r)
    {
        var n = r.Count(4);
        var entities = r.Bytes(n * 4);
        var k = r.Count(8);
        var count = (long)k * n;
        if (count > int.MaxValue / 9)
            throw new FormatException($"Columns: {k}×{n} — слишком много");
        // Ответ одним куском: Room может перевыделить буфер, прежние спаны устарели бы.
        var block = Room((int)count * 9);
        var values = block[..((int)count * 8)];
        var has = block[((int)count * 8)..];
        for (var pair = 0; pair < k; pair++)
        {
            var component = Component(r.U32());
            var index = r.U32();
            var store = world.Store(component);
            if (index >= (uint)store.Def.Fields.Count || store.Def.Fields[(int)index] is not { Columnar: true } field)
                throw new ArgumentException($"{store.Def.Name}: нет поля-столбца №{index}");
            var column = store.RawColumn(field.Slot);
            for (var i = 0; i < n; i++)
            {
                var entity = BinaryPrimitives.ReadUInt32LittleEndian(entities[(i * 4)..]);
                var row = store.RowOf(entity);
                var at = pair * n + i;
                var value = row >= 0 ? Number(world, entity, component, field.Index, column[row]) : 0;
                BinaryPrimitives.WriteDoubleLittleEndian(values[(at * 8)..], value);
                has[at] = row >= 0 ? (byte)1 : (byte)0;
            }
        }
        return 0;
    }
}
