using System.Buffers.Binary;
using System.IO.Compression;
using static Kharos.Engine.Terrain.TileFormat;

namespace Kharos.Engine.Terrain;

// Карта мира байтами — формат saveLand из TS, байт в байт: заголовок, затем тайлы плоскостями.
public sealed partial class Land
{
    /// <summary>Версия упаковки карты.</summary>
    private const byte LandFormat = 1;
    /// <summary>Формат (1 байт) и область в чанках: left, top, width, height — int32 LE.</summary>
    private const int LandHeader = 1 + 4 * 4;
    /// <summary>Предел распакованной карты: защита от сжатой бомбы.</summary>
    private const int MaxUnpackedBytes = 256 << 20;

    /// <summary>
    /// Карта мира в байты (<c>saveLand</c>). Тайлы лежат плоскостями — сначала все типы, потом биомы и т. д.: однородное
    /// подряд сжимается в разы лучше. Выводимое не хранится. Несобранные чанки собираются.
    /// </summary>
    public byte[] Save()
    {
        if (Area is not { } area) throw new InvalidOperationException("У местности нет карты мира");
        // Сначала все несобранные чанки, потом их выводимое: тогда соседи берутся из карты, а не из генератора заново.
        var fresh = new List<int>();
        for (int n = 0; n < _owned.Length; n++)
        {
            if (_owned[n] is not null) continue;
            _owned[n] = TerrainGenerator.GenerateChunk(Config, area.Left + n % area.Width, area.Top + n / area.Width);
            fresh.Add(n);
        }
        foreach (int n in fresh) DeriveChunk(_owned[n]!, area.Left + n % area.Width, area.Top + n / area.Width);

        int tiles = TileCount(area);
        var bytes = new byte[LandHeader + tiles * TileBytes];
        bytes[0] = LandFormat;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), area.Left);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5), area.Top);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(9), area.Width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(13), area.Height);
        int at = LandHeader;
        foreach (var chunk in _owned)
        {
            for (int i = 0; i < ChunkSize * ChunkSize; i++, at++)
            {
                int index = i * TileBytes;
                bool mountain = chunk![index] == (byte)TerrainType.Mountain;
                bytes[at] = chunk[index];
                bytes[at + tiles] = chunk[index + 1];
                bytes[at + tiles * 2] = mountain ? chunk[index + 2] : (byte)0;
                bytes[at + tiles * 3] = mountain ? chunk[index + 3] : (byte)(chunk[index + 3] & ~DerivedBits);
            }
        }
        return bytes;
    }

    /// <summary>Местность из <see cref="Save"/> (<c>loadLand</c>). Бросает <see cref="FormatException"/>, если байты не карта.</summary>
    public static Land Load(GeneratorConfig config, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < LandHeader || bytes[0] != LandFormat) throw new FormatException("Карта мира не читается");
        var area = ReadArea(bytes);
        if (area.Width <= 0 || area.Height <= 0 || (long)area.Width * area.Height * ChunkSize * ChunkSize * TileBytes != bytes.Length - LandHeader)
            throw new FormatException("Карта мира не читается");
        var land = new Land(config, area);
        int tiles = TileCount(area);
        int at = LandHeader;
        for (int n = 0; n < land._owned.Length; n++)
        {
            var chunk = new byte[ChunkBytes];
            for (int i = 0; i < ChunkSize * ChunkSize; i++, at++)
            {
                int index = i * TileBytes;
                chunk[index] = bytes[at];
                chunk[index + 1] = bytes[at + tiles];
                chunk[index + 2] = bytes[at + tiles * 2];
                chunk[index + 3] = bytes[at + tiles * 3];
            }
            land._owned[n] = chunk;
        }
        for (int n = 0; n < land._owned.Length; n++)
            land.DeriveChunk(land._owned[n]!, area.Left + n % area.Width, area.Top + n / area.Width);
        return land;
    }

    /// <summary>Подменяет тайл (x, y) в байтах <see cref="Save"/> хранимыми байтами. Тайл вне карты пропускает.</summary>
    public static void PatchSavedTile(Span<byte> bytes, int x, int y, StoredTile tile)
    {
        var area = ReadArea(bytes);
        int cx = (x >> ChunkShift) - area.Left;
        int cy = (y >> ChunkShift) - area.Top;
        if (cx < 0 || cy < 0 || cx >= area.Width || cy >= area.Height) return;
        int tiles = TileCount(area);
        int at = LandHeader + (cy * area.Width + cx) * ChunkSize * ChunkSize + (y & (ChunkSize - 1)) * ChunkSize + (x & (ChunkSize - 1));
        bytes[at] = tile.Terrain;
        bytes[at + tiles] = tile.Biome;
        bytes[at + tiles * 2] = tile.Peak;
        bytes[at + tiles * 3] = tile.Relief;
    }

    /// <summary>Карта, сжатая deflate без заголовка (как <c>CompressionStream('deflate-raw')</c>): так она идёт в кадре LAND и в файле.</summary>
    public byte[] Pack() => PackBytes(Save());

    /// <summary>Байты карты (<see cref="Save"/>, быть может, с <see cref="PatchSavedTile"/>), сжатые как <see cref="Pack"/>.</summary>
    public static byte[] PackBytes(ReadOnlySpan<byte> saved)
    {
        var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(saved);
        return output.ToArray();
    }

    /// <summary>Местность из <see cref="Pack"/>.</summary>
    public static Land Unpack(GeneratorConfig config, ReadOnlySpan<byte> packed)
    {
        using var input = new DeflateStream(new MemoryStream(packed.ToArray()), CompressionMode.Decompress);
        var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            if (output.Length + read > MaxUnpackedBytes) throw new FormatException("Карта мира слишком велика");
            output.Write(buffer, 0, read);
        }
        return Load(config, output.GetBuffer().AsSpan(0, (int)output.Length));
    }

    private static LandArea ReadArea(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadInt32LittleEndian(bytes[1..]),
        BinaryPrimitives.ReadInt32LittleEndian(bytes[5..]),
        BinaryPrimitives.ReadInt32LittleEndian(bytes[9..]),
        BinaryPrimitives.ReadInt32LittleEndian(bytes[13..]));

    private static int TileCount(LandArea area) => area.Width * area.Height * ChunkSize * ChunkSize;
}
