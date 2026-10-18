using Kharos.Engine.Layers;
using Kharos.Engine.World;

namespace Kharos.Protocol;

/// <summary>
/// Изменения мира с прошлой рассылки — итогом, а не по событиям: что появилось (целиком), что пропало, у оставшихся —
/// добавленные заново целиком, снятые компоненты и изменённые поля. Сущность, появившаяся и пропавшая между рассылками,
/// не попадает никуда. Значения берутся из мира при кодировании (<see cref="DeltaCodec.Encode"/>): кодировать — до
/// следующего тика. Одна на всех зрителей, фильтр — при кодировании.
/// </summary>
public sealed class WorldDelta
{
    public ulong Tick { get; private set; }
    public uint NextEntity { get; private set; }

    /// <summary>Номер первой новой строки мира и сами строки (строки только добавляются).</summary>
    public int StringsFrom { get; private set; }
    public List<string> Strings { get; } = [];

    /// <summary>Пропавшие (жившие до прошлой рассылки) — по возрастанию.</summary>
    public List<uint> Destroyed { get; } = [];

    /// <summary>Появившиеся и живые — по возрастанию: уходят со всеми компонентами.</summary>
    public List<uint> Created { get; } = [];

    /// <summary>Компоненты прежних сущностей, которые уходят целиком (добавлены, возможно, после снятия).</summary>
    public List<ComponentChange> Full { get; } = [];

    /// <summary>Снятые компоненты прежних сущностей.</summary>
    public List<ComponentChange> Removed { get; } = [];

    /// <summary>Изменённые поля прежних компонентов — по компоненту, внутри — по маске (в порядке правок).</summary>
    public List<FieldChange> Fields { get; } = [];

    public List<LayerChange> Layers { get; } = [];
    public List<Signal> Signals { get; } = [];

    public bool IsEmpty => Strings.Count == 0 && Destroyed.Count == 0 && Created.Count == 0 && Full.Count == 0 && Removed.Count == 0 && Fields.Count == 0 && Layers.Count == 0 && Signals.Count == 0;

    /// <summary>Итог изменений: changes — из <see cref="GameWorld.TakeChanges"/>, строки — с номера stringsFrom.</summary>
    public static WorldDelta Build(GameWorld world, WorldChanges changes, int stringsFrom, IEnumerable<LayerChange> layers, IEnumerable<Signal> signals, ulong tick)
    {
        var delta = new WorldDelta { Tick = tick, NextEntity = world.NextEntity, StringsFrom = stringsFrom };
        for (var id = stringsFrom; id < world.Strings.Count; id++)
            delta.Strings.Add(world.Strings.Text((uint)id));
        var born = new HashSet<uint>(changes.Spawned);
        foreach (var entity in born)
            if (world.Alive(entity))
                delta.Created.Add(entity);
        delta.Created.Sort();
        var created = new HashSet<uint>(delta.Created);
        foreach (var entity in new HashSet<uint>(changes.Destroyed))
            if (!born.Contains(entity))
                delta.Destroyed.Add(entity);
        delta.Destroyed.Sort();
        var touched = new HashSet<ComponentChange>();
        foreach (var change in changes.Added.Concat(changes.Removed))
        {
            if (created.Contains(change.Entity) || !world.Alive(change.Entity) || !touched.Add(change))
                continue;
            (world.Has(change.Entity, change.Component) ? delta.Full : delta.Removed).Add(change);
        }
        delta.Full.Sort(static (a, b) => a.Entity != b.Entity ? a.Entity.CompareTo(b.Entity) : a.Component.CompareTo(b.Component));
        delta.Removed.Sort(static (a, b) => a.Entity != b.Entity ? a.Entity.CompareTo(b.Entity) : a.Component.CompareTo(b.Component));
        var full = delta.Full.Count > 0 ? new HashSet<ComponentChange>(delta.Full) : null;
        var kept = new List<FieldChange>(changes.Fields.Count);
        foreach (var field in changes.Fields)
            if ((created.Count == 0 || !created.Contains(field.Entity)) && world.Has(field.Entity, field.Component) && (full is null || !full.Contains(new ComponentChange(field.Entity, field.Component))))
                kept.Add(field);
        // Поля уже идут по компонентам; внутри компонента — по маске, порядок правок сохраняется (без сортировки).
        var masks = new List<ulong>();
        for (var start = 0; start < kept.Count;)
        {
            var end = start;
            masks.Clear();
            for (; end < kept.Count && kept[end].Component == kept[start].Component; end++)
                if (!masks.Contains(kept[end].Mask))
                    masks.Add(kept[end].Mask);
            if (masks.Count == 1)
                delta.Fields.AddRange(kept.GetRange(start, end - start));
            else
                foreach (var mask in masks)
                    for (var at = start; at < end; at++)
                        if (kept[at].Mask == mask)
                            delta.Fields.Add(kept[at]);
            start = end;
        }
        delta.Layers.AddRange(layers);
        delta.Signals.AddRange(signals);
        return delta;
    }
}

