using System.Buffers.Binary;
using System.Text;
using Kharos.Mod.Abi;
using Kharos.Mod.Sdk.Runtime;

namespace Kharos.Mod.Sdk;

/// <summary>
/// Буфер команд мода: что система, обработчик события или приказа хочет сделать с миром. Байты — ровно формат
/// буфера команд движка (Kharos.Engine.World.CommandBuffer): движок проверяет и применяет их после вызова как есть.
/// <code>
///   запись := op:u8 тело; varint — LEB128 без знака; str/bytes — varint длина и байты
///   0x01 Spawn entity · 0x02 Destroy entity · 0x03 Add / 0x04 Set entity component count:u8 (field:u8 value)×count
///   0x05 Remove entity component · 0x06 Emit name:str payload:bytes · 0x07 Signal name:str payload:bytes
///   value := 0x00 f64 · 0x01 false · 0x02 true · 0x03 id:varint · 0x04 str · 0x05 bytes
/// </code>
/// Значения полей пишутся вызовами <see cref="Number"/> и др. сразу после <see cref="Add"/> или <see cref="Set"/>.
/// </summary>
public sealed class CommandWriter
{
    private NativeBuffer _buffer;
    private int _countAt = -1;
    private uint _next;

    /// <summary>Буфер; nextEntity — номер, с которого раздаются номера Spawn (в моде его ставит хост перед вызовом).</summary>
    public CommandWriter(uint nextEntity = 1) => _next = nextEntity;

    /// <summary>Записано байт.</summary>
    public int Length => _buffer.Length;

    /// <summary>Записанные байты (до следующего вызова мода).</summary>
    public ReadOnlySpan<byte> Written => _buffer.Written;

    internal unsafe byte* Pointer => _buffer.Pointer;

    /// <summary>Начинает буфер заново; next — номер следующей сущности мира.</summary>
    internal void Reset(uint next)
    {
        _buffer.Clear();
        _countAt = -1;
        _next = next;
    }

    /// <summary>Новая сущность: номер — следующий по порядку, появится она после вызова.</summary>
    public uint Spawn()
    {
        var entity = _next++;
        Op(ModAbi.OpSpawn).Varint(entity);
        return entity;
    }

    /// <summary>Убирает сущность.</summary>
    public void Destroy(uint entity) => Op(ModAbi.OpDestroy).Varint(entity);

    /// <summary>Добавляет компонент (поля — по умолчанию; значения поверх — сразу следом).</summary>
    public CommandWriter Add(uint entity, ComponentRef component) => Fields(ModAbi.OpAdd, entity, component.Id);

    /// <summary>Меняет поля компонента: значения — сразу следом.</summary>
    public CommandWriter Set(uint entity, ComponentRef component) => Fields(ModAbi.OpSet, entity, component.Id);

    /// <summary>Снимает компонент.</summary>
    public void Remove(uint entity, ComponentRef component) => Op(ModAbi.OpRemove).Varint(entity).Varint((uint)component.Id);

    /// <summary>Событие обработчикам (после системы). Имя — UTF-8, например <c>"hello.arrived"u8</c>.</summary>
    public void Emit(ReadOnlySpan<byte> name, ReadOnlySpan<byte> payload = default) => Op(ModAbi.OpEmit).Blob(name).Blob(payload);

    /// <summary>Событие обработчикам (после системы).</summary>
    public void Emit(string name, ReadOnlySpan<byte> payload = default) => Op(ModAbi.OpEmit).Text(name).Blob(payload);

    /// <summary>Сигнал клиентам: звук, вспышка.</summary>
    public void Signal(ReadOnlySpan<byte> name, ReadOnlySpan<byte> payload = default) => Op(ModAbi.OpSignal).Blob(name).Blob(payload);

    /// <summary>Сигнал клиентам: звук, вспышка.</summary>
    public void Signal(string name, ReadOnlySpan<byte> payload = default) => Op(ModAbi.OpSignal).Text(name).Blob(payload);

    /// <summary>Число в поле.</summary>
    public CommandWriter Number(FieldRef field, double value)
    {
        Field(field).Byte(ModAbi.ValueNumber);
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.Room(8), value);
        _buffer.Length += 8;
        return this;
    }

    /// <summary>Флаг в поле.</summary>
    public CommandWriter Flag(FieldRef field, bool value) => Field(field).Byte(value ? ModAbi.ValueTrue : ModAbi.ValueFalse);

    /// <summary>Строка текстом: номер заведёт движок.</summary>
    public CommandWriter Text(FieldRef field, string value) => Field(field).Byte(ModAbi.ValueStringText).Text(value);

    /// <summary>Строка номером из строк мира (как лежит в столбце).</summary>
    public CommandWriter TextId(FieldRef field, uint id) => Field(field).Byte(ModAbi.ValueStringId).Varint(id);

    /// <summary>Байты в поле вида «байты».</summary>
    public CommandWriter Bytes(FieldRef field, ReadOnlySpan<byte> value) => Field(field).Byte(ModAbi.ValueBytes).Blob(value);

    private CommandWriter Fields(byte op, uint entity, int component)
    {
        Op(op).Varint(entity).Varint((uint)component);
        _countAt = _buffer.Length;
        return Byte(0);
    }

    private unsafe CommandWriter Field(FieldRef field)
    {
        if (_countAt < 0)
            throw new InvalidOperationException("значение поля — только сразу после Add или Set");
        ref var count = ref _buffer.Pointer[_countAt];
        if (count == ModAbi.MaxFields)
            throw new InvalidOperationException($"больше {ModAbi.MaxFields} полей в одной записи");
        if ((uint)field.Index >= ModAbi.MaxFields)
            throw new ArgumentOutOfRangeException(nameof(field));
        count++;
        return Byte((byte)field.Index);
    }

    private CommandWriter Op(byte op)
    {
        _countAt = -1;
        return Byte(op);
    }

    private CommandWriter Byte(byte value)
    {
        _buffer.Room(1)[0] = value;
        _buffer.Length++;
        return this;
    }

    private CommandWriter Varint(uint value)
    {
        var room = _buffer.Room(5);
        var at = 0;
        while (value >= 0x80)
        {
            room[at++] = (byte)(value | 0x80);
            value >>= 7;
        }
        room[at++] = (byte)value;
        _buffer.Length += at;
        return this;
    }

    private CommandWriter Blob(ReadOnlySpan<byte> value)
    {
        Varint((uint)value.Length);
        _buffer.Write(value);
        return this;
    }

    private CommandWriter Text(string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        Varint((uint)count);
        Encoding.UTF8.GetBytes(value, _buffer.Room(count));
        _buffer.Length += count;
        return this;
    }
}
