using Kharos.Engine.Numerics;
using static Kharos.Engine.Terrain.TileFormat;

namespace Kharos.Engine.Terrain;

/// <summary>Хранимые байты тайла: тип, биом, байт вершины и рельеф без выводимого (у не-гор третий байт — 0).</summary>
public readonly record struct StoredTile(byte Terrain, byte Biome, byte Peak, byte Relief);

/// <summary>
/// Местность (<c>Land</c> из TS). Не путать с миром сущностей.
/// <para>
/// Внутри области карты (<see cref="Area"/>) чанки — данные мира: собранные однажды, больше не берутся из генератора,
/// правятся и целиком уходят в сохранение (<see cref="Save"/>). Собираются лениво: генератор детерминирован, так что это
/// то же самое, что собрать все сразу. Вне области — декоративный край мира, всегда из генератора, только кэшируется.
/// </para>
/// Глубина в песках и биты <see cref="TileFormat.StepBit"/>, <see cref="TileFormat.FootBit"/> не хранятся: они выводятся
/// из типов и ярусов соседей. Не потокобезопасна.
/// </summary>
public sealed partial class Land
{
    private const int MaxCachedChunks = 4096;
    /// <summary>На сколько тайлов от края песков барханы сходят на нет: дальше глубина в песках 1.</summary>
    private const double SandReach = 8;
    /// <summary>Окно соседей для StepBit: сверху больше — туда смотрит стенка обрыва.</summary>
    private const int StepUp = 2;
    private const int StepDown = 1;
    private const int StepSide = 1;
    /// <summary>Поле вокруг чанка, которое смотрит вывод: хватает и глубине песков, и StepBit.</summary>
    private const int DeriveMargin = 8;
    private const int DeriveSpan = ChunkSize + DeriveMargin * 2;
    private const double Sqrt2 = 1.4142135623730951;

    /// <summary>Чанки карты по номеру в области; null — ещё не собран.</summary>
    private readonly byte[]?[] _owned;
    /// <summary>Кэш чанков края мира; выбрасывается старейший.</summary>
    private readonly Dictionary<long, byte[]> _outside = [];
    private readonly Queue<long> _outsideOrder = new();
    /// <summary>Чанк, к которому обращались последним: соседние тайлы спрашивают подряд.</summary>
    private byte[]? _last;
    private long _lastKey;
    private readonly Dictionary<long, int> _revisions = [];
    /// <summary>Чанки, которые пересчитать в конце пакетной правки; null — пакета нет.</summary>
    private Dictionary<long, (int X, int Y)>? _pending;
    /// <summary>Вершины клеток за один пересчёт выводимого.</summary>
    private readonly Dictionary<long, Peak[]> _peaks = [];

    /// <summary>Местность по генератору; area — область карты мира, null — карты нет (предпросмотр, отладка).</summary>
    public Land(GeneratorConfig config, LandArea? area = null)
    {
        if (area is { Width: <= 0 } or { Height: <= 0 }) throw new ArgumentOutOfRangeException(nameof(area), area, "пустая область карты");
        Config = config;
        Area = area;
        _owned = new byte[]?[area?.Count ?? 0];
    }

    public GeneratorConfig Config { get; }
    public LandArea? Area { get; }
    /// <summary>Растёт с каждой правкой карты.</summary>
    public int Revision { get; private set; }

    /// <summary>Номер правки чанка: окно на видеокарте перечитывает только изменённые. 0 — чанк не правили.</summary>
    public int ChunkRevision(int chunkX, int chunkY) => _revisions.GetValueOrDefault(Key(chunkX, chunkY));

    /// <summary>Чанк по координатам (в чанках), собранный при первом обращении: <see cref="TileFormat.ChunkBytes"/> байт.</summary>
    public ReadOnlySpan<byte> GetChunk(int chunkX, int chunkY) => Chunk(chunkX, chunkY);

    public TerrainType TerrainAt(int x, int y) => (TerrainType)Chunk(x >> ChunkShift, y >> ChunkShift)[TileIndex(x, y)];

    public TerrainType TerrainAt(double x, double y) => TerrainAt(Tile(x), Tile(y));

    /// <summary>Основной биом в точке.</summary>
    public Biome BiomeAt(double x, double y)
    {
        int tileX = Tile(x);
        int tileY = Tile(y);
        return (Biome)(Chunk(tileX >> ChunkShift, tileY >> ChunkShift)[TileIndex(tileX, tileY) + 1] >> 6);
    }

