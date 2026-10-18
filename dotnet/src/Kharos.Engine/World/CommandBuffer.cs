using System.Buffers.Binary;
using System.Text;

namespace Kharos.Engine.World;

/// <summary>Коды записей буфера команд.</summary>
public static class CommandOp
{
    public const byte Spawn = 0x01;
    public const byte Destroy = 0x02;
    public const byte Add = 0x03;
    public const byte Set = 0x04;
    public const byte Remove = 0x05;
    public const byte Emit = 0x06;
    public const byte Signal = 0x07;
}

/// <summary>Метки значений поля в записях Add и Set.</summary>
public static class ValueTag
{
    public const byte Number = 0x00;
    public const byte False = 0x01;
    public const byte True = 0x02;
    public const byte StringId = 0x03;
    public const byte StringText = 0x04;
    public const byte Bytes = 0x05;
}

/// <summary>
/// Буфер команд: что система (или обработчик события, приказа) хочет сделать с миром. Пишется во время системы,
/// применяется пакетом после неё (<see cref="Commands.Apply"/>). Мод на wasm пишет те же байты в своей памяти, движок
/// их читает — формат и есть ABI.
/// <code>
/// Всё little-endian; varint — LEB128 без знака (u32 — до 5 байт); str — varint длина и UTF-8; bytes — varint длина и байты.
///
///   запись := op:u8 тело
///   0x01 Spawn    entity:varint
///   0x02 Destroy  entity:varint
///   0x03 Add      entity:varint component:varint count:u8 (field:u8 value) × count
///   0x04 Set      entity:varint component:varint count:u8 (field:u8 value) × count
///   0x05 Remove   entity:varint component:varint
///   0x06 Emit     name:str payload:bytes      — событие обработчикам после системы
///   0x07 Signal   name:str payload:bytes      — сигнал клиентам: звук, вспышка
///
///   value := tag:u8 …
///   0x00 число      f64
///   0x01 false, 0x02 true                     — флаг
///   0x03 строка     id:varint                 — номер из строк мира
///   0x04 строка     str                       — текстом, номер заведёт движок
///   0x05 байты      bytes                     — поле вида Bytes
/// </code>
/// component — номер в схеме, field — <see cref="FieldDef.Index"/>. Номер новой сущности в Spawn — заранее выданный
/// (<see cref="GameWorld.Reserve"/>) или ровно <see cref="GameWorld.NextEntity"/> на миг записи: мод, которому перед
/// системой сказали следующий номер, раздаёт номера подряд сам. Add и Set на сущность, созданную в том же буфере,
/// — после её Spawn.
/// </summary>
public sealed class CommandBuffer
{
    private readonly GameWorld? _world;
    private byte[] _bytes = new byte[256];
    private int _length;
    private int _countAt = -1;

    /// <param name="world">Мир, из которого <see cref="Spawn()"/> берёт номера; null — только явные номера.</param>
    public CommandBuffer(GameWorld? world = null) => _world = world;

    public ReadOnlySpan<byte> Written => _bytes.AsSpan(0, _length);
    public int Length => _length;
    public bool IsEmpty => _length == 0;

    public void Clear()
    {
        _length = 0;
        _countAt = -1;
    }

    /// <summary>Новая сущность: номер выдаётся сразу, появится она после системы.</summary>
    public uint Spawn()
    {
        var entity = (_world ?? throw new InvalidOperationException("буфер без мира: номер сущности — явно")).Reserve();
        Spawn(entity);
        return entity;
    }

    public void Spawn(uint entity) => Op(CommandOp.Spawn).Varint(entity);

    public void Destroy(uint entity) => Op(CommandOp.Destroy).Varint(entity);

    /// <summary>Добавляет компонент (поля — по умолчанию); значения поверх — вызовами <see cref="Number"/> и др. сразу после.</summary>
    public CommandBuffer Add(uint entity, int component) => Fields(CommandOp.Add, entity, component);

