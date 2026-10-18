using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

/// <summary>Местность в точке: вид (0 — болото, 1 — песок, 2 — скала, 3 — гора), биом и подножие ли обрыва.</summary>
public readonly record struct TerrainInfo(byte Terrain, byte Biome, bool Foot);

/// <summary>Значения компонента сущности (<see cref="Tools.Get"/>): поля по номеру.</summary>
public sealed class ComponentValues
{
    private readonly double[] _numbers;
    private readonly byte[]?[] _bytes;

    internal ComponentValues(double[] numbers, byte[]?[] bytes)
    {
        _numbers = numbers;
        _bytes = bytes;
    }

    /// <summary>Число поля (флаг — 0 или 1, строка — номер в строках мира).</summary>
    public double Number(FieldRef field) => _numbers[field.Index];

    public bool Flag(FieldRef field) => _numbers[field.Index] != 0;

    /// <summary>Байты поля вида «байты».</summary>
    public byte[] Bytes(FieldRef field) => _bytes[field.Index] ?? [];
}

/// <summary>Инструмент вернул ошибку (нет инструмента, вход не разобран).</summary>
public sealed class ToolException(int tool, int code) : Exception($"инструмент {tool}: код {code}")
{
    public int Tool { get; } = tool;
    public int Code { get; } = code;
}

/// <summary>
/// Инструменты движка (kh.tool, docs/dotnet.md §4.1, docs/dotnet-n5.md Н5.3): только чтение мира и пакетные расчёты.
/// Мир — каким он был до идущей системы (её столбцы ещё не вернулись; свои записи читает <see cref="Stage"/>). Форматы —
/// ModAbi.Tool*; инструменты Н5 (11–29) — каждый в своём файле Tools.&lt;Имя&gt;.cs. Ещё не сделанный движком
/// инструмент — <see cref="ToolException"/> с кодом -1.
/// </summary>
public static partial class Tools
{
    private static byte[] _input = new byte[256];
    private static int _length;
    private static byte[] _output = new byte[4096];

    /// <summary>Сущности с Position в круге (x, y, r) и со всеми components — по возрастанию номеров.</summary>
    public static uint[] Near(double x, double y, double radius, params ReadOnlySpan<ComponentRef> components)
    {
        Begin().U32(1).U32((uint)components.Length);
        foreach (var component in components)
            U32((uint)component.Id);
        F64(x).F64(y).F64(radius);
        var output = Call(ModAbi.ToolNear);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(output);
        return MemoryMarshal.Cast<byte, uint>(output.Slice(4, count * 4)).ToArray();
    }

    /// <summary>Поиск в кругах пакетом: на каждую точку — свой список.</summary>
    public static uint[][] NearMany(ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> radius, params ReadOnlySpan<ComponentRef> components)
    {
        var m = x.Length;
        Begin().U32((uint)m).U32((uint)components.Length);
        foreach (var component in components)
            U32((uint)component.Id);
        for (var i = 0; i < m; i++)
            F64(x[i]).F64(y[i]).F64(radius[i]);
        var output = Call(ModAbi.ToolNear);
        var lists = new uint[m][];
        var at = 0;
        for (var i = 0; i < m; i++)
        {
            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(output[at..]);
            lists[i] = MemoryMarshal.Cast<byte, uint>(output.Slice(at + 4, count * 4)).ToArray();
            at += 4 + count * 4;
        }
        return lists;
    }

    /// <summary>
    /// Расталкивает корпуса (как ctx:separate пилота): x и y меняются на месте, radius — радиусы корпусов, moving —
    /// идёт ли (пусто — все стоят); strength — доля перекрытия, снимаемая за вызов.
    /// </summary>
    public static void Separate(Span<double> x, Span<double> y, ReadOnlySpan<double> radius, ReadOnlySpan<double> moving, double strength = 0.5)
    {
        var n = x.Length;
        if (y.Length != n || radius.Length != n || (!moving.IsEmpty && moving.Length != n))
            throw new ArgumentException("столбцы разной длины");
        Begin().U32((uint)n).F64(strength).U32(moving.IsEmpty ? 0u : 1u);
        Doubles(x).Doubles(y).Doubles(radius);
        if (!moving.IsEmpty)
            Doubles(moving);
        var output = Call(ModAbi.ToolSeparate);
        MemoryMarshal.Cast<byte, double>(output[..(n * 8)]).CopyTo(x);
        MemoryMarshal.Cast<byte, double>(output.Slice(n * 8, n * 8)).CopyTo(y);
    }

