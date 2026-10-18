using System.Buffers.Binary;
using Kharos.Engine.World;

namespace Kharos.Engine.Tools;

// 13 Revision: ревизии мира, мест, земли, слоёв и тик — ключ кэша мода (формат — ModAbi.ToolRevision). Владелец — Д5.
public partial class ModTools
{
    private partial int Revision(GameWorld world, ref ToolInput r)
    {
        var into = Room(40);
        BinaryPrimitives.WriteUInt64LittleEndian(into, world.Revision);
        BinaryPrimitives.WriteUInt64LittleEndian(into[8..], world.PositionRevision);
        BinaryPrimitives.WriteUInt64LittleEndian(into[16..], (ulong)(Source.Land?.Revision ?? 0));
        BinaryPrimitives.WriteUInt64LittleEndian(into[24..], Source.Layers?.Revision ?? 0);
        BinaryPrimitives.WriteUInt64LittleEndian(into[32..], Source.Tick);
        return 0;
    }
}
