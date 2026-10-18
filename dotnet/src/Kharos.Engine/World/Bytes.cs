using System.Buffers.Binary;
using System.Text;

namespace Kharos.Engine.World;

/// <summary>Запись двоичных форматов движка: little-endian, varint — LEB128 без знака.</summary>
internal sealed class ByteWriter
{
    private byte[] _bytes = new byte[1024];

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Written => _bytes.AsSpan(0, Length);

    public byte[] ToArray() => Written.ToArray();

    public void Byte(byte value) => Room(1)[0] = value;

    public void U32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Room(4), value);

    public void Double(double value) => BinaryPrimitives.WriteDoubleLittleEndian(Room(8), value);

    public void Varint(ulong value)
    {
        while (value >= 0x80)
        {
            Byte((byte)(value | 0x80));
            value >>= 7;
        }
        Byte((byte)value);
    }

    public void Blob(ReadOnlySpan<byte> value)
    {
        Varint((ulong)value.Length);
        value.CopyTo(Room(value.Length));
    }

    public void Text(string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        Varint((ulong)count);
        Encoding.UTF8.GetBytes(value, Room(count));
    }

    public void Doubles(ReadOnlySpan<double> values)
    {
        var room = Room(values.Length * 8);
        if (BitConverter.IsLittleEndian)
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(values).CopyTo(room);
        else
            for (var at = 0; at < values.Length; at++)
                BinaryPrimitives.WriteDoubleLittleEndian(room[(at * 8)..], values[at]);
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
internal ref struct ByteReader(ReadOnlySpan<byte> bytes)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly ReadOnlySpan<byte> _bytes = bytes;

    public int At { get; private set; }
    public readonly bool End => At >= _bytes.Length;

    public byte Byte() => Take(1)[0];

    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public double Double() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

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

    public uint Varint32()
    {
        var value = Varint();
        return value <= uint.MaxValue ? (uint)value : throw new FormatException($"{value} не умещается в u32");
    }

    /// <summary>Длина: не больше, чем осталось байт (на элемент — хотя бы min байт).</summary>
    public int Count(int min = 1)
    {
        var count = Varint();
        return count <= (ulong)((_bytes.Length - At) / Math.Max(min, 1)) ? (int)count : throw new FormatException($"длина {count} больше данных");
    }

    public ReadOnlySpan<byte> Blob() => Take(Count());

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

    public void Doubles(Span<double> into)
    {
        var bytes = Take(into.Length * 8);
        for (var at = 0; at < into.Length; at++)
            into[at] = BinaryPrimitives.ReadDoubleLittleEndian(bytes[(at * 8)..]);
    }

    public ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > _bytes.Length - At)
            throw new FormatException("данные обрываются");
        var part = _bytes.Slice(At, count);
        At += count;
        return part;
    }
}