/// <summary>
/// Двоичный кодек снимка и дельты по схеме (docs/dotnet.md §4.4). Значения — как лежат: число — f64, флаг — u8,
/// строка — номер varint, байты — blob; квантование (позиции 1/256 тайла, углы) — позже, формат версионирован.
/// <code>
/// 0x02 Snapshot  tick:varint world:blob (WorldSave, "KHWD") layers:blob (LayerCodec)
/// 0x03 Delta     tick:varint next:varint
///   strings  from:varint count:varint str×count
///   destroyed count:varint (entity − предыдущий):varint×count
///   created  count:varint, на сущность: (entity − предыдущий):varint components:varint (component:varint values)×
///   full     count:varint (entity:varint component:varint values)×
///   removed  count:varint (entity:varint component:varint)×
///   fields   groups:varint, на группу: component:varint mask:varint count:varint ((entity − предыдущий):svarint values по маске)×
///   layers   blob (LayerCodec)
///   signals  count:varint (name:str payload:blob)×
///   values — поля компонента по порядку (или по маске): число f64, флаг u8, строка id:varint, байты blob
/// </code>
/// </summary>
public static class DeltaCodec
{
    /// <summary>Снимок для зрителя: мир целиком (фильтр сущностей — Н4) и слои, которые ему уходят.</summary>
    public static byte[] Snapshot(ulong tick, GameWorld world, LayerSet layers, IViewFilter filter)
    {
        var w = new WireWriter(64 * 1024);
        w.Byte((byte)MessageType.Snapshot).Varint(tick).Blob(WorldSave.Save(world));
        var blocks = new List<LayerChange>();
        for (var id = 0; id < layers.Count; id++)
        {
            var spec = layers.Spec(id);
            if (spec.Scope == LayerScope.World)
            {
                if (filter.Layer(spec, LayerOwner.World))
                    blocks.AddRange(layers.Blocks(id, LayerOwner.World));
            }
            else
            {
                foreach (var player in layers.Players())
                    if (filter.Layer(spec, LayerOwner.Player(player)))
                        blocks.AddRange(layers.Blocks(id, LayerOwner.Player(player)));
            }
        }
        w.Blob(LayerCodec.Encode(blocks));
        return w.ToArray();
    }

