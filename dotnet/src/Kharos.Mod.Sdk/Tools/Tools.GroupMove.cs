using System.Buffers.Binary;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 23 GroupMove: места группе у цели (ModAbi.ToolGroupMove, orderGroupMove в TS). Движок — Д1.
public static partial class Tools
{
    /// <summary>
    /// Каждому юниту группы — свой тайл рядом с (x, y), по порядку списка: Direct — ехать напрямую (путь — только если
    /// упрётся), Air — летающему путь из одной точки, Skip — не трогать.
    /// </summary>
    public static GroupSlot[] GroupMove(int x, int y, ReadOnlySpan<uint> entities)
    {
        Begin().I32(x).I32(y);
        Entities(entities);
        var output = Call(ModAbi.ToolGroupMove);
        var slots = new GroupSlot[entities.Length];
        for (var i = 0; i < slots.Length; i++)
            slots[i] = new GroupSlot(BinaryPrimitives.ReadInt32LittleEndian(output[(i * 9)..]), BinaryPrimitives.ReadInt32LittleEndian(output[(i * 9 + 4)..]), (GroupMode)output[i * 9 + 8]);
        return slots;
    }
}
