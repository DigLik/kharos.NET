using System.Buffers.Binary;
using System.Text;

namespace Kharos.Audio;

/// <summary>Кодирование отсчётов WAV.</summary>
public enum WavFormat
{
    /// <summary>16 бит, целые со знаком.</summary>
    Pcm16 = 1,

    /// <summary>32 бита, float (IEEE).</summary>
    Float32 = 3,
}

/// <summary>Звук, прочитанный из WAV: interleaved float.</summary>
public sealed record WavData(float[] Interleaved, int Channels, int SampleRate, WavFormat Format)
{
    public int Frames => Interleaved.Length / Channels;
}

/// <summary>
/// Запись WAV по частям: заголовок пишется сразу, размеры дописываются при закрытии (поток должен уметь перемотку).
/// Для записи роликов: звук идёт кусками по мере рендера.
/// </summary>
public sealed class WavWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly int _channels;
    private readonly WavFormat _format;
    private readonly long _start;
    private long _bytes;
    private bool _closed;

    public WavWriter(Stream stream, WavFormat format = WavFormat.Pcm16, int channels = AudioFormat.Channels, int sampleRate = AudioFormat.SampleRate, bool leaveOpen = false)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("для WAV нужен поток с перемоткой", nameof(stream));
        _stream = stream;
        _leaveOpen = leaveOpen;
        _channels = channels;
        _format = format;
        _start = stream.Position;
        var bits = format == WavFormat.Pcm16 ? 16 : 32;
        Span<byte> h = stackalloc byte[44];
        h.Clear();
        "RIFF"u8.CopyTo(h);
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(h[20..], (short)format);
        BinaryPrimitives.WriteInt16LittleEndian(h[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], sampleRate * channels * bits / 8);
        BinaryPrimitives.WriteInt16LittleEndian(h[32..], (short)(channels * bits / 8));
        BinaryPrimitives.WriteInt16LittleEndian(h[34..], (short)bits);
        "data"u8.CopyTo(h[36..]);
        stream.Write(h);
    }

    /// <summary>Дописать interleaved-отсчёты (кратно числу каналов).</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        if (_closed)
            throw new ObjectDisposedException(nameof(WavWriter));
        if (interleaved.Length % _channels != 0)
            throw new ArgumentException("отсчётов не кратно числу каналов", nameof(interleaved));
        var step = _format == WavFormat.Pcm16 ? 2 : 4;
        var buffer = new byte[Math.Min(interleaved.Length, 16384) * step];
        for (var at = 0; at < interleaved.Length;)
        {
            var count = Math.Min(interleaved.Length - at, buffer.Length / step);
            for (var i = 0; i < count; i++)
            {
                var v = interleaved[at + i];
                if (_format == WavFormat.Pcm16)
                    BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2), ToPcm16(v));
                else
                    BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(i * 4), float.IsFinite(v) ? v : 0f);
            }
            _stream.Write(buffer, 0, count * step);
            _bytes += count * step;
            at += count;
        }
    }

    public void Write(RenderedAudio audio) => Write(audio.Interleaved);

    /// <summary>Дописать размеры и закрыть.</summary>
    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        if (_bytes > uint.MaxValue - 36)
            throw new InvalidOperationException("WAV больше 4 ГиБ");
        var end = _stream.Position;
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(36 + _bytes));
        _stream.Position = _start + 4;
        _stream.Write(size);
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)_bytes);
        _stream.Position = _start + 40;
        _stream.Write(size);
        _stream.Position = end;
        _stream.Flush();
        if (!_leaveOpen)
            _stream.Dispose();
    }

    private static short ToPcm16(float v)
    {
        if (!float.IsFinite(v))
            return 0;
        return (short)Math.Clamp(MathF.Round(v * 32767f), -32768f, 32767f);
    }
}

/// <summary>Чтение и запись WAV целиком.</summary>
public static class WavFile
{
    public static void Write(Stream stream, ReadOnlySpan<float> interleaved, WavFormat format = WavFormat.Pcm16, int channels = AudioFormat.Channels, int sampleRate = AudioFormat.SampleRate)
    {
        using var writer = new WavWriter(stream, format, channels, sampleRate, leaveOpen: true);
        writer.Write(interleaved);
    }

    public static void Write(string path, RenderedAudio audio, WavFormat format = WavFormat.Pcm16)
    {
        using var file = File.Create(path);
        Write(file, audio.Interleaved, format, AudioFormat.Channels, audio.SampleRate);
    }

    public static byte[] ToBytes(RenderedAudio audio, WavFormat format = WavFormat.Pcm16)
    {
        using var memory = new MemoryStream();
        Write(memory, audio.Interleaved, format, AudioFormat.Channels, audio.SampleRate);
        return memory.ToArray();
    }

    /// <summary>Разобрать WAV (PCM 16 или float 32).</summary>
    public static WavData Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes[8..12].SequenceEqual("WAVE"u8))
            throw new FormatException("не WAV");
        int channels = 0, rate = 0, bits = 0, tag = 0;
        var at = 12;
        while (at + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes.Slice(at, 4));
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]), (uint)(bytes.Length - at - 8));
            var body = bytes.Slice(at + 8, size);
            if (id == "fmt ")
            {
                if (size < 16)
                    throw new FormatException("короткий fmt");
                tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
            }
            else if (id == "data")
            {
                if (channels == 0)
                    throw new FormatException("data раньше fmt");
                var format = (tag, bits) switch
                {
                    (1, 16) => WavFormat.Pcm16,
                    (3, 32) => WavFormat.Float32,
                    _ => throw new FormatException($"формат {tag}/{bits} не поддержан"),
                };
                var step = bits / 8;
                var data = new float[size / step / channels * channels];
                for (var i = 0; i < data.Length; i++)
                    data[i] = format == WavFormat.Pcm16
                        ? BinaryPrimitives.ReadInt16LittleEndian(body[(i * 2)..]) / 32768f
                        : BinaryPrimitives.ReadSingleLittleEndian(body[(i * 4)..]);
                return new WavData(data, channels, rate, format);
            }
            at += 8 + size + (size & 1);
        }
        throw new FormatException("нет data");
    }
}
