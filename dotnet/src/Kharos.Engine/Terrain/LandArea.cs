namespace Kharos.Engine.Terrain;

/// <summary>Прямоугольник чанков: левый верхний чанк и размер, в чанках.</summary>
public readonly record struct LandArea(int Left, int Top, int Width, int Height)
{
    /// <summary>Чанки, покрывающие прямоугольник тайлов [left, right) × [top, bottom) (<c>areaOf</c>).</summary>
    public static LandArea Of(int left, int top, int right, int bottom)
    {
        int fromX = left >> TileFormat.ChunkShift;
        int fromY = top >> TileFormat.ChunkShift;
        return new(fromX, fromY, CeilChunk(right) - fromX, CeilChunk(bottom) - fromY);
    }

    /// <summary>Номер чанка в области (y·Width + x) или −1, если чанк вне её.</summary>
    public int IndexOf(int chunkX, int chunkY)
    {
        int x = chunkX - Left;
        int y = chunkY - Top;
        return x >= 0 && y >= 0 && x < Width && y < Height ? y * Width + x : -1;
    }

    public int Count => Width * Height;

    private static int CeilChunk(int tile) => -(-tile >> TileFormat.ChunkShift);
}
