using System.Buffers.Binary;
using System.Text;
using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Mod.Abi;

namespace Kharos.Engine.World;

/// <summary>Событие обработчикам: имя и байты (формат знает тот, кто шлёт).</summary>
public readonly record struct WorldEvent(string Name, byte[] Payload);

/// <summary>
/// Сигнал клиентам: выстрел, взрыв, готовый юнит — им звучать и вспыхивать. Адресат — все (<see cref="To"/> и
/// <see cref="At"/> пусты), список игроков (SignalTo) или те, кто видит точку (SignalAt): разводит их сеть хоста.
/// </summary>
public readonly record struct Signal(string Name, byte[] Payload)
{
    /// <summary>Только этим игрокам; null — не по списку.</summary>
    public uint[]? To { get; init; }

    /// <summary>Тем, кто видит эту точку; null — не по месту.</summary>
    public (double X, double Y)? At { get; init; }

    /// <summary>Всем: ни списка, ни места.</summary>
    public bool Broadcast => To is null && At is null;
}

/// <summary>Буфер команд не прошёл проверку: ни одна его запись не применена.</summary>
public sealed class CommandException(string message, int offset) : Exception($"команда @{offset}: {message}")
{
    /// <summary>Смещение записи в буфере.</summary>
    public int Offset { get; } = offset;
}

/// <summary>Что, кроме мира, меняют команды: слои (Layer, Item) и землю (Land). null — у партии этого нет.</summary>
public readonly record struct CommandScope(LayerSet? Layers, Land? Land);

/// <summary>Применение буфера команд (<see cref="CommandBuffer"/>) к миру.</summary>
public static class Commands
{
    /// <summary>Событие движка о новой сущности: payload — entity:u32, proto:f64 (Proto.id; нет — -1).</summary>
    public const string Spawned = "spawned";

    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>
    /// Проверяет буфер целиком и, если он верен, применяет записи по порядку. Неверный буфер (обрыв, незнакомая
    /// запись, нет компонента, поля или слоя, значение не того вида, номер сущности, которого не было) —
    /// <see cref="CommandException"/>, мир не тронут. Записи о сущности, пропавшей к этому времени (её убрал кто-то
    /// раньше в том же тике), пропускаются молча; Set на сущность без компонента — тоже; слой игрока, которого нет, —
    /// тоже. Уничтожение уносит и сущности с <c>Mount.parent</c> = она (носитель — турели). События (<c>Emit</c>, затем
    /// <see cref="Spawned"/> о новых сущностях) и сигналы дописываются в events и signals.
    /// </summary>
    public static void Apply(GameWorld world, ReadOnlySpan<byte> buffer, List<WorldEvent> events, List<Signal> signals, CommandScope scope = default)
    {
        Validate(world, buffer, scope);
        ApplyValidated(world, buffer, events, signals, scope);
    }

    /// <summary>Только проверка: тот же разбор, что у <see cref="Apply"/>, мир не меняется.</summary>
    public static void Validate(GameWorld world, ReadOnlySpan<byte> buffer, CommandScope scope = default) => Run(world, buffer, scope, null, null, null);

