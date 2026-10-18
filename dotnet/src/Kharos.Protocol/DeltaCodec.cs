using Kharos.Engine.Layers;
using Kharos.Engine.World;

namespace Kharos.Protocol;

/// <summary>
/// Сборщик снимка или дельты одного зрителя (docs/dotnet.md §4.4). Порядок вызовов свободный: каждый раздел копится
/// отдельно, <see cref="Finish"/> склеивает их. Значения берутся из мира в момент вызова, числа — с точностью
/// <see cref="NetFormat"/>. Поля сущностей — группами (компонент, маска): у тысяч юнитов, сдвинувшихся за тик,
/// заголовок один. Зритель (<see cref="Viewer"/>) решает, что уходит: компонент целиком или нет (@net), скрытые поля
/// (@private) — значениями по умолчанию из схемы (в группах полей их нет), сигналы — только адресатам. Формат тот же.
/// <code>
/// 0x02 Snapshot | 0x03 Delta   tick:varint
///   strings  from:varint count:varint str×                 — новые строки мира с номера from
///   removed  count:varint (entity − предыдущий):svarint×     — пропали или ушли из виду
///   created  count:varint ((entity − предыдущий):svarint components:varint (component:varint values)×)×
///   added    count:varint (entity:varint component:varint values)×   — компонент знакомой сущности целиком
///   dropped  count:varint (entity:varint component:varint)×
///   fields   groups:varint (component:varint mask:varint count:varint ((entity − предыдущий):svarint values по маске)×)×
///   spans    count:varint ((entity − предыдущий):svarint span:varint)×  — за сколько тиков сущность пришла к месту
///   layers   packed:u8 blob (LayerCodec, при packed = 1 — сжатый deflate-raw; пусто — изменений нет)
///   signals  count:varint (name:str payload:blob)×
///   values — поля по порядку (или по маске): число — NetFormat, флаг u8, строка — номер varint, байты blob
/// </code>
/// Снимок — то же тело, но реплика начинает с пустого мира.
/// </summary>
public sealed class DeltaWriter
{
    private readonly WireWriter _removed = new(256), _created = new(4096), _added = new(256), _dropped = new(64), _spans = new(256), _signals = new(64), _out = new(4096);
    private readonly Dictionary<(int Component, ulong Mask), Group> _groups = [];
    private readonly List<Group> _order = [];
    private readonly List<Group> _spare = [];
    private readonly List<string> _strings = [];
    private int _removedCount, _createdCount, _addedCount, _droppedCount, _spansCount, _signalsCount, _stringsFrom;
    private long _removedPrev, _createdPrev, _spansPrev;
    private byte[] _layers = [];
    private bool _layersPacked;

    /// <summary>С какого размера блоки слоёв сжимаются.</summary>
    public const int SqueezeFrom = 512;
    private MessageType _type = MessageType.Delta;
    private ulong _tick;
    private NetFormat _format = null!;

    /// <summary>Сколько сущностей в разделах (для замеров).</summary>
    public int Entities => _createdCount + _order.Sum(group => group.Count);

    /// <summary>Примерный размер сообщения, байт.</summary>
    public int Size
    {
        get
        {
            var size = 16 + _removed.Length + _created.Length + _added.Length + _dropped.Length + _spans.Length + _signals.Length + _layers.Length;
            foreach (var group in _order)
                size += group.W.Length + 6;
            foreach (var text in _strings)
                size += text.Length + 1;
            return size;
        }
    }

    /// <summary>Начинает сообщение заново.</summary>
    public void Begin(MessageType type, ulong tick, NetFormat format)
    {
        if (type is not (MessageType.Snapshot or MessageType.Delta))
            throw new ArgumentException($"{type}: не снимок и не дельта", nameof(type));
        _type = type;
        _tick = tick;
        _format = format;
        _removed.Clear();
        _created.Clear();
        _added.Clear();
        _dropped.Clear();
        _spans.Clear();
        _signals.Clear();
        _strings.Clear();
        _stringsFrom = 0;
        _removedCount = _createdCount = _addedCount = _droppedCount = _spansCount = _signalsCount = 0;
        _removedPrev = _createdPrev = _spansPrev = 0;
        _layers = [];
        _layersPacked = false;
        foreach (var group in _order)
        {
            group.W.Clear();
            _spare.Add(group);
        }
        _order.Clear();
        _groups.Clear();
    }

