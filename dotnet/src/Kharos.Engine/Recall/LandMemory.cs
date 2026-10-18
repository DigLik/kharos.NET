using Kharos.Engine.Terrain;

namespace Kharos.Engine.Recall;

/// <summary>
/// Что игроки знают о карте (landMemory.ts). Карта у всех одна, но её правки (снесли гору, проложили пандус) игрок
/// узнаёт, только увидев: в тумане войны тайл остаётся для него таким, каким он его знал. Помнится, каким был каждый
/// изменённый тайл до первой правки, — это знают все, — и что из правок знает каждый игрок. Неизменённые тайлы у всех
/// одинаковы и не помнятся.
/// </summary>
public sealed class LandMemory(Land land) : LayerMemory
{
    private readonly Dictionary<Tile, uint> _original = [];

    /// <summary>Каким был изменённый тайл до первой правки (хранимые байты одним числом).</summary>
    public IReadOnlyDictionary<Tile, uint> Original => _original;

    /// <summary>Хранимые байты тайла одним числом (<c>packTile</c>).</summary>
    public static uint Pack(StoredTile tile) => tile.Terrain | (uint)tile.Biome << 8 | (uint)tile.Peak << 16 | (uint)tile.Relief << 24;

    public static StoredTile Unpack(uint value) => new((byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24));

    protected override bool Idle => _original.Count == 0;

    protected override uint Actual(Tile tile) => Pack(land.Stored(tile.X, tile.Y));

    public override uint Common(Tile tile, uint actual) => _original.TryGetValue(tile, out var original) ? original : actual;

    protected override IEnumerable<Tile> Tiles() => _original.Keys;

    /// <summary>Правка из журнала земли: первая запоминает исходный тайл; тайл ждёт сверки.</summary>
    public void Note(TileChange edit)
    {
        var tile = new Tile(edit.X, edit.Y);
        _original.TryAdd(tile, Pack(edit.Before));
        Change(tile);
    }

    /// <summary>Исходный тайл из сохранения.</summary>
    public void SetOriginal(Tile tile, uint value) => _original[tile] = value;

    public override void Forget(Tile tile)
    {
        _original.Remove(tile);
        base.Forget(tile);
    }

    /// <summary>Исходные тайлы по возрастанию (y, x).</summary>
    public List<Tile> OriginalTiles()
    {
        var tiles = _original.Keys.ToList();
        tiles.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        return tiles;
    }

    /// <summary>Что игрок знает о тайле: хранимые байты.</summary>
    public StoredTile KnownTile(uint player, Tile tile) => Unpack(KnownValue(player, tile));

    internal void WriteOriginal(BinaryWriter w)
    {
        w.Write7BitEncodedInt(_original.Count);
        foreach (var tile in OriginalTiles())
        {
            w.Write(tile.X);
            w.Write(tile.Y);
            w.Write(_original[tile]);
        }
    }

    internal void ReadOriginal(BinaryReader r)
    {
        for (var count = Count(r); count > 0; count--)
            _original[new Tile(r.ReadInt32(), r.ReadInt32())] = r.ReadUInt32();
    }
}
