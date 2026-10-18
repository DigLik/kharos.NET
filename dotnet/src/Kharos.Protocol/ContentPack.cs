using System.IO.Compression;
using System.Security.Cryptography;

namespace Kharos.Protocol;

/// <summary>
/// Пакет мода для клиента (Р2): манифест, данные и client.wasm — без кода хоста. Хеш — SHA-256 байт пакета: по нему
/// клиент держит кэш и просит недостающее. По сети пакет идёт сжатым (deflate-raw) кусками <see cref="ContentChunk"/>.
/// <code>
/// "KHMP" version:u8 name:str version:str manifest:blob data:varint (name:str bytes:blob)× client:u8 [wasm:blob]
/// </code>
/// Данные — по порядку имён (как в ModPackage), так что пакет и хеш у одного мода одни на всех машинах.
/// </summary>
public sealed record ContentPack(string Name, string Version, byte[] Manifest, IReadOnlyList<(string Name, byte[] Bytes)> Data, byte[]? ClientWasm)
{
    private const byte Format = 1;

    /// <summary>Предел распакованного пакета: защита от сжатой бомбы.</summary>
    public const int MaxBytes = 256 << 20;

    private static ReadOnlySpan<byte> Magic => "KHMP"u8;

    /// <summary>Байты пакета.</summary>
    public byte[] Encode()
    {
        var w = new WireWriter(1024 + Manifest.Length + (ClientWasm?.Length ?? 0) + Data.Sum(file => file.Bytes.Length + file.Name.Length + 8));
        w.Raw(Magic).Byte(Format).Text(Name).Text(Version).Blob(Manifest).Varint((ulong)Data.Count);
        foreach (var (name, bytes) in Data.OrderBy(file => file.Name, StringComparer.Ordinal))
            w.Text(name).Blob(bytes);
        w.Bool(ClientWasm is not null);
        if (ClientWasm is { } wasm)
            w.Blob(wasm);
        return w.ToArray();
    }

    /// <summary>Пакет из байт; не пакет — <see cref="FormatException"/>.</summary>
    public static ContentPack Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new WireReader(bytes);
        if (!r.Take(4).SequenceEqual(Magic))
            throw new FormatException("не пакет мода");
        if (r.Byte() is var format && format != Format)
            throw new FormatException($"пакет мода версии {format}, а нужна {Format}");
        var name = r.Text();
        var version = r.Text();
        var manifest = r.Blob().ToArray();
        var data = new (string, byte[])[r.Count(2)];
        for (var at = 0; at < data.Length; at++)
            data[at] = (r.Text(), r.Blob().ToArray());
        var wasm = r.Bool() ? r.Blob().ToArray() : null;
        if (!r.End)
            throw new FormatException("лишние байты в пакете мода");
        return new ContentPack(name, version, manifest, data, wasm);
    }

    public static byte[] Hash(ReadOnlySpan<byte> pack) => SHA256.HashData(pack);

    /// <summary>Сжатие deflate-raw.</summary>
    public static byte[] Deflate(ReadOnlySpan<byte> bytes, CompressionLevel level = CompressionLevel.Optimal)
    {
        var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, level, leaveOpen: true))
            deflate.Write(bytes);
        return output.ToArray();
    }

    /// <summary>Распаковка deflate-raw не больше most байт; больше или мусор — <see cref="FormatException"/>.</summary>
    public static byte[] Inflate(ReadOnlySpan<byte> packed, int most = MaxBytes)
    {
        try
        {
            using var input = new DeflateStream(new MemoryStream(packed.ToArray()), CompressionMode.Decompress);
            var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                if (output.Length + read > most)
                    throw new FormatException("распакованное больше предела");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (InvalidDataException error)
        {
            throw new FormatException($"сжатые данные не читаются: {error.Message}", error);
        }
    }
}
