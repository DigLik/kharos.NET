using System.Runtime.InteropServices;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 16 Tiles: тайлы прямоугольника — карта, проходимость, здания, ворота, юниты (ModAbi.ToolTiles). Движок — Д2.
public static partial class Tools
{
    /// <summary>Биты тайлов прямоугольника w×h с (x, y) построчно в into.</summary>
    public static void Tiles(int x, int y, int w, int h, Span<TileFlags> into)
    {
        Begin().I32(x).I32(y).U32((uint)w).U32((uint)h);
        Call(ModAbi.ToolTiles)[..(w * h)].CopyTo(MemoryMarshal.AsBytes(into));
    }

    /// <summary>Биты одного тайла.</summary>
    public static TileFlags Tile(int x, int y)
    {
        Span<TileFlags> one = stackalloc TileFlags[1];
        Tiles(x, y, 1, 1, one);
        return one[0];
    }
}
