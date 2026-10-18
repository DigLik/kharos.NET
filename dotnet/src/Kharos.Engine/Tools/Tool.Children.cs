using System.Buffers.Binary;
using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 14 Children: что держится на носителе — сущности с Mount.parent (формат — ModAbi.ToolChildren). Владелец — Д2.
public partial class ModTools
{
    private readonly Dictionary<uint, List<uint>> _children = [];

    private partial int Children(GameWorld world, ref ToolInput r)
    {
        var n = r.Count(4);
        var asked = r.Bytes(n * 4);
        _children.Clear();
        for (var i = 0; i < n; i++)
            _children.TryAdd(BinaryPrimitives.ReadUInt32LittleEndian(asked[(i * 4)..]), []);
        // Mount — компонент движка (Builtin.Mount); поле parent: 0 — ни на ком.
        if (world.Schema[Builtin.Mount].Field(ModAbi.MountParent) is { Columnar: true } parent)
        {
            var store = world.Store(Builtin.Mount);
            var parents = store.RawColumn(parent.Slot);
            var entities = store.Entities;
            for (var row = 0; row < entities.Length; row++)
            {
                var value = parents[row];
                if (value >= 1 && value <= uint.MaxValue && (uint)value == value && _children.TryGetValue((uint)value, out var list))
                    list.Add(entities[row]);
            }
        }
        for (var i = 0; i < n; i++)
        {
            var list = _children[BinaryPrimitives.ReadUInt32LittleEndian(asked[(i * 4)..])];
            list.Sort();
            U32((uint)list.Count);
            foreach (var child in list)
                U32(child);
        }
        return 0;
    }
}
