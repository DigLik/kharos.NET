using System.Buffers.Binary;
using System.Text;

namespace Kharos.Client.Game;

/// <summary>Запись потоков клиентского ABI (Abi/ClientAbi.cs): little-endian, буфер растёт и переиспользуется.</summary>
public sealed class AbiWriter
{
    private byte[] _bytes = new byte[4096];

    public int Length { get; private set; }
    public ReadOnlySpan<byte> Written => _bytes.AsSpan(0, Length);

    public void Clear() => Length = 0;

    public AbiWriter U8(byte value)
    {
        Room(1)[0] = value;
        return this;
    }

    public AbiWriter U16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Room(2), value);
        return this;
    }

    public AbiWriter U32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Room(4), value);
        return this;
    }

    public AbiWriter U64(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(Room(8), value);
        return this;
    }

    public AbiWriter F32(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Room(4), value);
        return this;
    }

    public AbiWriter F64(double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(Room(8), value);
        return this;
    }

    /// <summary>Строка: u16 длина байт и UTF-8 (длиннее 65535 байт — обрезается).</summary>
    public AbiWriter Str(string value)
    {
        var count = Math.Min(Encoding.UTF8.GetByteCount(value), ushort.MaxValue);
        U16((ushort)count);
        var room = Room(count);
        if (count == value.Length || Encoding.UTF8.GetByteCount(value) == count)
            Encoding.UTF8.GetBytes(value, room);
        else
            Encoding.UTF8.GetBytes(value)[..count].CopyTo(room);
        return this;
    }

    public AbiWriter Blob(ReadOnlySpan<byte> bytes)
    {
        U32((uint)bytes.Length);
        bytes.CopyTo(Room(bytes.Length));
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

/// <summary>Чтение потоков мода: обрыв или неверное значение — <see cref="FormatException"/>.</summary>
public ref struct AbiReader(ReadOnlySpan<byte> bytes)
{
    private readonly ReadOnlySpan<byte> _bytes = bytes;

    /// <summary>Сколько прочитано.</summary>
    public int Position { get; private set; }

    public readonly bool End => Position == _bytes.Length;

    public byte U8() => Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
    public string Str() => Encoding.UTF8.GetString(Take(U16()));
    public ReadOnlySpan<byte> Blob() => Take(checked((int)U32()));

    public ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > _bytes.Length - Position)
            throw new FormatException($"поток мода обрывается @{Position}");
        var part = _bytes.Slice(Position, count);
        Position += count;
        return part;
    }
}