    /// <summary>Строки мира с номера from (до конца).</summary>
    public void Strings(Strings strings, int from)
    {
        from = Math.Max(from, 1);
        _stringsFrom = from;
        _strings.Clear();
        for (var id = from; id < strings.Count; id++)
            _strings.Add(strings.Text((uint)id));
    }

    public void Remove(uint entity)
    {
        _removed.SVarint(entity - _removedPrev);
        _removedPrev = entity;
        _removedCount++;
    }

    /// <summary>
    /// Зритель: что из сущностей и сигналов ему уходит (<see cref="IViewFilter.Shows"/>, <see cref="IViewFilter.Hidden"/>,
    /// <see cref="IViewFilter.Hears"/>). null — всё как есть. <see cref="Begin"/> его не сбрасывает.
    /// </summary>
    public IViewFilter? Viewer { get; set; }

    /// <summary>Сущность целиком, со всеми компонентами, которые уходят зрителю.</summary>
    public void Create(GameWorld world, uint entity)
    {
        var w = _created;
        w.SVarint(entity - _createdPrev);
        _createdPrev = entity;
        _createdCount++;
        var schema = world.Schema;
        var count = 0;
        for (var c = 0; c < schema.Count; c++)
            if (world.Has(entity, c) && Shows(world, entity, c))
                count++;
        w.Varint((ulong)count);
        for (var c = 0; c < schema.Count; c++)
            if (world.Has(entity, c) && Shows(world, entity, c))
            {
                w.Varint((ulong)c);
                Values(w, world, entity, c, ulong.MaxValue, Hidden(world, entity, c));
            }
    }

    /// <summary>
    /// Сущность целиком из записи, а не из мира (призрак — здание, каким его запомнил зритель): что зрителю не положено,
    /// уже снято тем, кто запись дал.
    /// </summary>
    public void Create(uint entity, IReadOnlyList<RecordPart> parts)
    {
        var w = _created;
        w.SVarint(entity - _createdPrev);
        _createdPrev = entity;
        _createdCount++;
        w.Varint((ulong)parts.Count);
        foreach (var part in parts)
        {
            var id = part.Def.Id;
            w.Varint((ulong)id);
            var fields = part.Def.Fields;
            for (var index = 0; index < fields.Count; index++)
            {
                var field = fields[index];
                switch (field.Kind)
                {
                    case FieldKind.Number:
                        _format.Write(w, id, index, part.Columns[field.Slot]);
                        break;
                    case FieldKind.Flag:
                        w.Bool(part.Columns[field.Slot] != 0);
                        break;
                    case FieldKind.String:
                        w.Varint((ulong)part.Columns[field.Slot]);
                        break;
                    default:
                        w.Blob(part.Bytes[field.Slot]);
                        break;
                }
            }
        }
    }

    /// <summary>Компонент знакомой сущности целиком (добавлен заново); зрителю не уходит — ничего.</summary>
    public void Add(GameWorld world, uint entity, int component)
    {
        if (!Shows(world, entity, component))
            return;
        _added.Varint(entity).Varint((ulong)component);
        Values(_added, world, entity, component, ulong.MaxValue, Hidden(world, entity, component));
        _addedCount++;
    }

    /// <summary>Компонент снят.</summary>
    public void Drop(uint entity, int component)
    {
        _dropped.Varint(entity).Varint((ulong)component);
        _droppedCount++;
    }

    /// <summary>Компонент снят — если он вообще уходит зрителю.</summary>
    public void Drop(GameWorld world, uint entity, int component)
    {
        if (Shows(world, entity, component))
            Drop(entity, component);
    }