    /// <summary>Местность в точке; false — у партии нет местности.</summary>
    public static bool Terrain(double x, double y, out TerrainInfo info)
    {
        Begin().U32(1).F64(x).F64(y);
        var output = Call(ModAbi.ToolTerrain);
        info = output.Length >= 3 ? new TerrainInfo(output[0], output[1], output[2] != 0) : default;
        return output.Length >= 3;
    }

    /// <summary>Местность в точках пакетом; false — у партии нет местности.</summary>
    public static bool Terrain(ReadOnlySpan<double> x, ReadOnlySpan<double> y, Span<TerrainInfo> into)
    {
        var n = x.Length;
        Begin().U32((uint)n);
        for (var i = 0; i < n; i++)
            F64(x[i]).F64(y[i]);
        var output = Call(ModAbi.ToolTerrain);
        if (output.Length < n * 3)
            return false;
        for (var i = 0; i < n; i++)
            into[i] = new TerrainInfo(output[i * 3], output[i * 3 + 1], output[i * 3 + 2] != 0);
        return true;
    }

    /// <summary>Значения компонента сущности сейчас; нет компонента — null.</summary>
    public static ComponentValues? Get(uint entity, ComponentRef component)
    {
        Begin().U32(entity).U32((uint)component.Id);
        var output = Call(ModAbi.ToolGet);
        if (output.IsEmpty)
            return null;
        var fields = component.Fields;
        var numbers = new double[fields.Count];
        var bytes = new byte[]?[fields.Count];
        var at = 0;
        for (var i = 0; i < fields.Count; i++)
        {
            if (fields[i].Columnar)
            {
                numbers[i] = BinaryPrimitives.ReadDoubleLittleEndian(output[at..]);
                at += 8;
            }
            else
            {
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(output[at..]);
                bytes[i] = output.Slice(at + 4, length).ToArray();
                at += 4 + length;
            }
        }
        return new ComponentValues(numbers, bytes);
    }

    public static bool Has(uint entity, ComponentRef component) => HasOne(component.Id, entity);

    public static bool Alive(uint entity) => HasOne(-1, entity);

    /// <summary>Есть ли компонент у сущностей пакетом (component null — живы ли).</summary>
    public static void Has(ReadOnlySpan<uint> entities, ComponentRef? component, Span<bool> into)
    {
        Begin().U32((uint)(component?.Id ?? -1)).U32((uint)entities.Length);
        foreach (var entity in entities)
            U32(entity);
        var output = Call(ModAbi.ToolHas);
        for (var i = 0; i < entities.Length; i++)
            into[i] = output[i] != 0;
    }

    /// <summary>Сколько сущностей с компонентом.</summary>
    public static int Count(ComponentRef component)
    {
        Begin().U32((uint)component.Id);
        return (int)BinaryPrimitives.ReadUInt32LittleEndian(Call(ModAbi.ToolCount));
    }

    /// <summary>Номер вида «раздел/имя» (запись data/раздел.json); нет — -1.</summary>
    public static int ProtoId(string name)
    {
        Begin().Byte(0).Utf8(name);
        return BinaryPrimitives.ReadInt32LittleEndian(Call(ModAbi.ToolProto));
    }

    /// <summary>«Раздел/имя» вида по номеру; нет — null.</summary>
    public static string? ProtoName(int id)
    {
        Begin().Byte(1).U32((uint)id);
        var output = Call(ModAbi.ToolProto);
        return output.IsEmpty ? null : Encoding.UTF8.GetString(output);
    }

    /// <summary>Строка мира по номеру (как лежит в столбце поля-строки).</summary>
    public static string Text(uint id)
    {
        Begin().U32(id);
        return Encoding.UTF8.GetString(Call(ModAbi.ToolText));
    }

