namespace Kharos.Engine.Layers;

/// <summary>Чей экземпляр слоя: мира (код -1) или игрока (код — его номер). Порядок — мир, затем игроки по номерам.</summary>
public readonly record struct LayerOwner(long Code) : IComparable<LayerOwner>
{
    public static LayerOwner World => new(-1);

    public static LayerOwner Player(uint player) => new(player);

    public bool IsWorld => Code < 0;

    /// <summary>Номер игрока; у мира — исключение.</summary>
    public uint PlayerId => Code >= 0 ? (uint)Code : throw new InvalidOperationException("слой мира, а не игрока");

    /// <summary>Владелец по коду формата изменений: отрицательный — мир, иначе — игрок.</summary>
    public static LayerOwner FromCode(long code) => code < 0 ? World : new LayerOwner(code);

    public int CompareTo(LayerOwner other) => Code.CompareTo(other.Code);

    public override string ToString() => IsWorld ? "мир" : $"игрок {Code}";
}

/// <summary>Изменение слоя: чанк сетки или ячейка предметов целиком. Пустые байты — чанка или ячейки больше нет.</summary>
public sealed record LayerChange(string Layer, LayerOwner Owner, int X, int Y, byte[] Bytes)
{
    public bool Equals(LayerChange? other) =>
        other is not null && Layer == other.Layer && Owner == other.Owner && X == other.X && Y == other.Y && Bytes.AsSpan().SequenceEqual(other.Bytes);

    public override int GetHashCode() => HashCode.Combine(Layer, Owner, X, Y, Bytes.Length);
}

/// <summary>Предмет слоя: место и поля по порядку объявления.</summary>
public sealed record LayerItem(double X, double Y, double[] Fields)
{
    public bool Equals(LayerItem? other) =>
        other is not null && X.Equals(other.X) && Y.Equals(other.Y) && Fields.AsSpan().SequenceEqual(other.Fields);

    public override int GetHashCode() => HashCode.Combine(X, Y, Fields.Length);
}

/// <summary>
/// Слои: всё, что лежит на карте и не является сущностью (crates/engine/src/layers.rs, docs/layers.md). Слой
/// объявляется (<see cref="LayerSpec"/>), а хранение, изменения и их формат у всех слоёв общие.
/// <para>
/// Слой мира — один на всех; слой игрока — свой у каждого и живёт вместе с игроком. Сетка — чанки 32×32 тайла с
/// каналами фиксированного типа; чанк заводится при первой записи ненулевого. Предметы — записи с местом (x, y) и
/// полями, разложенные по ячейкам. Изменение — чанк или ячейка целиком: им же слой сохраняется и уходит клиенту.
/// </para>
/// </summary>
public sealed class LayerSet
{
    /// <summary>Сторона чанка сетки в тайлах — как у земли.</summary>
    public const int Chunk = 32;
    public const int ChunkTiles = Chunk * Chunk;

    private readonly Layer[] _layers;
    private readonly Dictionary<string, int> _byName = [];

    public LayerSet(IEnumerable<LayerSpec> specs)
    {
        _layers = [.. specs.Select(spec => new Layer(spec))];
        for (var id = 0; id < _layers.Length; id++)
            if (!_byName.TryAdd(_layers[id].Spec.Name, id))
                throw new ArgumentException($"слой {_layers[id].Spec.Name} дважды");
    }

    public int Count => _layers.Length;

    /// <summary>Номер правки: растёт при любом изменении блоков (ключ кэшей, производных от слоёв).</summary>
    public ulong Revision { get; private set; }

    public int? Find(string name) => _byName.TryGetValue(name, out var id) ? id : null;

    /// <summary>Номер слоя; нет — исключение.</summary>
    public int Id(string name) => Find(name) ?? throw new KeyNotFoundException($"нет слоя {name}");

    public LayerSpec Spec(int id) => _layers[id].Spec;

    public IEnumerable<LayerSpec> Specs => _layers.Select(layer => layer.Spec);

    // Игроки.

    /// <summary>Заводит игроку его слои (пустые). Уже заведённые не трогает.</summary>
    public void AddPlayer(uint player)
    {
        foreach (var layer in _layers)
            if (layer.Spec.Scope == LayerScope.Player)
                layer.Players.TryAdd(player, []);
    }

