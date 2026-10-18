using System.Buffers.Binary;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 22 Plan: путь с квотой поиска тика (ModAbi.ToolPlan, orderMove в TS). Движок — Д1.
public static partial class Tools
{
    /// <summary>
    /// Путь сущности к тайлу (goalX, goalY): по центрам тайлов, сглаженный; стоящие юниты, кроме ignore, — препятствия;
    /// near — достаточно подойти на столько тайлов; limit 0 — по умолчанию. Тратит квоту поиска тика.
    /// </summary>
    public static PlanResult Plan(uint entity, int goalX, int goalY, double near = 0, int limit = 0, PathFlags flags = PathFlags.None, ReadOnlySpan<uint> ignore = default)
    {
        Begin().U32(entity).I32(goalX).I32(goalY).F64(near).U32((uint)limit).U32((uint)flags);
        Entities(ignore);
        var output = Call(ModAbi.ToolPlan);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(output[9..]);
        var points = new double[count * 2];
        for (var i = 0; i < points.Length; i++)
            points[i] = BinaryPrimitives.ReadDoubleLittleEndian(output[(13 + i * 8)..]);
        return new PlanResult((PlanStatus)output[0], BinaryPrimitives.ReadInt32LittleEndian(output[1..]), BinaryPrimitives.ReadInt32LittleEndian(output[5..]), points);
    }
}
