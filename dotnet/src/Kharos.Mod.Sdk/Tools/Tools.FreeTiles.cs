using System.Buffers.Binary;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 24 FreeTiles: свободные места рядом с точкой (ModAbi.ToolFreeTiles, freeTilesNear в TS). Движок — Д1.
public static partial class Tools
{
    /// <summary>
    /// До count тайлов вокруг (x, y) через шаг группы, от ближних к дальним, с кольца from; стоящие юниты (кроме ignore)
    /// под корпус radius заняты; Air — места летающим.
    /// </summary>
    public static (int X, int Y)[] FreeTiles(int x, int y, int count, int from = 0, PathFlags flags = PathFlags.None, double radius = 0.5, ReadOnlySpan<uint> ignore = default)
    {
        Begin().I32(x).I32(y).U32((uint)count).U32((uint)from).U32((uint)flags).F64(radius);
        Entities(ignore);
        return TilePairs(Call(ModAbi.ToolFreeTiles));
    }

    /// <summary>Разбор count:u32 (x:i32 y:i32)×count.</summary>
    private static (int X, int Y)[] TilePairs(ReadOnlySpan<byte> output)
    {
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(output);
        var tiles = new (int, int)[count];
        for (var i = 0; i < count; i++)
            tiles[i] = (BinaryPrimitives.ReadInt32LittleEndian(output[(4 + i * 8)..]), BinaryPrimitives.ReadInt32LittleEndian(output[(8 + i * 8)..]));
        return tiles;
    }
}