    /// <summary>Тайл у подножия обрыва, см. <see cref="TileFormat.FootBit"/>.</summary>
    public bool IsCliffFoot(double x, double y)
    {
        int tileX = Tile(x);
        int tileY = Tile(y);
        var chunk = Chunk(tileX >> ChunkShift, tileY >> ChunkShift);
        int index = TileIndex(tileX, tileY);
        return chunk[index] != (byte)TerrainType.Mountain && (chunk[index + 3] & FootBit) != 0;
    }

    /// <summary>Хранимые байты тайла (<c>tileBytes</c>): так тайл лежит в сохранении и ходит в правках.</summary>
    public StoredTile Stored(int x, int y)
    {
        var chunk = Chunk(x >> ChunkShift, y >> ChunkShift);
        int index = TileIndex(x, y);
        bool mountain = chunk[index] == (byte)TerrainType.Mountain;
        return new(chunk[index], chunk[index + 1], mountain ? chunk[index + 2] : (byte)0,
            mountain ? chunk[index + 3] : (byte)(chunk[index + 3] & ~DerivedBits));
    }

    /// <summary>
    /// Меняет тайл карты. Вне области карты ничего не делает: край мира — из генератора. Горы так не ставятся, для них
    /// <see cref="SetPeakTile"/>. Пересчитывает выводимое у соседей.
    /// </summary>
    public void SetTile(int x, int y, TileEdit edit)
    {
        if (edit.Terrain == TerrainType.Mountain) return;
        int dunes = edit.Terrain == TerrainType.Sand ? (int)edit.Dunes << DuneShift : 0;
        WriteTile(x, y, new((byte)edit.Terrain, edit.Biome, 0, (byte)((edit.Tier & 3) | (edit.Cliff ? CliffBit : 0) | dunes)));
    }

    /// <summary>
    /// Ставит на тайл часть горы с вершиной в (peakX, peakY) радиуса radius. Вершина — не дальше 4 тайлов по каждой оси,
    /// радиус — до <see cref="TileFormat.PeakRadiusScale"/>: так они укладываются в байты тайла.
    /// </summary>
    public void SetPeakTile(int x, int y, byte biome, double peakX, double peakY, double radius)
    {
        double dx = JsMath.Round((peakX - (x + 0.5)) * 2 + 8);
        double dy = JsMath.Round((peakY - (y + 0.5)) * 2 + 8);
        if (dx < 0 || dx > 15 || dy < 0 || dy > 15) return;
        byte size = JsMath.ToUint8(JsMath.Round(Math.Min(radius, PeakRadiusScale) / PeakRadiusScale * 255));
        WriteTile(x, y, new((byte)TerrainType.Mountain, biome, size, (byte)((int)dx << 4 | (int)dy)));
    }

    /// <summary>Вершина горы, частью которой стоит тайл; null — тайл не гора.</summary>
    public Peak? PeakOf(int x, int y)
    {
        var tile = Stored(x, y);
        if (tile.Terrain != (byte)TerrainType.Mountain) return null;
        return new(x + 0.5 + ((tile.Relief >> 4) - 8) / 2.0, y + 0.5 + ((tile.Relief & 15) - 8) / 2.0, tile.Peak / 255.0 * PeakRadiusScale);
    }

    /// <summary>Накладывает правки: x, y и четыре хранимых байта тайла подряд (<c>applyEdits</c>).</summary>
    public void ApplyEdits(ReadOnlySpan<int> edits)
    {
        for (int i = 0; i + 5 < edits.Length; i += 6)
            WriteTile(edits[i], edits[i + 1], new((byte)edits[i + 2], (byte)edits[i + 3], (byte)edits[i + 4], (byte)edits[i + 5]));
    }

    /// <summary>
    /// Правит карту пакетом: правки пишутся сразу, а выводимое соседние чанки пересчитывают один раз в конце. Внутри
    /// пакета выводимое у правленых тайлов ещё прежнее.
    /// </summary>
    public void Batch(Action edit)
    {
        if (_pending is not null)
        {
            edit();
            return;
        }
        _pending = [];
        try
        {
            edit();
        }
        finally
        {
            var chunks = _pending.Values.ToArray();
            _pending = null;
            Derive(chunks);
        }
    }

    private static int Tile(double coordinate) => JsMath.ToInt32(Math.Floor(coordinate));

