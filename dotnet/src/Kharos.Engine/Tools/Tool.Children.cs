using System.Buffers.Binary;
using Kharos.Engine.World;
using Kharos.Mod.Abi;

namespace Kharos.Engine.Tools;

// 14 Children: что держится на носителе — сущности с Mount.parent (формат — ModAbi.ToolChildren). Владелец — Д2.
public partial class ModTools
{
    /// <summary>
    /// Турели по носителям — из хранилища Mount, пока оно не менялось (<see cref="ComponentStore.Version"/>): вызов на
    /// каждый приказ атаки обходил бы все турели мира, а в бою их тысячи.
    /// </summary>
    private readonly Dictionary<uint, List<uint>> _children = [];
    private readonly Stack<List<uint>> _freeChildren = [];
    private ComponentStore? _childrenStore;
    private ulong _childrenVersion;

    private static readonly List<uint> NoChildren = [];

    private partial int Children(GameWorld world, ref ToolInput r)
    {
        var n = r.Count(4);
        var asked = r.Bytes(n * 4);
        IndexChildren(world);
        for (var i = 0; i < n; i++)
        {
            var list = _children.GetValueOrDefault(BinaryPrimitives.ReadUInt32LittleEndian(asked[(i * 4)..])) ?? NoChildren;
            U32((uint)list.Count);
            foreach (var child in list)
                U32(child);
        }
        return 0;
    }

    /// <summary>Носитель → его турели по возрастанию номеров; пересчёт — только если Mount изменился.</summary>
    private void IndexChildren(GameWorld world)
    {
        var store = world.Store(Builtin.Mount);
        if (ReferenceEquals(store, _childrenStore) && store.Version == _childrenVersion)
            return;
        _childrenStore = store;
        _childrenVersion = store.Version;
        foreach (var list in _children.Values)
        {
            list.Clear();
            _freeChildren.Push(list);
        }
        _children.Clear();
        // Mount — компонент движка (Builtin.Mount); поле parent: 0 — ни на ком.
        if (world.Schema[Builtin.Mount].Field(ModAbi.MountParent) is not { Columnar: true } parent)
            return;
        var parents = store.RawColumn(parent.Slot);
        var entities = store.Entities;
        for (var row = 0; row < entities.Length; row++)
        {
            var value = parents[row];
            if (value < 1 || value > uint.MaxValue || (uint)value != value)
                continue;
            if (!_children.TryGetValue((uint)value, out var list))
                _children[(uint)value] = list = _freeChildren.Count > 0 ? _freeChildren.Pop() : [];
            list.Add(entities[row]);
        }
        foreach (var list in _children.Values)
            list.Sort();
    }
}
