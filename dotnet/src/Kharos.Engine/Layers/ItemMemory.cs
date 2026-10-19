using System.Buffers.Binary;

namespace Kharos.Engine.Layers;

/// <summary>Обзор для памяти предметов: кто из игроков есть, видит ли он место и какие ячейки слоя у него в обзоре.</summary>
public interface IItemSight
{
    /// <summary>Игроки с обзором, по возрастанию.</summary>
    IEnumerable<uint> Players { get; }

    /// <summary>Видит ли игрок точку сейчас.</summary>
    bool Sees(uint player, double x, double y);

    /// <summary>Ячейки стороной cell тайлов, где игрок видит сейчас хоть один тайл, — в into.</summary>
    void SeenCells(uint player, int cell, HashSet<(int X, int Y)> into);
}

/// <summary>
/// Версии предметных слоёв мира у игроков (docs/layers.md, «запоминаемость»; depositLayer.ts, renewal.ts, host.ts —
/// traces): слой у всех один, а игроку уходит его версия.
/// <list type="bullet">
/// <item>Запоминаемый (<see cref="LayerSpec.Remember"/>, месторождения): предмет, место которого игрок видит, — какой он
/// есть; остальное — каким игрок его видел последним. Ушедшее он помнит, пока не увидит его место; новое, которого не
/// видел, не знает. Версия идёт в сохранение (<see cref="Encode"/>).</item>
/// <item>Только в обзоре (<see cref="LayerSpec.Sight"/>, следы): только предметы, место которых игрок видит сейчас.
/// Ушёл — забыл; вернулся — получит снова. Не сохраняется: обзор пересчитают.</item>
/// </list>
/// Место предмета — его точка (x, y). Сверка: изменённые ячейки слоя — каждый тик, по текущему обзору; ячейки обзора
/// игрока (и бывшие в нём при прошлой сверке) — раз в <see cref="LookTicks"/> тиков, когда обзор пересчитан. Версия
/// хранится блоками в формате слоя: записи ячейки по порядку, как у <see cref="LayerSet"/>.
/// </summary>
public sealed class ItemMemory
{
    /// <summary>Раз во сколько тиков сверяются ячейки обзора: обзор и пересчитывается раз в столько.</summary>
    public const int LookTicks = 4;

    private const byte Version = 1;

    private readonly LayerSet _layers;
    private readonly Fogged[] _fogged;
    private readonly HashSet<(int X, int Y)> _seen = [];
    private readonly HashSet<(int X, int Y)> _check = [];
    private readonly List<(int X, int Y)> _order = [];

    private ItemMemory(LayerSet layers, Fogged[] fogged)
    {
        _layers = layers;
        _fogged = fogged;
    }

    /// <summary>
    /// Память партии: слои предметов мира с remember или sight (<see cref="LayerSpec.Fogged"/>); за их ячейками следят.
    /// Таких слоёв нет — null.
    /// </summary>
    public static ItemMemory? Create(LayerSet layers)
    {
        var fogged = new List<Fogged>();
        for (var id = 0; id < layers.Count; id++)
        {
            var spec = layers.Spec(id);
            if (!spec.Fogged)
                continue;
            layers.WatchCells(id);
            fogged.Add(new Fogged(id, spec));
        }
        return fogged.Count == 0 ? null : new ItemMemory(layers, [.. fogged]);
    }

    /// <summary>Идёт ли слой игрокам версиями этой памяти, а не слоем мира.</summary>
    public bool Covers(string layer)
    {
        foreach (var fogged in _fogged)
            if (fogged.Spec.Name == layer)
                return true;
        return false;
    }

    /// <summary>
    /// Сверка после обзора (каждый тик): изменённые ячейки — у всех игроков; на тиках пересчёта обзора — и ячейки, где
    /// игрок видит что-то сейчас или видел при прошлой сверке.
    /// </summary>
    public void Update(IItemSight sight, ulong tick)
    {
        var look = tick % LookTicks == 0;
        foreach (var fogged in _fogged)
        {
            var changed = _layers.TakeCells(fogged.Id);
            if (changed.Count == 0 && !look)
                continue;
            foreach (var player in sight.Players)
            {
                _check.Clear();
                _check.UnionWith(changed);
                if (look)
                {
                    _seen.Clear();
                    sight.SeenCells(player, fogged.Spec.Cell, _seen);
                    _check.UnionWith(_seen);
                    if (fogged.Swept.TryGetValue(player, out var swept))
                    {
                        _check.UnionWith(swept);
                        swept.Clear();
                        swept.UnionWith(_seen);
                    }
                    else if (_seen.Count > 0)
                    {
                        fogged.Swept[player] = [.. _seen];
                    }
                }
                if (_check.Count == 0)
                    continue;
                _order.Clear();
                _order.AddRange(_check);
                _order.Sort();
                foreach (var cell in _order)
                    Learn(sight, fogged, player, cell);
            }
        }
    }