    /// <summary>Поля компонента по маске (без скрытых от зрителя).</summary>
    public void Fields(GameWorld world, uint entity, int component, ulong mask)
    {
        if (Viewer is not null)
        {
            if (!Shows(world, entity, component))
                return;
            mask &= ~Hidden(world, entity, component);
            if (mask == 0)
                return;
        }
        if (!_groups.TryGetValue((component, mask), out var group))
        {
            if (_spare.Count > 0)
            {
                group = _spare[^1];
                _spare.RemoveAt(_spare.Count - 1);
            }
            else
            {
                group = new Group();
            }
            (group.Component, group.Mask, group.Count, group.Prev) = (component, mask, 0, 0);
            _groups[(component, mask)] = group;
            _order.Add(group);
        }
        group.W.SVarint(entity - group.Prev);
        group.Prev = entity;
        group.Count++;
        Values(group.W, world, entity, component, mask);
    }

    /// <summary>Сущность догоняет мир за span тиков: реплика ведёт её к новому месту столько же.</summary>
    public void Span(uint entity, int span)
    {
        _spans.SVarint(entity - _spansPrev).Varint((ulong)span);
        _spansPrev = entity;
        _spansCount++;
    }

    /// <summary>Блоки слоёв; больше <see cref="SqueezeFrom"/> байт — сжатыми (чанки тумана — нули и единицы).</summary>
    public void Layers(IReadOnlyList<LayerChange> blocks)
    {
        _layers = blocks.Count > 0 ? LayerCodec.Encode(blocks) : [];
        _layersPacked = _layers.Length > SqueezeFrom;
        if (_layersPacked)
            _layers = ContentPack.Deflate(_layers, System.IO.Compression.CompressionLevel.Fastest);
    }

    /// <summary>Сигналы, которые слышит зритель (адресные — только адресатам, см. <see cref="IViewFilter.Hears"/>).</summary>
    public void Signals(IReadOnlyList<Signal> signals)
    {
        foreach (var signal in signals)
        {
            if (Viewer is { } viewer && !viewer.Hears(signal))
                continue;
            _signals.Text(signal.Name).Blob(signal.Payload);
            _signalsCount++;
        }
    }

    /// <summary>Сообщение целиком.</summary>
    public byte[] Finish()
    {
        var w = _out;
        w.Clear();
        w.Byte((byte)_type).Varint(_tick);
        w.Varint((ulong)_stringsFrom).Varint((ulong)_strings.Count);
        foreach (var text in _strings)
            w.Text(text);
        w.Varint((ulong)_removedCount).Raw(_removed.Written);
        w.Varint((ulong)_createdCount).Raw(_created.Written);
        w.Varint((ulong)_addedCount).Raw(_added.Written);
        w.Varint((ulong)_droppedCount).Raw(_dropped.Written);
        w.Varint((ulong)_order.Count);
        foreach (var group in _order)
            w.Varint((ulong)group.Component).Varint(group.Mask).Varint((ulong)group.Count).Raw(group.W.Written);
        w.Varint((ulong)_spansCount).Raw(_spans.Written);
        w.Bool(_layersPacked).Blob(_layers);
        w.Varint((ulong)_signalsCount).Raw(_signals.Written);
        return w.ToArray();
    }

    private bool Shows(GameWorld world, uint entity, int component) => Viewer?.Shows(world, entity, component) ?? true;

    private ulong Hidden(GameWorld world, uint entity, int component) => Viewer?.Hidden(world, entity, component) ?? 0;

    /// <summary>Значение поля по умолчанию (из схемы): так уходит скрытое поле. Строки, которой нет в мире, — пустая.</summary>
    private void Default(WireWriter w, GameWorld world, int component, FieldDef field)
    {
        switch (field.Kind)
        {
            case FieldKind.Number:
                _format.Write(w, component, field.Index, field.Default.Number);
                break;
            case FieldKind.Flag:
                w.Bool(field.Default.Number != 0);
                break;
            case FieldKind.String:
                w.Varint(world.Strings.Find(field.Default.Text ?? "") ?? 0);
                break;
            default:
                w.Blob([]);
                break;
        }
    }

