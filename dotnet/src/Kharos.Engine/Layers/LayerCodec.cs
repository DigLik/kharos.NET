using System.Buffers.Binary;
using System.Text;

namespace Kharos.Engine.Layers;

/// <summary>
/// Изменения слоёв одним буфером — так их пишет и сохранение, и сеть (формат layers.rs):
/// <code>
/// count:u32, на изменение: имя слоя (len:u8, UTF-8), владелец:i32 (-1 — мир), x:i32, y:i32 блока, len:u32, байты
/// </code>
/// Всё little-endian.
/// </summary>
public static class LayerCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Encode(IReadOnlyList<LayerChange> changes)
    {
        var size = 4;
        foreach (var change in changes)
            size += 1 + Encoding.UTF8.GetByteCount(change.Layer) + 16 + change.Bytes.Length;
        var bytes = new byte[size];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)changes.Count);
        var at = 4;
        foreach (var change in changes)
        {
            var name = Encoding.UTF8.GetBytes(change.Layer);
            if (name.Length > byte.MaxValue)
                throw new ArgumentException($"имя слоя длиннее {byte.MaxValue} байт: {change.Layer}");
            span[at++] = (byte)name.Length;
            name.CopyTo(span[at..]);
            at += name.Length;
            BinaryPrimitives.WriteInt32LittleEndian(span[at..], (int)change.Owner.Code);
            BinaryPrimitives.WriteInt32LittleEndian(span[(at + 4)..], change.X);
            BinaryPrimitives.WriteInt32LittleEndian(span[(at + 8)..], change.Y);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 12)..], (uint)change.Bytes.Length);
            at += 16;
            change.Bytes.CopyTo(span[at..]);
            at += change.Bytes.Length;
        }
        return bytes;
    }

    /// <summary>Разбор буфера изменений; обрыв или мусор — <see cref="FormatException"/>.</summary>
    public static List<LayerChange> Decode(ReadOnlySpan<byte> bytes)
    {
        var at = 0;
        ReadOnlySpan<byte> Take(ReadOnlySpan<byte> source, int count)
        {
            if (count < 0 || count > source.Length - at)
                throw new FormatException("изменения обрываются");
            var part = source.Slice(at, count);
            at += count;
            return part;
        }
        var total = BinaryPrimitives.ReadUInt32LittleEndian(Take(bytes, 4));
        // Изменение — не меньше 17 байт: мусорный счёт не заставит выделить лишнего.
        var changes = new List<LayerChange>((int)Math.Min(total, (uint)(bytes.Length / 17)));
        for (var index = 0u; index < total; index++)
        {
            var length = Take(bytes, 1)[0];
            string layer;
            try
            {
                layer = Utf8.GetString(Take(bytes, length));
            }
            catch (DecoderFallbackException)
            {
                throw new FormatException("имя слоя не UTF-8");
            }
            var head = Take(bytes, 16);
            var owner = LayerOwner.FromCode(BinaryPrimitives.ReadInt32LittleEndian(head));
            var x = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
            var y = BinaryPrimitives.ReadInt32LittleEndian(head[8..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
            var block = Take(bytes, size > int.MaxValue ? -1 : (int)size);
            changes.Add(new LayerChange(layer, owner, x, y, block.ToArray()));
        }
        return changes;
    }
}