    private static int TileIndex(int x, int y) => ((y & (ChunkSize - 1)) * ChunkSize + (x & (ChunkSize - 1))) * TileBytes;

    private static long Key(int x, int y) => (long)y << 32 | (uint)x;

    private int OwnedIndex(int chunkX, int chunkY) => Area?.IndexOf(chunkX, chunkY) ?? -1;

    internal byte[] Chunk(int chunkX, int chunkY)
    {
        long key = Key(chunkX, chunkY);
        if (_last is not null && _lastKey == key) return _last;
        int owned = OwnedIndex(chunkX, chunkY);
        var chunk = owned >= 0 ? _owned[owned] : _outside.GetValueOrDefault(key);
        if (chunk is null)
        {
            chunk = TerrainGenerator.GenerateChunk(Config, chunkX, chunkY);
            if (owned >= 0) _owned[owned] = chunk;
            DeriveChunk(chunk, chunkX, chunkY);
            if (owned < 0)
            {
                _outside.Add(key, chunk);
                _outsideOrder.Enqueue(key);
                // Старейший чанк края можно выбросить: он детерминированно соберётся заново.
                if (_outside.Count > MaxCachedChunks) _outside.Remove(_outsideOrder.Dequeue());
            }
        }
        _last = chunk;
        _lastKey = key;
        return chunk;
    }

    /// <summary>Записывает хранимые байты тайла и пересчитывает выводимое соседей. Вне карты ничего не делает.</summary>
    private void WriteTile(int x, int y, StoredTile tile)
    {
        int chunkX = x >> ChunkShift;
        int chunkY = y >> ChunkShift;
        if (OwnedIndex(chunkX, chunkY) < 0) return;
        // Соседи, которые выводят что-то из этого тайла, собираются до правки: несобранный посчитал бы его по генератору.
        const int reach = (DeriveMargin + ChunkSize - 1) / ChunkSize;
        var touched = new List<(int X, int Y)>(9);
        for (int cy = (y - DeriveMargin) >> ChunkShift; cy <= (y + DeriveMargin) >> ChunkShift; cy++)
        {
            for (int cx = (x - DeriveMargin) >> ChunkShift; cx <= (x + DeriveMargin) >> ChunkShift; cx++)
            {
                if (Math.Abs(cx - chunkX) > reach || Math.Abs(cy - chunkY) > reach || OwnedIndex(cx, cy) < 0) continue;
                Chunk(cx, cy);
                touched.Add((cx, cy));
            }
        }
        var chunk = Chunk(chunkX, chunkY);
        int index = TileIndex(x, y);
        chunk[index] = tile.Terrain;
        chunk[index + 1] = tile.Biome;
        chunk[index + 2] = tile.Peak;
        chunk[index + 3] = tile.Relief;
        Revision++;
        if (_pending is not null)
            foreach (var (cx, cy) in touched) _pending[Key(cx, cy)] = (cx, cy);
        else
            Derive(touched);
    }

    /// <summary>Пересчитывает выводимое у чанков карты и отмечает их правку.</summary>
    private void Derive(IEnumerable<(int X, int Y)> chunks)
    {
        foreach (var (cx, cy) in chunks)
        {
            DeriveChunk(_owned[OwnedIndex(cx, cy)]!, cx, cy);
            _revisions[Key(cx, cy)] = Revision;
        }
    }

    /// <summary>
    /// Тип, ярус и обрывистость соседа (у гор ярус −1). Из карты, если его чанк собран; из chunk, если сосед в самом
    /// досчитываемом чанке; иначе из генератора вместе с горами. За краем карты для её чанков — ближайший тайл карты:
    /// так из сохранения выходит то же при любом генераторе.
    /// </summary>
    private (TerrainType Terrain, int Tier, bool Cliff) Neighbor(int x, int y, byte[] chunk, int chunkX, int chunkY)
    {
        if (Area is { } area && area.IndexOf(chunkX, chunkY) >= 0)
        {
            x = Math.Max(area.Left * ChunkSize, Math.Min((area.Left + area.Width) * ChunkSize - 1, x));
            y = Math.Max(area.Top * ChunkSize, Math.Min((area.Top + area.Height) * ChunkSize - 1, y));
        }
        int cx = x >> ChunkShift;
        int cy = y >> ChunkShift;
        byte[]? tiles = cx == chunkX && cy == chunkY ? chunk : OwnedIndex(cx, cy) is var at and >= 0 ? _owned[at] : null;
        if (tiles is null) return TerrainGenerator.GeneratedTile(Config, x, y, _peaks);
        int index = TileIndex(x, y);
        var terrain = (TerrainType)tiles[index];
        if (terrain == TerrainType.Mountain) return (terrain, -1, false);
        return (terrain, tiles[index + 3] & 3, (tiles[index + 3] & CliffBit) != 0);
    }

