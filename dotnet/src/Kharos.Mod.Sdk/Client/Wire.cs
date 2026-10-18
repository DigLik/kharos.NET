using System.Buffers.Binary;
using System.Text;
using Kharos.Mod.Sdk.Runtime;

namespace Kharos.Mod.Sdk.Client;

/// <summary>Запись потока клиентского ABI (Abi/ClientAbi.cs): little-endian, вне кучи GC — адрес отдаётся хосту.</summary>
internal sealed unsafe class ByteWriter : IDisposable
{
    private NativeBuffer _buffer;

    /// <summary>Освобождает буфер (разовые записи вне кадра).</summary>
    public void Dispose() => _buffer.Free();

    public int Length => _buffer.Length;
    public ReadOnlySpan<byte> Written => _buffer.Written;
    public byte* Pointer => _buffer.Pointer;

    public void Clear() => _buffer.Clear();

    public ByteWriter U8(byte value)
    {
        _buffer.Room(1)[0] = value;
        _buffer.Length++;
        return this;
    }

    public ByteWriter U16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Room(2), value);
        _buffer.Length += 2;
        return this;
    }

    public ByteWriter U32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Room(4), value);
        _buffer.Length += 4;
        return this;
    }

    public ByteWriter I32(int value) => U32((uint)value);

    public ByteWriter F32(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.Room(4), value);
        _buffer.Length += 4;
        return this;
    }

    public ByteWriter F64(double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.Room(8), value);
        _buffer.Length += 8;
        return this;
    }

    /// <summary>Строка: u16 длина байт и UTF-8; длиннее 65535 байт — обрезается по символам.</summary>
    public ByteWriter Str(string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        if (count > ushort.MaxValue)
        {
            var chars = value.Length;
            while (count > ushort.MaxValue)
            {
                chars = chars * 3 / 4;
                count = Encoding.UTF8.GetByteCount(value.AsSpan(0, chars));
            }
            value = value[..chars];
        }
        U16((ushort)count);
        Encoding.UTF8.GetBytes(value, _buffer.Room(count));
        _buffer.Length += count;
        return this;
    }

    /// <summary>Байты как есть.</summary>
    public ByteWriter Raw(ReadOnlySpan<byte> bytes)
    {
        _buffer.Write(bytes);
        return this;
    }

    /// <summary>u32 длина и байты.</summary>
    public ByteWriter Blob(ReadOnlySpan<byte> bytes) => U32((uint)bytes.Length).Raw(bytes);
}

/// <summary>Чтение входа кадра; обрыв — <see cref="FormatException"/>.</summary>
internal ref struct ByteReader(ReadOnlySpan<byte> bytes)
{
    private readonly ReadOnlySpan<byte> _bytes = bytes;
    private int _at;

    public readonly bool End => _at == _bytes.Length;

    public byte U8() => Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
    public string Str() => Encoding.UTF8.GetString(Take(U16()));
    public ReadOnlySpan<byte> Blob() => Take((int)U32());

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > _bytes.Length - _at)
            throw new FormatException("вход кадра обрывается");
        var part = _bytes.Slice(_at, count);
        _at += count;
        return part;
    }
}