    /// <summary>Версия ячейки у игрока — заново: что он видит, — каким оно есть; остальное у запоминаемого — каким было.</summary>
    private void Learn(IItemSight sight, Fogged fogged, uint player, (int X, int Y) cell)
    {
        var stride = fogged.Spec.Stride;
        var actual = _layers.ChunkBytes(fogged.Id, LayerOwner.World, cell.X, cell.Y);
        fogged.Known.TryGetValue(player, out var known);
        var was = known is not null && known.TryGetValue(cell, out var bytes) ? bytes : [];
        if (actual.Length == 0 && was.Length == 0)
            return;
        var next = new byte[was.Length + actual.Length];
        var length = 0;
        if (fogged.Spec.Remember)
            for (var at = 0; at + stride <= was.Length; at += stride)
                if (!Sees(sight, player, was.AsSpan(at)))
                {
                    was.AsSpan(at, stride).CopyTo(next.AsSpan(length));
                    length += stride;
                }
        for (var at = 0; at + stride <= actual.Length; at += stride)
            if (Sees(sight, player, actual[at..]))
            {
                actual.Slice(at, stride).CopyTo(next.AsSpan(length));
                length += stride;
            }
        if (next.AsSpan(0, length).SequenceEqual(was))
            return;
        if (known is null)
            fogged.Known[player] = known = [];
        if (length == 0)
            known.Remove(cell);
        else
            known[cell] = next[..length];
        if (!fogged.Learned.TryGetValue(player, out var learned))
            fogged.Learned[player] = learned = [];
        learned.Add(cell);
    }

    private static bool Sees(IItemSight sight, uint player, ReadOnlySpan<byte> record) =>
        sight.Sees(player, BinaryPrimitives.ReadDoubleLittleEndian(record), BinaryPrimitives.ReadDoubleLittleEndian(record[8..]));

    /// <summary>Ячейки, где версия игрока изменилась с прошлого раза, — изменениями слоя мира (пусто — ячейки нет); по игрокам.</summary>
    public SortedDictionary<uint, List<LayerChange>> TakeLearned()
    {
        var result = new SortedDictionary<uint, List<LayerChange>>();
        foreach (var fogged in _fogged)
        {
            foreach (var (player, cells) in fogged.Learned)
            {
                if (!result.TryGetValue(player, out var list))
                    result[player] = list = [];
                foreach (var cell in cells)
                    list.Add(Change(fogged, player, cell));
            }
            fogged.Learned.Clear();
        }
        return result;
    }

    /// <summary>Вся версия игрока — при подключении: непустые ячейки по слоям и по порядку.</summary>
    public List<LayerChange> Blocks(uint player)
    {
        var changes = new List<LayerChange>();
        foreach (var fogged in _fogged)
        {
            if (!fogged.Known.TryGetValue(player, out var known))
                continue;
            var cells = known.Keys.ToList();
            cells.Sort();
            foreach (var cell in cells)
                changes.Add(Change(fogged, player, cell));
        }
        return changes;
    }

    /// <summary>Предметы слоя, какими их знает игрок (тесты, запросы): ячейки по порядку, в ячейке — по записи.</summary>
    public List<LayerItem> Items(string layer, uint player)
    {
        var items = new List<LayerItem>();
        var fogged = _fogged.FirstOrDefault(item => item.Spec.Name == layer) ?? throw new KeyNotFoundException($"{layer}: слой не идёт по обзору");
        if (!fogged.Known.TryGetValue(player, out var known))
            return items;
        var stride = fogged.Spec.Stride;
        var kinds = fogged.Spec.Fields.Select(field => field.Channel).ToArray();
        var cells = known.Keys.ToList();
        cells.Sort();
        foreach (var cell in cells)
        {
            var bytes = known[cell];
            for (var at = 0; at + stride <= bytes.Length; at += stride)
            {
                var record = bytes.AsSpan(at, stride);
                var fields = new double[kinds.Length];
                var offset = 16;
                for (var index = 0; index < kinds.Length; index++)
                {
                    fields[index] = kinds[index].Read(record[offset..]);
                    offset += kinds[index].Size();
                }
                items.Add(new LayerItem(Channel.F64.Read(record), Channel.F64.Read(record[8..]), fields));
            }
        }
        return items;
    }

    private static LayerChange Change(Fogged fogged, uint player, (int X, int Y) cell) =>
        new(fogged.Spec.Name, LayerOwner.World, cell.X, cell.Y,
            fogged.Known.TryGetValue(player, out var known) && known.TryGetValue(cell, out var bytes) ? (byte[])bytes.Clone() : []);

    /// <summary>Игрок начинает заново: он не знает ничего; клиенту уходит, что ячеек больше нет.</summary>
    public void ForgetPlayer(uint player)
    {
        foreach (var fogged in _fogged)
        {
            fogged.Swept.Remove(player);
            if (!fogged.Known.Remove(player, out var known) || known.Count == 0)
                continue;
            if (!fogged.Learned.TryGetValue(player, out var learned))
                fogged.Learned[player] = learned = [];
            learned.UnionWith(known.Keys);
        }
    }

