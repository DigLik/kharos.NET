using System.Buffers.Binary;
using System.Text;
using Kharos.Mod.Abi;
using Kharos.Mod.Sdk.Runtime;

namespace Kharos.Mod.Sdk;

/// <summary>Правка тайла земли (как TileEdit движка): местность 0–2 (гора — <see cref="CommandWriter.LandPeak"/>), биом, ярус, обрыв, барханы.</summary>
public readonly record struct LandEdit(byte Terrain, byte Biome, byte Tier, bool Cliff = false, byte Dunes = 0);

/// <summary>
/// Буфер команд мода: что система, обработчик события или приказа хочет сделать с миром. Байты — ровно формат
/// буфера команд движка (Kharos.Engine.World.CommandBuffer): движок проверяет и применяет их после вызова как есть.
/// <code>
///   запись := op:u8 тело; varint — LEB128 без знака; sint — zigzag поверх varint; str/bytes — varint длина и байты
///   0x01 Spawn entity · 0x02 Destroy entity (и всё с Mount.parent = entity) · 0x03 Add / 0x04 Set entity component
///   count:u8 (field:u8 value)×count · 0x05 Remove entity component · 0x06 Emit name:str payload:bytes · 0x07 Signal
///   name:str payload:bytes · 0x08 Layer layer owner channel:u8 x:sint y:sint w h value:f64 · 0x09 Item layer owner
///   mode:u8 x:f64 y:f64 [count:u8 f64×count] · 0x0A Land kind:u8 x:sint y:sint … · 0x0B SignalTo name payload count
///   player×count · 0x0C SignalAt name payload x:f64 y:f64
///   value := 0x00 f64 · 0x01 false · 0x02 true · 0x03 id:varint · 0x04 str · 0x05 bytes
/// </code>
/// Значения полей пишутся вызовами <see cref="Number"/> и др. сразу после <see cref="Add"/> или <see cref="Set"/>.
/// Сцена вызова (<see cref="Sdk.Stage"/>), если её взяли, видит каждую запись.
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

    /// <summary>Сцена этого вызова; null — не брали.</summary>
    internal Stage? Stage { get; set; }

    internal unsafe byte* Pointer => _buffer.Pointer;

    /// <summary>Начинает буфер заново; next — номер следующей сущности мира. Сцена прошлого вызова отпадает.</summary>
    internal void Reset(uint next)
    {
        _buffer.Clear();
        _countAt = -1;
        _next = next;
        Stage = null;
    }

    /// <summary>Освобождает память буфера (выгрузка мода в режиме CLR).</summary>
    internal void Free() => _buffer.Free();

    /// <summary>Новая сущность: номер — следующий по порядку, появится она после вызова.</summary>
    public uint Spawn()
    {
        var entity = _next++;
        Op(ModAbi.OpSpawn).Varint(entity);
        Stage?.OnSpawn(entity);
        return entity;
    }

    /// <summary>Убирает сущность (и всё, что на ней держится: Mount.parent).</summary>
    public void Destroy(uint entity)
    {
        Op(ModAbi.OpDestroy).Varint(entity);
        Stage?.OnDestroy(entity);
    }

    /// <summary>Добавляет компонент (поля — по умолчанию; значения поверх — сразу следом).</summary>
    public CommandWriter Add(uint entity, ComponentRef component)
    {
        Fields(ModAbi.OpAdd, entity, component.Id);
        Stage?.OnFields(true, entity, component);
        return this;
    }

    /// <summary>Меняет поля компонента: значения — сразу следом.</summary>
    public CommandWriter Set(uint entity, ComponentRef component)
    {
        Fields(ModAbi.OpSet, entity, component.Id);
        Stage?.OnFields(false, entity, component);
        return this;
    }

    /// <summary>Снимает компонент.</summary>
    public void Remove(uint entity, ComponentRef component)
    {
        Op(ModAbi.OpRemove).Varint(entity).Varint((uint)component.Id);
        Stage?.OnRemove(entity, component);
    }

    /// <summary>Событие обработчикам (после системы). Имя — UTF-8, например <c>"hello.arrived"u8</c>.</summary>
    public void Emit(ReadOnlySpan<byte> name, ReadOnlySpan<byte> payload = default) => Op(ModAbi.OpEmit).Blob(name).Blob(payload);

    /// <summary>Событие обработчикам (после системы).</summary>
    public void Emit(string name, ReadOnlySpan<byte> payload = default) => Op(ModAbi.OpEmit).Text(name).Blob(payload);

    /// <summary>Сигнал всем клиентам: звук, вспышка.</summary>
    public void Signal(ReadOnlySpan<byte> name, ReadOnlySpan<byte> payload = default) => Op(ModAbi.OpSignal).Blob(name).Blob(payload);

    /// <summary>Сигнал всем клиентам: звук, вспышка.</summary>
    public void Signal(string name, ReadOnlySpan<byte> payload = default) => Op(ModAbi.OpSignal).Text(name).Blob(payload);

    /// <summary>Сигнал только этим игрокам (отказы, события своих юнитов).</summary>
    public void SignalTo(string name, ReadOnlySpan<byte> payload, ReadOnlySpan<uint> players)
    {
        Op(ModAbi.OpSignalTo).Text(name).Blob(payload).Varint((uint)players.Length);
        foreach (var player in players)
            Varint(player);
    }

    /// <summary>Сигнал тем, кто видит точку (x, y): выстрел, взрыв.</summary>
    public void SignalAt(string name, ReadOnlySpan<byte> payload, double x, double y) => Op(ModAbi.OpSignalAt).Text(name).Blob(payload).F64(x).F64(y);

    /// <summary>Канал слоя-сетки в прямоугольнике w×h с левого верхнего тайла (x, y); owner 0 — слой мира, иначе игрока.</summary>
    public void Layer(LayerRef layer, uint owner, int channel, int x, int y, int w, int h, double value) =>
        Op(ModAbi.OpLayer).Varint((uint)layer.Id).Varint(owner).Byte((byte)channel).SVarint(x).SVarint(y).Varint((uint)w).Varint((uint)h).F64(value);

    /// <summary>Кладёт предмет в слой предметов (поля — по порядку объявления, недостающие — 0).</summary>
    public void PushItem(LayerRef layer, uint owner, double x, double y, ReadOnlySpan<double> fields)
    {
        if (fields.Length > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(fields));
        Op(ModAbi.OpItem).Varint((uint)layer.Id).Varint(owner).Byte(ModAbi.ItemPush).F64(x).F64(y).Byte((byte)fields.Length);
        foreach (var field in fields)
            F64(field);
    }

    /// <summary>Убирает предметы слоя, лежащие ровно в (x, y).</summary>
    public void DeleteItems(LayerRef layer, uint owner, double x, double y) =>
        Op(ModAbi.OpItem).Varint((uint)layer.Id).Varint(owner).Byte(ModAbi.ItemDelete).F64(x).F64(y);

    /// <summary>Правка тайла земли (рассылается клиентам).</summary>
    public void LandTile(int x, int y, LandEdit edit) =>
        Op(ModAbi.OpLand).Byte(ModAbi.LandTile).SVarint(x).SVarint(y)
            .Byte(edit.Terrain).Byte(edit.Biome).Byte(edit.Tier).Byte(edit.Cliff ? ModAbi.LandCliff : (byte)0).Byte(edit.Dunes);

    /// <summary>Тайл (x, y) — часть горы с вершиной (px, py) радиуса radius.</summary>
    public void LandPeak(int x, int y, byte biome, double px, double py, double radius) =>
        Op(ModAbi.OpLand).Byte(ModAbi.LandPeak).SVarint(x).SVarint(y).Byte(biome).F64(px).F64(py).F64(radius);

    /// <summary>Число в поле.</summary>
    public CommandWriter Number(FieldRef field, double value)
    {
        Field(field).Byte(ModAbi.ValueNumber).F64(value);
        Stage?.OnNumber(field, value);
        return this;
    }

    /// <summary>Флаг в поле.</summary>
    public CommandWriter Flag(FieldRef field, bool value)
    {
        Field(field).Byte(value ? ModAbi.ValueTrue : ModAbi.ValueFalse);
        Stage?.OnNumber(field, value ? 1 : 0);
        return this;
    }

    /// <summary>Строка текстом: номер заведёт движок.</summary>
    public CommandWriter Text(FieldRef field, string value)
    {
        Field(field).Byte(ModAbi.ValueStringText).Text(value);
        Stage?.OnText(field, value);
        return this;
    }

    /// <summary>Строка номером из строк мира (как лежит в столбце).</summary>
    public CommandWriter TextId(FieldRef field, uint id)
    {
        Field(field).Byte(ModAbi.ValueStringId).Varint(id);
        Stage?.OnNumber(field, id);
        return this;
    }

    /// <summary>Байты в поле вида «байты».</summary>
    public CommandWriter Bytes(FieldRef field, ReadOnlySpan<byte> value)
    {
        Field(field).Byte(ModAbi.ValueBytes).Blob(value);
        Stage?.OnBytes(field, value);
        return this;
    }

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

    private CommandWriter F64(double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.Room(8), value);
        _buffer.Length += 8;
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

    private CommandWriter SVarint(int value) => Varint((uint)((value << 1) ^ (value >> 31)));

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
