using System.Buffers.Binary;
using System.Text;

namespace Kharos.Engine.World;

/// <summary>Событие обработчикам: имя и байты (формат знает тот, кто шлёт).</summary>
public readonly record struct WorldEvent(string Name, byte[] Payload);

/// <summary>Сигнал клиентам: выстрел, взрыв, готовый юнит — им звучать и вспыхивать.</summary>
public readonly record struct Signal(string Name, byte[] Payload);

/// <summary>Буфер команд не прошёл проверку: ни одна его запись не применена.</summary>
public sealed class CommandException(string message, int offset) : Exception($"команда @{offset}: {message}")
{
    /// <summary>Смещение записи в буфере.</summary>
    public int Offset { get; } = offset;
}

/// <summary>Применение буфера команд (<see cref="CommandBuffer"/>) к миру.</summary>
public static class Commands
{
    /// <summary>Событие движка о новой сущности: payload — entity:u32, proto:f64 (Proto.id; нет — -1).</summary>
    public const string Spawned = "spawned";

    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>
    /// Проверяет буфер целиком и, если он верен, применяет записи по порядку. Неверный буфер (обрыв, незнакомая
    /// запись, нет компонента или поля, значение не того вида, номер сущности, которого не было) —
    /// <see cref="CommandException"/>, мир не тронут. Записи о сущности, пропавшей к этому времени (её убрал кто-то
    /// раньше в том же тике), пропускаются молча; Set на сущность без компонента — тоже. События (<c>Emit</c>, затем
    /// <see cref="Spawned"/> о новых сущностях) и сигналы дописываются в events и signals.
    /// </summary>
    public static void Apply(GameWorld world, ReadOnlySpan<byte> buffer, List<WorldEvent> events, List<Signal> signals)
    {
        Validate(world, buffer);
        ApplyValidated(world, buffer, events, signals);
    }

    /// <summary>Только проверка: тот же разбор, что у <see cref="Apply"/>, мир не меняется.</summary>
    public static void Validate(GameWorld world, ReadOnlySpan<byte> buffer) => Run(world, buffer, null, null, null);

    /// <summary>Применение проверенного буфера (между проверкой и применением состав мира не менялся).</summary>
    internal static void ApplyValidated(GameWorld world, ReadOnlySpan<byte> buffer, List<WorldEvent> events, List<Signal> signals)
    {
        var spawned = new List<uint>();
        Run(world, buffer, events, signals, spawned);
        foreach (var entity in spawned)
        {
            var payload = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, entity);
            var proto = world.TryNumber(entity, Builtin.Proto, 0, out var id) ? id : -1;
            BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(4), proto);
            events.Add(new WorldEvent(Spawned, payload));
        }
    }

    /// <summary>Проход по буферу: без events — только проверка, с ними — применение.</summary>
    private static void Run(GameWorld world, ReadOnlySpan<byte> buffer, List<WorldEvent>? events, List<Signal>? signals, List<uint>? spawned)
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
                        world.Destroy(entity);
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
                    var name = reader.Text(start);
                    if (name.Length == 0)
                        throw new CommandException("событие без имени", start);
                    var payload = reader.Blob();
                    if (op == CommandOp.Emit)
                        events?.Add(new WorldEvent(name, payload.ToArray()));
                    else
                        signals?.Add(new Signal(name, payload.ToArray()));
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
