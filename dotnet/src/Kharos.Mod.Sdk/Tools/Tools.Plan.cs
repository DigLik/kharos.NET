using System.Buffers.Binary;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 22 Plan: путь с нормой поиска тика (ModAbi.ToolPlan, orderMove в TS). Движок — Д1.
public static partial class Tools
{
    /// <summary>
    /// Путь сущности к тайлу (goalX, goalY): по центрам тайлов, сглаженный; стоящие юниты, кроме ignore (и самого
    /// юнита), — препятствия; near — достаточно подойти на столько тайлов; limit 0 — по умолчанию; Jam — затор. pool ≠ 0 —
    /// норма: все поиски с этим pool за тик вместе осматривают не больше budget тайлов (вышла — <see cref="PlanStatus.Cut"/>).
    /// Found — положить в Path (points, goalX/Y, wait 0, roads у наземных, direct и stuck false, best −1), None — снять
    /// Path, Cut — оставить прежний.
    /// </summary>
    public static PlanResult Plan(uint entity, int goalX, int goalY, double near = 0, int limit = 0, PathFlags flags = PathFlags.None, uint pool = 0, uint budget = 0, ReadOnlySpan<uint> ignore = default)
    {
        Begin().U32(entity).I32(goalX).I32(goalY).F64(near).U32((uint)limit).U32((uint)flags).U32(pool).U32(budget);
        Entities(ignore);
        return ReadPlan(Call(ModAbi.ToolPlan));
    }

    /// <summary>Разбор ответа Plan и Stand: status:u8 roads:u8 goalX:i32 goalY:i32 count:u32 (x:f64 y:f64)×count.</summary>
    private static PlanResult ReadPlan(ReadOnlySpan<byte> output)
    {
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(output[10..]);
        var points = new double[count * 2];
        for (var i = 0; i < points.Length; i++)
            points[i] = BinaryPrimitives.ReadDoubleLittleEndian(output[(14 + i * 8)..]);
        return new PlanResult((PlanStatus)output[0], BinaryPrimitives.ReadInt32LittleEndian(output[2..]), BinaryPrimitives.ReadInt32LittleEndian(output[6..]), points, output[1] != 0);
    }
}