    /// <summary>Забывает все слои игрока разом: он начинает заново. Клиенту уходит, что блоков больше нет.</summary>
    public void ForgetPlayer(uint player)
    {
        foreach (var layer in _layers)
            if (layer.Players.Remove(player, out var blocks))
            {
                foreach (var (x, y) in blocks.Keys)
                    layer.Dirty.Add((player, x, y));
                Revision++;
            }
    }

    /// <summary>Игроки, у которых заведены слои, по возрастанию.</summary>
    public SortedSet<uint> Players()
    {
        var players = new SortedSet<uint>();
        foreach (var layer in _layers)
            players.UnionWith(layer.Players.Keys);
        return players;
    }

    // Сетка.

    /// <summary>Байты чанка (cx, cy в чанках): тайлы построчно, у тайла — каналы подряд. Пусто — чанка нет (одни нули).</summary>
    public ReadOnlySpan<byte> ChunkBytes(int id, LayerOwner owner, int cx, int cy) =>
        _layers[id].Blocks(owner) is { } blocks && blocks.TryGetValue((cx, cy), out var bytes) ? bytes : [];

    /// <summary>Значение канала тайла (x, y); нет чанка — ноль.</summary>
    public double Get(int id, LayerOwner owner, int x, int y, int channel)
    {
        var layer = _layers[id];
        var (chunk, tile) = ChunkOf(x, y);
        return layer.Blocks(owner) is { } blocks && blocks.TryGetValue(chunk, out var bytes)
            ? layer.Kinds[channel].Read(bytes.AsSpan(tile * layer.Stride + layer.Offsets[channel]))
            : 0;
    }

    /// <summary>Пишет канал тайла. Чанк заводится при первой записи ненулевого; то же значение — не изменение.</summary>
    public void Set(int id, LayerOwner owner, int x, int y, int channel, double value)
    {
        var layer = _layers[id];
        var (chunk, tile) = ChunkOf(x, y);
        if (layer.Blocks(owner) is not { } blocks)
            return;
        if (!blocks.TryGetValue(chunk, out var bytes))
        {
            if (value == 0)
                return;
            blocks[chunk] = bytes = new byte[ChunkTiles * layer.Stride];
        }
        var place = bytes.AsSpan(tile * layer.Stride + layer.Offsets[channel]);
        var kind = layer.Kinds[channel];
        var before = kind.Read(place);
        kind.Write(place, value);
        if (!kind.Read(place).Equals(before))
        {
            layer.Dirty.Add((owner.Code, chunk.X, chunk.Y));
            layer.Tiles?.Add((x, y));
            Revision++;
        }
    }

    /// <summary>
    /// Пишет канал всем тайлам прямоугольника [left, right) × [top, bottom) — как <see cref="Set"/> по каждому, но
    /// чанками: блок ищется раз на чанк (открыть всю карту — /reveal — миллион тайлов).
    /// </summary>
    public void Fill(int id, LayerOwner owner, int left, int top, int right, int bottom, int channel, double value)
    {
        var layer = _layers[id];
        if (left >= right || top >= bottom || layer.Blocks(owner) is not { } blocks)
            return;
        var kind = layer.Kinds[channel];
        var offset = layer.Offsets[channel];
        var size = kind.Size();
        Span<byte> pattern = stackalloc byte[size];
        kind.Write(pattern, value);
        for (var cy = top >> 5; cy <= (bottom - 1) >> 5; cy++)
            for (var cx = left >> 5; cx <= (right - 1) >> 5; cx++)
            {
                if (!blocks.TryGetValue((cx, cy), out var bytes))
                {
                    if (value == 0)
                        continue;
                    blocks[(cx, cy)] = bytes = new byte[ChunkTiles * layer.Stride];
                }
                var changed = false;
                var (fromX, toX) = (Math.Max(left, cx * Chunk), Math.Min(right, cx * Chunk + Chunk));
                var (fromY, toY) = (Math.Max(top, cy * Chunk), Math.Min(bottom, cy * Chunk + Chunk));
                for (var y = fromY; y < toY; y++)
                    for (var x = fromX; x < toX; x++)
                    {
                        var place = bytes.AsSpan(((y & (Chunk - 1)) * Chunk + (x & (Chunk - 1))) * layer.Stride + offset, size);
                        if (place.SequenceEqual(pattern))
                            continue;
                        var before = kind.Read(place);
                        pattern.CopyTo(place);
                        if (kind.Read(place).Equals(before))
                            continue;
                        changed = true;
                        layer.Tiles?.Add((x, y));
                    }
                if (changed)
                {
                    layer.Dirty.Add((owner.Code, cx, cy));
                    Revision++;
                }
            }
    }

