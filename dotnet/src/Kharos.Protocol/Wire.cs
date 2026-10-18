using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Kharos.Protocol;

/// <summary>Запись сообщения: little-endian, varint — LEB128 без знака, svarint — zigzag, str и blob — varint длина и байты.</summary>
public sealed class WireWriter
{
    private byte[] _bytes;

    public WireWriter(int capacity = 256) => _bytes = new byte[Math.Max(16, capacity)];

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Written => _bytes.AsSpan(0, Length);

    public byte[] ToArray() => Written.ToArray();

    public void Clear() => Length = 0;

    public WireWriter Byte(byte value)
    {
        Room(1)[0] = value;
        return this;
    }

    public WireWriter Bool(bool value) => Byte(value ? (byte)1 : (byte)0);

    public WireWriter I32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(Room(4), value);
        return this;
    }

    public WireWriter F64(double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(Room(8), value);
        return this;
    }

    public WireWriter Varint(ulong value)
    {
        var room = Room(10);
        var at = 0;
        while (value >= 0x80)
        {
            room[at++] = (byte)(value | 0x80);
            value >>= 7;
        }
        room[at++] = (byte)value;
        Length -= 10 - at;
        return this;
    }

    public WireWriter SVarint(long value) => Varint((ulong)((value << 1) ^ (value >> 63)));

    public WireWriter Text(string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        Varint((ulong)count);
        Encoding.UTF8.GetBytes(value, Room(count));
        return this;
    }

    public WireWriter Blob(ReadOnlySpan<byte> value)
    {
        Varint((ulong)value.Length);
        return Raw(value);
    }

    public WireWriter Raw(ReadOnlySpan<byte> value)
    {
        value.CopyTo(Room(value.Length));
        return this;
    }

    public WireWriter Doubles(ReadOnlySpan<double> values)
    {
        var room = Room(values.Length * 8);
        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(values).CopyTo(room);
        else
            for (var at = 0; at < values.Length; at++)
                BinaryPrimitives.WriteDoubleLittleEndian(room[(at * 8)..], values[at]);
        return this;
    }

    private Span<byte> Room(int count)
    {
        if (Length + count > _bytes.Length)
            Array.Resize(ref _bytes, Math.Max(Length + count, _bytes.Length * 2));
        var room = _bytes.AsSpan(Length, count);
        Length += count;
        return room;
    }
}

/// <summary>Чтение того же формата; обрыв и мусор — <see cref="FormatException"/>.</summary>
public ref struct WireReader(ReadOnlySpan<byte> bytes)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly ReadOnlySpan<byte> _bytes = bytes;

    public int At { get; private set; }
    public readonly bool End => At >= _bytes.Length;
    public readonly int Left => _bytes.Length - At;

    public byte Byte() => Take(1)[0];

    public bool Bool() => Byte() switch
    {
        0 => false,
        1 => true,
        var other => throw new FormatException($"флаг {other}"),
    };

    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    public ulong Varint()
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var part = Byte();
            value |= (ulong)(part & 0x7f) << shift;
            if (part < 0x80)
                return value;
        }
        throw new FormatException("varint длиннее u64");
    }

    public uint U32() => Varint() is var value && value <= uint.MaxValue ? (uint)value : throw new FormatException("число не умещается в u32");

    public int Int() => Varint() is var value && value <= int.MaxValue ? (int)value : throw new FormatException("число не умещается в i32");

    public long SVarint()
    {
        var value = Varint();
        return (long)(value >> 1) ^ -(long)(value & 1);
    }

    /// <summary>Длина списка: не больше, чем осталось байт (на элемент — хотя бы min байт).</summary>
    public int Count(int min = 1)
    {
        var count = Varint();
        return count <= (ulong)(Left / Math.Max(min, 1)) ? (int)count : throw new FormatException($"длина {count} больше данных");
    }

    public string Text()
    {
        try
        {
            return Utf8.GetString(Blob());
        }
        catch (DecoderFallbackException)
        {
            throw new FormatException("строка не UTF-8");
        }
    }

    public ReadOnlySpan<byte> Blob() => Take(Count());

    public void Doubles(Span<double> into)
    {
        var bytes = Take(into.Length * 8);
        for (var at = 0; at < into.Length; at++)
            into[at] = BinaryPrimitives.ReadDoubleLittleEndian(bytes[(at * 8)..]);
    }

    public ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Left)
            throw new FormatException("сообщение обрывается");
        var part = _bytes.Slice(At, count);
        At += count;
        return part;
    }
}