    /// <summary>
    /// Досчитывает выводимое из соседей: глубину в песках (расстояние до скал и болот), перепад ярусов и подножие обрыва.
    /// Горы не трогает: их байты заняты вершиной.
    /// </summary>
    private void DeriveChunk(byte[] chunk, int chunkX, int chunkY)
    {
        const int span = DeriveSpan;
        Span<byte> types = stackalloc byte[span * span];
        Span<sbyte> tiers = stackalloc sbyte[span * span];
        Span<bool> cliffs = stackalloc bool[span * span];
        _peaks.Clear();
        for (int y = 0; y < span; y++)
        {
            for (int x = 0; x < span; x++)
            {
                var (terrain, tier, cliff) = Neighbor(chunkX * ChunkSize + x - DeriveMargin, chunkY * ChunkSize + y - DeriveMargin, chunk, chunkX, chunkY);
                types[y * span + x] = (byte)terrain;
                tiers[y * span + x] = (sbyte)tier;
                cliffs[y * span + x] = cliff;
            }
        }

        // Расстояние до ближайшего не-песка: два прохода с шагами 1 и √2. В float, как Float32Array в TS: сумма в double,
        // округление при записи.
        Span<float> distance = stackalloc float[span * span];
        for (int i = 0; i < distance.Length; i++) distance[i] = types[i] == (byte)TerrainType.Sand ? float.PositiveInfinity : 0;
        for (int y = 0; y < span; y++)
        {
            for (int x = 0; x < span; x++)
            {
                int i = y * span + x;
                if (x > 0) Relax(distance, i, i - 1, 1);
                if (y > 0)
                {
                    Relax(distance, i, i - span, 1);
                    if (x > 0) Relax(distance, i, i - span - 1, Sqrt2);
                    if (x < span - 1) Relax(distance, i, i - span + 1, Sqrt2);
                }
            }
        }
        for (int y = span - 1; y >= 0; y--)
        {
            for (int x = span - 1; x >= 0; x--)
            {
                int i = y * span + x;
                if (x < span - 1) Relax(distance, i, i + 1, 1);
                if (y < span - 1)
                {
                    Relax(distance, i, i + span, 1);
                    if (x < span - 1) Relax(distance, i, i + span + 1, Sqrt2);
                    if (x > 0) Relax(distance, i, i + span - 1, Sqrt2);
                }
            }
        }

        for (int y = 0; y < ChunkSize; y++)
        {
            for (int x = 0; x < ChunkSize; x++)
            {
                int index = (y * ChunkSize + x) * TileBytes;
                if (chunk[index] == (byte)TerrainType.Mountain) continue;
                int at = (y + DeriveMargin) * span + x + DeriveMargin;
                // У самой кромки — 0, на SandReach тайлов вглубь — 1.
                chunk[index + 2] = JsMath.ToUint8(JsMath.Round(Math.Max(0, Math.Min(1, (distance[at] - 0.5) / SandReach)) * 255));
                int tier = tiers[at];
                bool step = false;
                for (int dy = -StepUp; dy <= StepDown && !step; dy++)
                {
                    for (int dx = -StepSide; dx <= StepSide; dx++)
                    {
                        int other = tiers[at + dy * span + dx];
                        if (other >= 0 && other != tier)
                        {
                            step = true;
                            break;
                        }
                    }
                }
                bool foot = false;
                for (int dy = -1; dy <= 1 && !foot; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int near = at + dy * span + dx;
                        if (tiers[near] > tier && cliffs[near])
                        {
                            foot = true;
                            break;
                        }
                    }
                }
                chunk[index + 3] = (byte)((chunk[index + 3] & ~DerivedBits) | (step ? StepBit : 0) | (foot ? FootBit : 0));
            }
        }
    }

    private static void Relax(Span<float> distance, int i, int j, double cost)
    {
        double value = distance[j] + cost;
        if (value < distance[i]) distance[i] = (float)value;
    }
}