    /// <summary>Дельта для зрителя.</summary>
    public static byte[] Encode(WorldDelta delta, GameWorld world, LayerSet layers, IViewFilter filter, WireWriter? scratch = null)
    {
        var w = scratch ?? new WireWriter(4096);
        w.Clear();
        w.Byte((byte)MessageType.Delta).Varint(delta.Tick).Varint(delta.NextEntity);
        w.Varint((ulong)delta.StringsFrom).Varint((ulong)delta.Strings.Count);
        foreach (var text in delta.Strings)
            w.Text(text);

        var destroyed = delta.Destroyed.Where(filter.Entity).ToList();
        w.Varint((ulong)destroyed.Count);
        uint previous = 0;
        foreach (var entity in destroyed)
        {
            w.Varint(entity - previous);
            previous = entity;
        }

        var created = delta.Created.Where(filter.Entity).ToList();
        w.Varint((ulong)created.Count);
        previous = 0;
        var schema = world.Schema;
        foreach (var entity in created)
        {
            w.Varint(entity - previous);
            previous = entity;
            var count = 0;
            for (var c = 0; c < schema.Count; c++)
                if (world.Has(entity, c))
                    count++;
            w.Varint((ulong)count);
            for (var c = 0; c < schema.Count; c++)
                if (world.Has(entity, c))
                {
                    w.Varint((ulong)c);
                    Values(w, world, entity, c, ulong.MaxValue);
                }
        }

        var full = delta.Full.Where(change => filter.Entity(change.Entity)).ToList();
        w.Varint((ulong)full.Count);
        foreach (var change in full)
        {
            w.Varint(change.Entity).Varint((ulong)change.Component);
            Values(w, world, change.Entity, change.Component, ulong.MaxValue);
        }

        var removed = delta.Removed.Where(change => filter.Entity(change.Entity)).ToList();
        w.Varint((ulong)removed.Count);
        foreach (var change in removed)
            w.Varint(change.Entity).Varint((ulong)change.Component);

        // Поля — группами (компонент, маска): у тысяч юнитов, сдвинувшихся за тик, заголовок один.
        var fields = delta.Fields;
        var groups = new List<(int Start, int End, int Count)>();
        for (var start = 0; start < fields.Count;)
        {
            var end = start;
            var count = 0;
            for (; end < fields.Count && SameGroup(fields[start], fields[end]); end++)
                if (filter.Entity(fields[end].Entity))
                    count++;
            if (count > 0)
                groups.Add((start, end, count));
            start = end;
        }
        w.Varint((ulong)groups.Count);
        foreach (var (start, end, count) in groups)
        {
            w.Varint((ulong)fields[start].Component).Varint(fields[start].Mask).Varint((ulong)count);
            previous = 0;
            for (var at = start; at < end; at++)
            {
                var field = fields[at];
                if (!filter.Entity(field.Entity))
                    continue;
                w.SVarint((long)field.Entity - previous);
                previous = field.Entity;
                Values(w, world, field.Entity, field.Component, field.Mask);
            }
        }

        var blocks = new List<LayerChange>(delta.Layers.Count);
        foreach (var change in delta.Layers)
            if (layers.Find(change.Layer) is { } id && filter.Layer(layers.Spec(id), change.Owner))
                blocks.Add(change);
        w.Blob(blocks.Count > 0 ? LayerCodec.Encode(blocks) : []);

        w.Varint((ulong)delta.Signals.Count);
        foreach (var signal in delta.Signals)
            w.Text(signal.Name).Blob(signal.Payload);
        return w.ToArray();
    }

    /// <summary>Приказ клиента: 0x10 name:str payload:blob.</summary>
    public static byte[] Order(string name, ReadOnlySpan<byte> payload) =>
        new WireWriter(16 + name.Length + payload.Length).Byte((byte)MessageType.Order).Text(name).Blob(payload).ToArray();

    /// <summary>Разбор приказа; не приказ — <see cref="FormatException"/>.</summary>
    public static (string Name, byte[] Payload) ReadOrder(ReadOnlySpan<byte> message)
    {
        var r = new WireReader(message);
        if (r.Byte() != (byte)MessageType.Order)
            throw new FormatException("не приказ");
        var name = r.Text();
        var payload = r.Blob().ToArray();
        return r.End ? (name, payload) : throw new FormatException("лишние байты в приказе");
    }

    private static bool SameGroup(FieldChange a, FieldChange b) => a.Component == b.Component && a.Mask == b.Mask;

    /// <summary>Поля компонента сущности по маске.</summary>
    private static void Values(WireWriter w, GameWorld world, uint entity, int component, ulong mask)
    {
        var store = world.Store(component);
        var row = store.RowOf(entity);
        var fields = store.Def.Fields;
        for (var index = 0; index < fields.Count; index++)
        {
            if ((mask & (1UL << index)) == 0)
                continue;
            var field = fields[index];
            switch (field.Kind)
            {
                case FieldKind.Number:
                    w.F64(store.Column(field.Slot)[row]);
                    break;
                case FieldKind.Flag:
                    w.Bool(store.Column(field.Slot)[row] != 0);
                    break;
                case FieldKind.String:
                    w.Varint((ulong)store.Column(field.Slot)[row]);
                    break;
                default:
                    w.Blob(store.BytesAt(field.Slot, row));
                    break;
            }
        }
    }
}