    /// <summary>Поля компонента сущности по маске; скрытые (hidden) — значениями по умолчанию.</summary>
    private void Values(WireWriter w, GameWorld world, uint entity, int component, ulong mask, ulong hidden = 0)
    {
        var store = world.Store(component);
        var row = store.RowOf(entity);
        var fields = store.Def.Fields;
        for (var index = 0; index < fields.Count; index++)
        {
            if ((mask & (1UL << index)) == 0)
                continue;
            var field = fields[index];
            if ((hidden & (1UL << index)) != 0)
            {
                Default(w, world, component, field);
                continue;
            }
            switch (field.Kind)
            {
                case FieldKind.Number:
                    _format.Write(w, component, index, store.Column(field.Slot)[row]);
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

    private sealed class Group
    {
        public WireWriter W { get; } = new(1024);
        public int Component;
        public ulong Mask;
        public int Count;
        public long Prev;
    }
}

/// <summary>
/// Снимок и дельта без памяти о зрителе (у хоста она — в рассылке с бюджетом): что видно фильтру, уходит целиком и
/// сразу. Плюс приказы клиента.
/// </summary>
public static class DeltaCodec
{
    /// <summary>Снимок для зрителя: видимые сущности целиком, все строки и слои, которые ему уходят.</summary>
    public static byte[] Snapshot(ulong tick, GameWorld world, LayerSet layers, IViewFilter filter, NetFormat format, DeltaWriter? writer = null)
    {
        var w = writer ?? new DeltaWriter();
        w.Begin(MessageType.Snapshot, tick, format);
        w.Viewer = filter;
        w.Strings(world.Strings, 1);
        var alive = new List<uint>(world.Count);
        world.Entities(alive);
        foreach (var entity in alive)
            if (filter.Entity(entity))
                w.Create(world, entity);
        w.Layers(LayerBlocks(layers, filter));
        return w.Finish();
    }

    /// <summary>Все блоки слоёв, которые уходят зрителю.</summary>
    public static List<LayerChange> LayerBlocks(LayerSet layers, IViewFilter filter)
    {
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
        return blocks;
    }

    /// <summary>Изменения слоёв, которые уходят зрителю.</summary>
    public static List<LayerChange> LayerChanges(IReadOnlyList<LayerChange> changes, LayerSet layers, IViewFilter filter)
    {
        var blocks = new List<LayerChange>(changes.Count);
        foreach (var change in changes)
            if (layers.Find(change.Layer) is { } id && filter.Layer(layers.Spec(id), change.Owner))
                blocks.Add(change);
        return blocks;
    }

    /// <summary>Дельта для зрителя: изменения видимых ему сущностей — все и сразу.</summary>
    public static byte[] Encode(WorldDelta delta, GameWorld world, LayerSet layers, IViewFilter filter, NetFormat format, DeltaWriter? writer = null)
    {
        var w = writer ?? new DeltaWriter();
        w.Begin(MessageType.Delta, delta.Tick, format);
        w.Viewer = filter;
        w.Strings(world.Strings, delta.StringsFrom);
        foreach (var entity in delta.Destroyed)
            if (filter.Entity(entity))
                w.Remove(entity);
        foreach (var entity in delta.Created)
            if (filter.Entity(entity))
                w.Create(world, entity);
        foreach (var change in delta.Full)
            if (filter.Entity(change.Entity))
                w.Add(world, change.Entity, change.Component);
        foreach (var change in delta.Removed)
            if (filter.Entity(change.Entity))
                w.Drop(world, change.Entity, change.Component);
        foreach (var field in delta.Fields)
            if (filter.Entity(field.Entity))
                w.Fields(world, field.Entity, field.Component, field.Mask);
        w.Layers(LayerChanges(delta.Layers, layers, filter));
        w.Signals(delta.Signals);
        return w.Finish();
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
}