    /// <summary>Применение проверенного буфера (между проверкой и применением состав мира не менялся).</summary>
    internal static void ApplyValidated(GameWorld world, ReadOnlySpan<byte> buffer, List<WorldEvent> events, List<Signal> signals, CommandScope scope = default)
    {
        if (buffer.IsEmpty)
            return;
        var spawned = new List<uint>();
        Run(world, buffer, scope, events, signals, spawned);
        foreach (var entity in spawned)
        {
            var payload = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, entity);
            var proto = world.TryNumber(entity, Builtin.Proto, 0, out var id) ? id : -1;
            BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(4), proto);
            events.Add(new WorldEvent(Spawned, payload));
        }
    }

    /// <summary>Убирает сущность и всё, что на ней держится (Mount.parent), глубже — тоже.</summary>
    public static void DestroyWithMounts(GameWorld world, uint entity)
    {
        if (!world.Alive(entity))
            return;
        world.Destroy(entity);
        const int mount = Builtin.Mount;
        if (world.Schema[mount].Field(ModAbi.MountParent) is not { Columnar: true } parent)
            return;
        var store = world.Store(mount);
        if (store.Count == 0)
            return;
        var pending = new Stack<uint>();
        pending.Push(entity);
        var children = new List<uint>();
        while (pending.Count > 0)
        {
            var gone = (double)pending.Pop();
            children.Clear();
            var parents = store.RawColumn(parent.Slot);
            var entities = store.Entities;
            for (var row = 0; row < entities.Length; row++)
                if (parents[row] == gone)
                    children.Add(entities[row]);
            foreach (var child in children)
                if (world.Alive(child))
                {
                    world.Destroy(child);
                    pending.Push(child);
                }
        }
    }

    /// <summary>Проход по буферу: без events — только проверка, с ними — применение.</summary>
    private static void Run(GameWorld world, ReadOnlySpan<byte> buffer, CommandScope scope, List<WorldEvent>? events, List<Signal>? signals, List<uint>? spawned)
    {
        var apply = events is not null;
        var schema = world.Schema;
        var reader = new Reader(buffer);
        var next = world.NextEntity;
        HashSet<uint>? born = apply ? null : [];
        while (!reader.End)
        {
            var start = reader.At;
            var op = reader.Byte();
            switch (op)
            {
                case CommandOp.Spawn:
                {
                    var entity = reader.Varint();
                    if (entity == next)
                        next++;
                    else if (!world.IsReserved(entity) || (born is not null && born.Contains(entity)))
                        throw new CommandException($"Spawn {entity}: номер не выдан или уже жил", start);
                    born?.Add(entity);
                    if (apply)
                    {
                        if (entity == world.NextEntity)
                            world.Reserve();
                        world.Revive(entity);
                        spawned!.Add(entity);
                    }
                    break;
                }
                case CommandOp.Destroy:
                {
                    var entity = Entity(ref reader, next, start);
                    if (apply)
                        DestroyWithMounts(world, entity);
                    break;
                }
                case CommandOp.Add:
                case CommandOp.Set:
                {
                    var entity = Entity(ref reader, next, start);
                    var component = Component(ref reader, schema, start);
                    var def = schema[component];
                    var live = apply && world.Alive(entity) && (op == CommandOp.Add || world.Has(entity, component));
                    var row = live ? world.Add(entity, component) : -1;
                    var store = world.Store(component);
                    var count = reader.Byte();
                    if (count > Schema.MaxFields)
                        throw new CommandException($"{def.Name}: {count} полей", start);
                    for (var i = 0; i < count; i++)
                    {
                        var index = reader.Byte();
                        if (index >= def.Fields.Count)
                            throw new CommandException($"у {def.Name} нет поля №{index}", start);
                        var field = def.Fields[index];
                        Value(ref reader, world, store, row, field, start);
                    }
                    break;
                }
                case CommandOp.Remove:
                {
                    var entity = Entity(ref reader, next, start);
                    var component = Component(ref reader, schema, start);
                    if (apply)
                        world.Remove(entity, component);
                    break;
                }
                case CommandOp.Emit:
                case CommandOp.Signal:
                {
                    var (name, payload) = Named(ref reader, start);
                    if (op == CommandOp.Emit)
                        events?.Add(new WorldEvent(name, payload));
                    else
                        signals?.Add(new Signal(name, payload));
                    break;
                }
                case CommandOp.SignalTo:
                {
                    var (name, payload) = Named(ref reader, start);
                    var count = reader.Varint();
                    if (count > (uint)reader.Left)
                        throw new CommandException($"SignalTo: {count} игроков", start);
                    var players = new uint[count];
                    for (var i = 0; i < players.Length; i++)
                        players[i] = reader.Varint();
                    signals?.Add(new Signal(name, payload) { To = players });
                    break;
                }
                case CommandOp.SignalAt:
                {
                    var (name, payload) = Named(ref reader, start);
                    var (x, y) = (reader.Double(), reader.Double());
                    if (!double.IsFinite(x) || !double.IsFinite(y))
                        throw new CommandException("SignalAt: точка не число", start);
                    signals?.Add(new Signal(name, payload) { At = (x, y) });
                    break;
                }
                case CommandOp.Layer:
                {
                    var (layers, layer, owner) = Layer(ref reader, scope, start);
                    var spec = layers.Spec(layer);
                    var channel = reader.Byte();
                    var (x, y) = (reader.SVarint(), reader.SVarint());
                    var (w, h) = (reader.Varint(), reader.Varint());
                    var value = reader.Double();
                    if (!spec.IsGrid)
                        throw new CommandException($"слой {spec.Name}: предметы, а не сетка", start);
                    if (channel >= spec.Channels.Count)
                        throw new CommandException($"слой {spec.Name}: нет канала {channel}", start);
                    if ((ulong)w * h > ModAbi.MaxLayerRect || (long)x + w > int.MaxValue || (long)y + h > int.MaxValue)
                        throw new CommandException($"слой {spec.Name}: прямоугольник {w}×{h}", start);
                    if (!double.IsFinite(value))
                        throw new CommandException($"слой {spec.Name}: значение не число", start);
                    if (apply)
                        for (var tileY = 0; tileY < h; tileY++)
                            for (var tileX = 0; tileX < w; tileX++)
                                layers.Set(layer, owner, x + tileX, y + tileY, channel, value);
                    break;
                }
                case CommandOp.Item:
                {
                    var (layers, layer, owner) = Layer(ref reader, scope, start);
                    var spec = layers.Spec(layer);
                    var mode = reader.Byte();
                    var (x, y) = (reader.Double(), reader.Double());
                    if (spec.IsGrid)
                        throw new CommandException($"слой {spec.Name}: сетка, а не предметы", start);
                    if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 1e9 || Math.Abs(y) > 1e9)
                        throw new CommandException($"слой {spec.Name}: место предмета ({x}, {y})", start);
                    switch (mode)
                    {
                        case ModAbi.ItemPush:
                        {
                            var count = reader.Byte();
                            if (count > spec.Fields.Count)
                                throw new CommandException($"слой {spec.Name}: {count} полей из {spec.Fields.Count}", start);
                            var fields = new double[spec.Fields.Count];
                            for (var i = 0; i < count; i++)
                                fields[i] = reader.Double();
                            if (apply)
                                layers.Push(layer, owner, new LayerItem(x, y, fields));
                            break;
                        }
                        case ModAbi.ItemDelete:
                            if (apply)
                                layers.Retain(layer, owner, layers.CellOf(layer, x, y), item =>
                                    BitConverter.DoubleToInt64Bits(item.X) != BitConverter.DoubleToInt64Bits(x) ||
                                    BitConverter.DoubleToInt64Bits(item.Y) != BitConverter.DoubleToInt64Bits(y));
                            break;
                        default:
                            throw new CommandException($"Item: незнакомый режим {mode}", start);
                    }
                    break;
                }
                case CommandOp.Land:
                {
                    var kind = reader.Byte();
                    var (x, y) = (reader.SVarint(), reader.SVarint());
                    var land = scope.Land ?? throw new CommandException("Land: у партии нет земли", start);
                    switch (kind)
                    {
                        case ModAbi.LandTile:
                        {
                            var (terrain, biome, tier, flags, dunes) = (reader.Byte(), reader.Byte(), reader.Byte(), reader.Byte(), reader.Byte());
                            if (terrain >= (byte)TerrainType.Mountain || biome > (byte)Biome.Marsh || tier > TileFormat.MaxTier || dunes > (byte)Dunes.Many || (flags & ~ModAbi.LandCliff) != 0)
                                throw new CommandException($"Land: тайл ({terrain}, {biome}, {tier}, {flags}, {dunes}) не той формы", start);
                            if (apply)
                                land.SetTile(x, y, new TileEdit((TerrainType)terrain, biome, tier, (flags & ModAbi.LandCliff) != 0, (Dunes)dunes));
                            break;
                        }
                        case ModAbi.LandPeak:
                        {
                            var biome = reader.Byte();
                            var (px, py, radius) = (reader.Double(), reader.Double(), reader.Double());
                            if (biome > (byte)Biome.Marsh || !double.IsFinite(px) || !double.IsFinite(py) || !(radius > 0 && radius <= TileFormat.PeakRadiusScale))
                                throw new CommandException("Land: вершина не той формы", start);
                            if (apply)
                                land.SetPeakTile(x, y, biome, px, py, radius);
                            break;
                        }
                        default:
                            throw new CommandException($"Land: незнакомый вид {kind}", start);
                    }
                    break;
                }
                default:
                    throw new CommandException($"незнакомая запись 0x{op:x2}", start);
            }
        }
    }

    private static uint Entity(ref Reader reader, uint next, int start)
    {
        var entity = reader.Varint();
        return entity != 0 && entity < next ? entity : throw new CommandException($"сущности {entity} не было", start);
    }

    private static int Component(ref Reader reader, Schema schema, int start)
    {
        var component = reader.Varint();
        return component < (uint)schema.Count ? (int)component : throw new CommandException($"нет компонента №{component}", start);
    }

    /// <summary>Имя и payload события или сигнала.</summary>
    private static (string Name, byte[] Payload) Named(ref Reader reader, int start)
    {
        var name = reader.Text(start);
        if (name.Length == 0)
            throw new CommandException("событие без имени", start);
        return (name, reader.Blob().ToArray());
    }

    /// <summary>Слой и владелец записи Layer или Item: owner 0 — мир, иначе игрок; сторона владельца — как у слоя.</summary>
    private static (LayerSet Layers, int Layer, LayerOwner Owner) Layer(ref Reader reader, CommandScope scope, int start)
    {
        var id = reader.Varint();
        var player = reader.Varint();
        var layers = scope.Layers ?? throw new CommandException("у партии нет слоёв", start);
        if (id >= (uint)layers.Count)
            throw new CommandException($"нет слоя №{id}", start);
        var spec = layers.Spec((int)id);
        if ((spec.Scope == LayerScope.Player) != (player != 0))
            throw new CommandException(player == 0 ? $"слой {spec.Name} — у игроков, а не у мира" : $"слой {spec.Name} — у мира, а не у игроков", start);
        return (layers, (int)id, player == 0 ? LayerOwner.World : LayerOwner.Player(player));
    }

    /// <summary>Читает значение поля; row ≥ 0 — пишет его в строку.</summary>
    private static void Value(ref Reader reader, GameWorld world, ComponentStore store, int row, FieldDef field, int start)
    {
        var tag = reader.Byte();
        double number;
        switch (tag)
        {
            case ValueTag.Number when field.Kind == FieldKind.Number:
                number = reader.Double();
                break;
            case ValueTag.False or ValueTag.True when field.Kind == FieldKind.Flag:
                number = tag == ValueTag.True ? 1 : 0;
                break;
            case ValueTag.StringId when field.Kind == FieldKind.String:
            {
                var id = reader.Varint();
                if (!world.Strings.Contains(id))
                    throw new CommandException($"{store.Def.Name}.{field.Name}: нет строки №{id}", start);
                number = id;
                break;
            }
            case ValueTag.StringText when field.Kind == FieldKind.String:
            {
                var text = reader.Text(start);
                number = row >= 0 ? world.Strings.Id(text) : 0;
                break;
            }
            case ValueTag.Bytes when field.Kind == FieldKind.Bytes:
            {
                var bytes = reader.Blob();
                if (row >= 0)
                    world.WriteBytes(store, row, field, bytes.ToArray());
                return;
            }
            default:
                throw new CommandException($"{store.Def.Name}.{field.Name}: значение 0x{tag:x2} — не {Schema.Word(field.Kind)}", start);
        }
        if (row >= 0)
            world.WriteCell(store, row, field, number);
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;

        public int At { get; private set; }
        public readonly bool End => At >= _bytes.Length;
        public readonly int Left => _bytes.Length - At;

        public byte Byte() => Take(1)[0];

        public double Double() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

        public uint Varint()
        {
            uint value = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var part = Byte();
                if (shift == 28 && part > 0x0f)
                    throw new CommandException("varint длиннее u32", At - 1);
                value |= (uint)(part & 0x7f) << shift;
                if (part < 0x80)
                    return value;
            }
            throw new CommandException("varint длиннее u32", At);
        }

        /// <summary>Целое со знаком: zigzag поверх varint.</summary>
        public int SVarint()
        {
            var raw = Varint();
            return (int)(raw >> 1) ^ -(int)(raw & 1);
        }

        public ReadOnlySpan<byte> Blob() => Take(checked((int)Math.Min(Varint(), int.MaxValue)));

        public string Text(int start)
        {
            var bytes = Blob();
            try
            {
                return Utf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new CommandException("строка не UTF-8", start);
            }
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count > _bytes.Length - At)
                throw new CommandException("буфер обрывается", At);
            var part = _bytes.Slice(At, count);
            At += count;
            return part;
        }
    }
}