    /// <summary>Номер строки мира; такой строки нет — null (значит, ни одно поле её не держит).</summary>
    public static uint? StringId(string text)
    {
        Begin().Utf8(text);
        var id = BinaryPrimitives.ReadInt32LittleEndian(Call(ModAbi.ToolStringId));
        return id < 0 ? null : (uint)id;
    }

    /// <summary>Память мода: байты, которые он сохранил (<see cref="WriteStorage"/>); null — хост памяти не дал.</summary>
    public static byte[]? ReadStorage(out uint entity)
    {
        Begin();
        entity = 0;
        var code = Raw(ModAbi.ToolStorage, out var output);
        if (code < 0 || output.Length < 4)
            return null;
        entity = BinaryPrimitives.ReadUInt32LittleEndian(output);
        return output[4..].ToArray();
    }

    /// <summary>Пишет память мода командой (применится после вызова); false — хост памяти не дал.</summary>
    public static bool WriteStorage(CommandWriter commands, ReadOnlySpan<byte> data)
    {
        var storage = Runtime.ModRuntime.Schema?.Find(ModAbi.StorageComponent);
        if (storage is null || ReadStorage(out var entity) is null)
            return false;
        commands.Set(entity, storage).Bytes(storage["data"], data);
        return true;
    }

    private static bool HasOne(int component, uint entity)
    {
        Begin().U32((uint)component).U32(1).U32(entity);
        return Call(ModAbi.ToolHas)[0] != 0;
    }

    /// <summary>Список номеров во вход: count:u32 entity:u32×count.</summary>
    private static Writer Entities(ReadOnlySpan<uint> entities)
    {
        U32((uint)entities.Length);
        MemoryMarshal.AsBytes(entities).CopyTo(Room(entities.Length * 4));
        return default;
    }

    /// <summary>Вызов с проверкой кода: ошибка — исключение.</summary>
    private static ReadOnlySpan<byte> Call(int tool)
    {
        var code = Raw(tool, out var output);
        return code >= 0 ? output : throw new ToolException(tool, code);
    }

    /// <summary>kh.tool с входом из буфера; мало места под ответ — растит и зовёт снова.</summary>
    private static int Raw(int tool, out ReadOnlySpan<byte> output)
    {
        while (true)
        {
            var code = Host.Tool(tool, _input.AsSpan(0, _length), _output);
            if (code < 0)
            {
                output = default;
                return code;
            }
            if (code <= _output.Length)
            {
                output = _output.AsSpan(0, code);
                return code;
            }
            _output = new byte[Math.Max(code, _output.Length * 2)];
        }
    }

    private static Writer Begin()
    {
        _length = 0;
        return default;
    }

    private static Span<byte> Room(int count)
    {
        if (_length + count > _input.Length)
            Array.Resize(ref _input, Math.Max(_length + count, _input.Length * 2));
        var room = _input.AsSpan(_length, count);
        _length += count;
        return room;
    }

    private static Writer U32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Room(4), value);
        return default;
    }

    private static Writer I32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(Room(4), value);
        return default;
    }

    private static Writer F64(double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(Room(8), value);
        return default;
    }

    private static Writer Doubles(ReadOnlySpan<double> values)
    {
        MemoryMarshal.AsBytes(values).CopyTo(Room(values.Length * 8));
        return default;
    }

    /// <summary>Цепочка записей во вход инструмента.</summary>
    private readonly struct Writer
    {
        public Writer Byte(byte value)
        {
            Room(1)[0] = value;
            return this;
        }

        public Writer U32(uint value) => Tools.U32(value);
        public Writer I32(int value) => Tools.I32(value);
        public Writer F64(double value) => Tools.F64(value);
        public Writer Doubles(ReadOnlySpan<double> values) => Tools.Doubles(values);

        public Writer Utf8(string text)
        {
            Encoding.UTF8.GetBytes(text, Room(Encoding.UTF8.GetByteCount(text)));
            return this;
        }
    }
}