    /// <summary>Меняет поля компонента: значения — вызовами <see cref="Number"/> и др. сразу после.</summary>
    public CommandBuffer Set(uint entity, int component) => Fields(CommandOp.Set, entity, component);

    public void Remove(uint entity, int component)
    {
        Op(CommandOp.Remove).Varint(entity).Varint((uint)component);
    }

    public void Emit(string name, ReadOnlySpan<byte> payload = default) => Op(CommandOp.Emit).Text(name).Blob(payload);

    public void Signal(string name, ReadOnlySpan<byte> payload = default) => Op(CommandOp.Signal).Text(name).Blob(payload);

    public CommandBuffer Number(int field, double value)
    {
        Field(field).Byte(ValueTag.Number);
        BinaryPrimitives.WriteDoubleLittleEndian(Room(8), value);
        _length += 8;
        return this;
    }

    public CommandBuffer Flag(int field, bool value)
    {
        Field(field).Byte(value ? ValueTag.True : ValueTag.False);
        return this;
    }

    public CommandBuffer Text(int field, string value)
    {
        Field(field).Byte(ValueTag.StringText).Text(value);
        return this;
    }

    public CommandBuffer TextId(int field, uint id)
    {
        Field(field).Byte(ValueTag.StringId).Varint(id);
        return this;
    }

    public CommandBuffer Bytes(int field, ReadOnlySpan<byte> value)
    {
        Field(field).Byte(ValueTag.Bytes).Blob(value);
        return this;
    }

    /// <summary>Значение любого вида — для удобного API, не для горячих путей.</summary>
    public CommandBuffer Value(int field, FieldValue value) => value.Kind switch
    {
        FieldKind.Number => Number(field, value.Number),
        FieldKind.Flag => Flag(field, value.Flag),
        FieldKind.String => Text(field, value.Text ?? ""),
        _ => Bytes(field, value.Bytes),
    };

    /// <summary>Дописывает готовые записи (буфер мода) как есть.</summary>
    public void Append(ReadOnlySpan<byte> records)
    {
        _countAt = -1;
        records.CopyTo(Room(records.Length));
        _length += records.Length;
    }

    private CommandBuffer Fields(byte op, uint entity, int component)
    {
        Op(op).Varint(entity).Varint((uint)component);
        _countAt = _length;
        return Byte(0);
    }

    private CommandBuffer Field(int field)
    {
        if (_countAt < 0)
            throw new InvalidOperationException("значение поля — только сразу после Add или Set");
        if (_bytes[_countAt] == Schema.MaxFields)
            throw new InvalidOperationException($"больше {Schema.MaxFields} полей в одной записи");
        if ((uint)field >= Schema.MaxFields)
            throw new ArgumentOutOfRangeException(nameof(field));
        _bytes[_countAt]++;
        return Byte((byte)field);
    }

    private CommandBuffer Op(byte op)
    {
        _countAt = -1;
        return Byte(op);
    }

    private CommandBuffer Byte(byte value)
    {
        Room(1)[0] = value;
        _length++;
        return this;
    }

    private CommandBuffer Varint(uint value)
    {
        var room = Room(5);
        var at = 0;
        while (value >= 0x80)
        {
            room[at++] = (byte)(value | 0x80);
            value >>= 7;
        }
        room[at++] = (byte)value;
        _length += at;
        return this;
    }

    private CommandBuffer Blob(ReadOnlySpan<byte> value)
    {
        Varint((uint)value.Length);
        value.CopyTo(Room(value.Length));
        _length += value.Length;
        return this;
    }

    private CommandBuffer Text(string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        Varint((uint)count);
        Encoding.UTF8.GetBytes(value, Room(count));
        _length += count;
        return this;
    }

    private Span<byte> Room(int count)
    {
        if (_length + count > _bytes.Length)
            Array.Resize(ref _bytes, Math.Max(_length + count, _bytes.Length * 2));
        return _bytes.AsSpan(_length, count);
    }
}