    /// <summary>
    /// Следить за тайлами слоя-сетки мира: каждое изменение значения записывается (<see cref="TakeTiles"/>). Нужно
    /// памяти игроков (Recall): блок целиком она бы сверяла в тысячу раз дольше. Сеть и загрузка (<see cref="Apply"/>) не пишут.
    /// </summary>
    public void WatchTiles(int id)
    {
        var layer = _layers[id];
        if (!layer.Spec.IsGrid || layer.Spec.Scope != LayerScope.World)
            throw new InvalidOperationException($"{layer.Spec.Name}: следить можно за сеткой мира");
        layer.Tiles ??= [];
    }

    /// <summary>Тайлы, изменённые с прошлого раза, по возрастанию (y, затем x); слежение — <see cref="WatchTiles"/>.</summary>
    public List<(int X, int Y)> TakeTiles(int id)
    {
        var tiles = _layers[id].Tiles;
        if (tiles is null || tiles.Count == 0)
            return [];
        var list = new List<(int X, int Y)>(tiles);
        tiles.Clear();
        list.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        return list;
    }

    /// <summary>
    /// Следить за ячейками слоя предметов мира: каждая изменённая ячейка записывается (<see cref="TakeCells"/>). Нужно
    /// памяти игроков о предметах (<see cref="ItemMemory"/>). Сеть и загрузка (<see cref="Apply"/>) не пишут.
    /// </summary>
    public void WatchCells(int id)
    {
        var layer = _layers[id];
        if (layer.Spec.IsGrid || layer.Spec.Scope != LayerScope.World)
            throw new InvalidOperationException($"{layer.Spec.Name}: следить можно за предметами мира");
        layer.Cells ??= [];
    }

    /// <summary>Ячейки, изменённые с прошлого раза, по возрастанию (x, затем y); слежение — <see cref="WatchCells"/>.</summary>
    public List<(int X, int Y)> TakeCells(int id)
    {
        var cells = _layers[id].Cells;
        if (cells is null || cells.Count == 0)
            return [];
        var list = new List<(int X, int Y)>(cells);
        cells.Clear();
        list.Sort();
        return list;
    }

    /// <summary>Чанк и место тайла в нём.</summary>
    public static ((int X, int Y) Chunk, int Tile) ChunkOf(int x, int y)
    {
        // Сдвиг вправо у знаковых — деление с округлением вниз, маска — остаток по модулю: и для отрицательных.
        return ((x >> 5, y >> 5), (y & (Chunk - 1)) * Chunk + (x & (Chunk - 1)));
    }

    // Предметы.

    /// <summary>Ячейка места (x, y) слоя предметов.</summary>
    public (int X, int Y) CellOf(int id, double x, double y)
    {
        var spec = _layers[id].Spec;
        if (spec.IsGrid)
            throw new InvalidOperationException($"{spec.Name}: не предметы");
        return ((int)Math.Floor(x / spec.Cell), (int)Math.Floor(y / spec.Cell));
    }

    /// <summary>
    /// Правило слоя предметов (<see cref="Intercept"/>): видит каждый кладущийся предмет (кроме перекладываемых
    /// <see cref="Retain"/>) и возвращает его, быть может изменённым, или null — не класть.
    /// </summary>
    public delegate LayerItem? ItemRule(LayerOwner owner, LayerItem item);

    /// <summary>Ставит правило слою предметов: предел густоты следов, номера и время, которые назначает движок.</summary>
    public void Intercept(int id, ItemRule? rule)
    {
        if (_layers[id].Spec.IsGrid)
            throw new InvalidOperationException($"{_layers[id].Spec.Name}: правило ставят слою предметов");
        _layers[id].Rule = rule;
    }

