using System.Runtime.InteropServices;

namespace Kharos.Mod.Sdk.Runtime;

/// <summary>
/// Растущий буфер байтов вне кучи GC: его адрес отдаётся хосту (выход вызова, текст ошибки), а сборщик мусора
/// объекты двигать может. Память не освобождается: экземпляр мода живёт недолго, буферы — до его конца.
/// </summary>
internal unsafe struct NativeBuffer
{
    private byte* _data;
    private int _capacity;

    /// <summary>Записано байт.</summary>
    public int Length;

    public readonly byte* Pointer => _data;

    public readonly ReadOnlySpan<byte> Written => new(_data, Length);

    public void Clear() => Length = 0;

    /// <summary>Место под count байт в конце; Length не двигает.</summary>
    public Span<byte> Room(int count)
    {
        var need = Length + count;
        if (need > _capacity)
        {
            var capacity = Math.Max(need, Math.Max(256, _capacity * 2));
            _data = (byte*)NativeMemory.Realloc(_data, (nuint)capacity);
            _capacity = capacity;
        }
        return new Span<byte>(_data + Length, count);
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(Room(bytes.Length));
        Length += bytes.Length;
    }
}