    /// <summary>
    /// Сохранение без памяти предметов (прежние версии): запоминаемые предметы, место которых игрок разведал, он знает —
    /// как knowsDeposit в TS (разведано — значит, видел).
    /// </summary>
    public void KnowExplored(IEnumerable<uint> players, Func<uint, double, double, bool> explored)
    {
        foreach (var fogged in _fogged)
        {
            if (!fogged.Spec.Remember)
                continue;
            var stride = fogged.Spec.Stride;
            var cells = _layers.Cells(fogged.Id, LayerOwner.World);
            foreach (var player in players)
            {
                var known = new SortedDictionary<(int X, int Y), byte[]>();
                foreach (var cell in cells)
                {
                    var actual = _layers.ChunkBytes(fogged.Id, LayerOwner.World, cell.X, cell.Y);
                    var next = new List<byte>(actual.Length);
                    for (var at = 0; at + stride <= actual.Length; at += stride)
                        if (explored(player, BinaryPrimitives.ReadDoubleLittleEndian(actual[at..]), BinaryPrimitives.ReadDoubleLittleEndian(actual[(at + 8)..])))
                            next.AddRange(actual.Slice(at, stride));
                    if (next.Count > 0)
                        known[cell] = [.. next];
                }
                if (known.Count > 0)
                    fogged.Known[player] = new Dictionary<(int X, int Y), byte[]>(known);
            }
        }
    }

    // Сохранение: только запоминаемые слои; игроки и ячейки по возрастанию — файл одинаков у одинаковых партий.

    /// <summary>Есть ли что сохранять: кто-то что-то знает о запоминаемом слое.</summary>
    public bool Any => _fogged.Any(fogged => fogged.Spec.Remember && fogged.Known.Values.Any(known => known.Count > 0));

    /// <summary>
    /// Версии запоминаемых слоёв байтами для раздела сохранения:
    /// <c>version:u8 layers:varint (name:str players:varint (player:varint cells:varint (x:i32 y:i32 bytes:blob)×)×)×</c>.
    /// </summary>
    public byte[] Encode()
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Version);
            var remembered = _fogged.Where(fogged => fogged.Spec.Remember).ToList();
            w.Write7BitEncodedInt(remembered.Count);
            foreach (var fogged in remembered)
            {
                w.Write(fogged.Spec.Name);
                var players = fogged.Known.Where(pair => pair.Value.Count > 0).ToList();
                w.Write7BitEncodedInt(players.Count);
                foreach (var (player, known) in players)
                {
                    w.Write7BitEncodedInt64(player);
                    w.Write7BitEncodedInt(known.Count);
                    var cells = known.Keys.ToList();
                    cells.Sort();
                    foreach (var cell in cells)
                    {
                        w.Write(cell.X);
                        w.Write(cell.Y);
                        w.Write7BitEncodedInt(known[cell].Length);
                        w.Write(known[cell]);
                    }
                }
            }
        }
        return stream.ToArray();
    }

    /// <summary>Версии из <see cref="Encode"/>. Слоя больше нет или он не запоминаемый — пропускается; неверное — <see cref="FormatException"/>.</summary>
    public void Decode(ReadOnlySpan<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray());
        using var r = new BinaryReader(stream);
        try
        {
            if (r.ReadByte() != Version)
                throw new FormatException("память предметов: версия");
            for (var layers = Count(r); layers > 0; layers--)
            {
                var name = r.ReadString();
                var fogged = _fogged.FirstOrDefault(item => item.Spec.Name == name && item.Spec.Remember);
                for (var players = Count(r); players > 0; players--)
                {
                    var player = (uint)r.Read7BitEncodedInt64();
                    if (player == 0)
                        throw new FormatException("память предметов: игрок 0");
                    Dictionary<(int X, int Y), byte[]>? known = null;
                    if (fogged is not null && !fogged.Known.TryGetValue(player, out known))
                        fogged.Known[player] = known = [];
                    for (var cells = Count(r); cells > 0; cells--)
                    {
                        var cell = (r.ReadInt32(), r.ReadInt32());
                        var block = r.ReadBytes(Count(r));
                        if (known is null)
                            continue;
                        if (block.Length == 0 || block.Length % fogged!.Spec.Stride != 0)
                            throw new FormatException($"память предметов: {name}: блок {block.Length} байт");
                        known[cell] = block;
                    }
                }
            }
            if (stream.Position != stream.Length)
                throw new FormatException("память предметов: лишние байты");
        }
        catch (EndOfStreamException)
        {
            throw new FormatException("память предметов обрывается");
        }
    }

    private static int Count(BinaryReader r)
    {
        var count = r.Read7BitEncodedInt();
        return count >= 0 && count <= r.BaseStream.Length - r.BaseStream.Position ? count : throw new FormatException("память предметов: счёт больше данных");
    }

    /// <summary>Слой по обзору: версии игроков, ячейки прошлой сверки и изменённое с прошлого раза.</summary>
    private sealed class Fogged(int id, LayerSpec spec)
    {
        public int Id { get; } = id;
        public LayerSpec Spec { get; } = spec;
        public SortedDictionary<uint, Dictionary<(int X, int Y), byte[]>> Known { get; } = [];
        public Dictionary<uint, HashSet<(int X, int Y)>> Swept { get; } = [];
        public SortedDictionary<uint, SortedSet<(int X, int Y)>> Learned { get; } = [];
    }
}