    /// <summary>Кладёт предмет в его ячейку (правило слоя может его изменить или не пустить); false — не положен.</summary>
    public bool Push(int id, LayerOwner owner, LayerItem item)
    {
        var layer = _layers[id];
        if (layer.Blocks(owner) is null)
            return false;
        if (layer.Rule is { } rule)
        {
            if (rule(owner, item) is not { } accepted)
                return false;
            item = accepted;
        }
        return Put(id, owner, item);
    }

    private bool Put(int id, LayerOwner owner, LayerItem item)
    {
        var cell = CellOf(id, item.X, item.Y);
        var layer = _layers[id];
        if (layer.Blocks(owner) is not { } blocks)
            return false;
        var old = blocks.TryGetValue(cell, out var bytes) ? bytes : [];
        var grown = new byte[old.Length + layer.Stride];
        old.CopyTo(grown, 0);
        var record = grown.AsSpan(old.Length);
        Channel.F64.Write(record, item.X);
        Channel.F64.Write(record[8..], item.Y);
        for (var at = 0; at < layer.Kinds.Length; at++)
            layer.Kinds[at].Write(record[layer.Offsets[at]..], at < item.Fields.Length ? item.Fields[at] : 0);
        blocks[cell] = grown;
        layer.Dirty.Add((owner.Code, cell.X, cell.Y));
        if (owner.IsWorld)
            layer.Cells?.Add(cell);
        Revision++;
        return true;
    }

    /// <summary>Предметы ячейки по порядку.</summary>
    public List<LayerItem> Items(int id, LayerOwner owner, (int X, int Y) cell)
    {
        var layer = _layers[id];
        var items = new List<LayerItem>();
        if (layer.Blocks(owner) is not { } blocks || !blocks.TryGetValue(cell, out var bytes))
            return items;
        for (var start = 0; start + layer.Stride <= bytes.Length; start += layer.Stride)
        {
            var record = bytes.AsSpan(start, layer.Stride);
            var fields = new double[layer.Kinds.Length];
            for (var at = 0; at < fields.Length; at++)
                fields[at] = layer.Kinds[at].Read(record[layer.Offsets[at]..]);
            items.Add(new LayerItem(Channel.F64.Read(record), Channel.F64.Read(record[8..]), fields));
        }
        return items;
    }

    /// <summary>Оставляет в ячейке предметы, для которых keep — true, в прежнем порядке.</summary>
    public void Retain(int id, LayerOwner owner, (int X, int Y) cell, Func<LayerItem, bool> keep)
    {
        var items = Items(id, owner, cell);
        var kept = items.Where(keep).ToList();
        if (kept.Count == items.Count)
            return;
        var layer = _layers[id];
        layer.Blocks(owner)?.Remove(cell);
        layer.Dirty.Add((owner.Code, cell.X, cell.Y));
        if (owner.IsWorld)
            layer.Cells?.Add(cell);
        Revision++;
        foreach (var item in kept)
            Put(id, owner, item);
    }

    // Изменения.

    /// <summary>Блоки, изменённые с прошлого раза, — по слоям в порядке объявления, внутри — по владельцу и координатам.</summary>
    public List<LayerChange> TakeChanges()
    {
        var changes = new List<LayerChange>();
        foreach (var layer in _layers)
        {
            foreach (var (code, x, y) in layer.Dirty)
            {
                var owner = LayerOwner.FromCode(code);
                var bytes = layer.Blocks(owner) is { } blocks && blocks.TryGetValue((x, y), out var block) ? (byte[])block.Clone() : [];
                changes.Add(new LayerChange(layer.Spec.Name, owner, x, y, bytes));
            }
            layer.Dirty.Clear();
        }
        return changes;
    }

    /// <summary>Все блоки сохраняемых слоёв владельца: мира или одного игрока. Тот же формат, что у изменений.</summary>
    public List<LayerChange> Save(LayerOwner owner)
    {
        var changes = new List<LayerChange>();
        for (var id = 0; id < _layers.Length; id++)
        {
            var spec = _layers[id].Spec;
            if (spec.Save && owner.IsWorld == (spec.Scope == LayerScope.World))
                changes.AddRange(Blocks(id, owner));
        }
        return changes;
    }

