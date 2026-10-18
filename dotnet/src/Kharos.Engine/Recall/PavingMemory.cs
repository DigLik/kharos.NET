using Kharos.Engine.Layers;

namespace Kharos.Engine.Recall;

/// <summary>
/// Что игроки знают о готовом покрытии (paveMemory.ts; слой «paving»: код вида и хозяина, 0 — нет). Покрытие у всех
/// одно, но чужое игрок узнаёт, только увидев: в тумане войны дорога остаётся для него такой, какой он её видел, — как
/// здание-призрак или правка карты. Своё и союзное покрытие он знает всегда. Общее — покрытия нет.
/// </summary>
public sealed class PavingMemory(LayerSet layers, int layer) : LayerMemory
{
    public const string LayerName = "paving";

    /// <summary>Номер слоя.</summary>
    public int Layer => layer;

    /// <summary>
    /// Память покрытия партии: слой «paving» — сетка мира из одного канала u16, за его тайлами следят. Нет слоя или он
    /// не тот — null.
    /// </summary>
    public static PavingMemory? Create(LayerSet layers)
    {
        if (layers.Find(LayerName) is not { } id)
            return null;
        var spec = layers.Spec(id);
        if (spec.Scope != LayerScope.World || !spec.IsGrid || spec.Channels.Count != 1 || spec.Channels[0] != Channel.U16)
            return null;
        layers.WatchTiles(id);
        return new PavingMemory(layers, id);
    }

    /// <summary>Хозяин покрытия по коду: код = хозяин·2 + вид + 2 (<c>paveOwnerOf</c>); 0 — покрытия нет.</summary>
    public static uint? OwnerOf(uint code) => code >= 2 ? (code - 2) / 2 : null;

    protected override bool HasOwners => true;

    protected override uint Actual(Tile tile) => (uint)layers.Get(layer, LayerOwner.World, tile.X, tile.Y, 0);

    public override uint Common(Tile tile, uint actual) => 0;

    protected override uint? Owner(uint value) => OwnerOf(value);

    protected override IEnumerable<Tile> Tiles()
    {
        foreach (var (cx, cy) in layers.Cells(layer, LayerOwner.World))
        {
            var bytes = layers.ChunkBytes(layer, LayerOwner.World, cx, cy).ToArray();
            for (var i = 0; i < LayerSet.ChunkTiles; i++)
                if (bytes[i * 2] != 0 || bytes[i * 2 + 1] != 0)
                    yield return new Tile(cx * LayerSet.Chunk + i % LayerSet.Chunk, cy * LayerSet.Chunk + i / LayerSet.Chunk);
        }
    }

    /// <summary>Изменённые слоем тайлы — к сверке (звать перед <see cref="LayerMemory.Update"/>).</summary>
    public void Intake()
    {
        foreach (var (x, y) in layers.TakeTiles(layer))
            Change(new Tile(x, y));
    }

    /// <summary>
    /// Чанки слоя, какими их знает игрок: покрытие, которое он знает, — остальное пусто. Блок целиком (чанк 32×32), как
    /// у любого изменения слоя; нет знания в чанке — пустые байты (чанка больше нет).
    /// </summary>
    public List<LayerChange> Chunks(uint player, IEnumerable<Tile> tiles)
    {
        var known = Known.GetValueOrDefault(player);
        var changes = new List<LayerChange>();
        var chunks = new SortedSet<(int Y, int X)>();
        foreach (var tile in tiles)
            chunks.Add((tile.Y >> 5, tile.X >> 5));
        foreach (var (cy, cx) in chunks)
        {
            var bytes = new byte[LayerSet.ChunkTiles * 2];
            var any = false;
            if (known is not null)
                for (var i = 0; i < LayerSet.ChunkTiles; i++)
                    if (known.TryGetValue(new Tile(cx * LayerSet.Chunk + i % LayerSet.Chunk, cy * LayerSet.Chunk + i / LayerSet.Chunk), out var code))
                    {
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)code);
                        any = true;
                    }
            changes.Add(new LayerChange(LayerName, LayerOwner.World, cx, cy, any ? bytes : []));
        }
        return changes;
    }
}