    /// <summary>Все блоки экземпляра слоя по порядку координат — изменениями, как их применил бы <see cref="Apply"/>.</summary>
    public List<LayerChange> Blocks(int id, LayerOwner owner)
    {
        var layer = _layers[id];
        return [.. Cells(id, owner).Select(cell => new LayerChange(layer.Spec.Name, owner, cell.X, cell.Y, (byte[])layer.Blocks(owner)![cell].Clone()))];
    }

    /// <summary>Непустые блоки экземпляра слоя (чанки сетки или ячейки предметов) по порядку координат.</summary>
    public List<(int X, int Y)> Cells(int id, LayerOwner owner)
    {
        if (_layers[id].Blocks(owner) is not { } blocks)
            return [];
        var keys = blocks.Keys.ToList();
        keys.Sort();
        return keys;
    }

    /// <summary>
    /// Применяет чужое изменение (загрузка, сеть). В свои изменения оно не идёт: та сторона о нём уже знает. Игроку
    /// слой заводится сам. Нет слоя, блок не того размера, владелец не той стороны — <see cref="FormatException"/>.
    /// </summary>
    public void Apply(LayerChange change)
    {
        var id = Find(change.Layer) ?? throw new FormatException($"нет слоя {change.Layer}");
        var layer = _layers[id];
        var fits = layer.Spec.IsGrid ? change.Bytes.Length == ChunkTiles * layer.Stride : change.Bytes.Length % layer.Stride == 0;
        if (change.Bytes.Length > 0 && !fits)
            throw new FormatException($"{change.Layer}: блок {change.Bytes.Length} байт");
        if (change.Owner.IsWorld != (layer.Spec.Scope == LayerScope.World))
            throw new FormatException($"{change.Layer}: {change.Owner} — не владелец этого слоя");
        if (!change.Owner.IsWorld)
            layer.Players.TryAdd(change.Owner.PlayerId, []);
        var blocks = layer.Blocks(change.Owner)!;
        if (change.Bytes.Length == 0)
            blocks.Remove((change.X, change.Y));
        else
            blocks[(change.X, change.Y)] = (byte[])change.Bytes.Clone();
        Revision++;
    }

    /// <summary>Один слой: объявление, смещения каналов, блоки мира или игроков и изменённое.</summary>
    private sealed class Layer
    {
        public Layer(LayerSpec spec)
        {
            Spec = spec;
            Stride = spec.Stride;
            Kinds = spec.IsGrid ? [.. spec.Channels] : [.. spec.Fields.Select(field => field.Channel)];
            Offsets = new int[Kinds.Length];
            // У предмета первые 16 байт — место.
            var at = spec.IsGrid ? 0 : 16;
            for (var index = 0; index < Kinds.Length; index++)
            {
                Offsets[index] = at;
                at += Kinds[index].Size();
            }
        }

        public LayerSpec Spec { get; }
        public int Stride { get; }
        public Channel[] Kinds { get; }
        public int[] Offsets { get; }
        public Dictionary<(int X, int Y), byte[]> World { get; } = [];

        /// <summary>Изменённые тайлы сетки мира, если за ними следят (<see cref="WatchTiles"/>).</summary>
        public HashSet<(int X, int Y)>? Tiles { get; set; }

        /// <summary>Изменённые ячейки предметов мира, если за ними следят (<see cref="WatchCells"/>).</summary>
        public HashSet<(int X, int Y)>? Cells { get; set; }

        /// <summary>Правило слоя предметов (<see cref="Intercept"/>).</summary>
        public ItemRule? Rule { get; set; }
        public SortedDictionary<uint, Dictionary<(int X, int Y), byte[]>> Players { get; } = [];

        /// <summary>Изменённые блоки с прошлого раза: (код владельца, x, y) по порядку — так изменения детерминированы.</summary>
        public SortedSet<(long Owner, int X, int Y)> Dirty { get; } = [];

        /// <summary>Блоки владельца; владелец не той стороны или незаведённый игрок — null.</summary>
        public Dictionary<(int X, int Y), byte[]>? Blocks(LayerOwner owner)
        {
            if (owner.IsWorld)
                return Spec.Scope == LayerScope.World ? World : null;
            return Spec.Scope == LayerScope.Player && Players.TryGetValue(owner.PlayerId, out var blocks) ? blocks : null;
        }
    }
}
