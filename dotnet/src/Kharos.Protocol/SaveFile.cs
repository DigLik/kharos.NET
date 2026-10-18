using System.Buffers.Binary;
using System.Text;

namespace Kharos.Protocol;

/// <summary>Сохранение другой версии: хост его не читает (сервер откладывает файл в сторону и начинает заново).</summary>
public sealed class SaveVersionException(int found, int expected)
    : FormatException($"сохранение версии {found}, а этот хост читает версию {expected}")
{
    public int Found { get; } = found;
    public int Expected { get; } = expected;
}

/// <summary>
/// Файл сохранения хоста — разделы в сжатом теле (как save.ts):
/// <code>
/// "KHSV" version:u16 body — deflate-raw: (tag:4 ASCII length:u32 data)×
/// </code>
/// Незнакомые разделы чтение пропускает. Другая версия — <see cref="SaveVersionException"/>, мусор —
/// <see cref="FormatException"/>. Числа — little-endian.
/// </summary>
public static class SaveFile
{
    /// <summary>Версия формата сохранения хоста: растёт, когда меняется любой раздел.</summary>
    public const int Version = 1;

    /// <summary>Предел распакованного тела.</summary>
    public const int MaxBody = 1 << 30;

    private static ReadOnlySpan<byte> Magic => "KHSV"u8;

    /// <summary>Файл из разделов по порядку.</summary>
    public static byte[] Pack(IEnumerable<(string Tag, byte[] Data)> sections, int version = Version)
    {
        var body = new MemoryStream();
        Span<byte> head = stackalloc byte[8];
        foreach (var (tag, data) in sections)
        {
            if (tag.Length != 4 || Encoding.ASCII.GetByteCount(tag) != 4 || tag.Any(c => c > 127))
                throw new ArgumentException($"тег раздела — 4 знака ASCII: {tag}");
            Encoding.ASCII.GetBytes(tag, head);
            BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)data.Length);
            body.Write(head);
            body.Write(data);
        }
        var packed = ContentPack.Deflate(body.GetBuffer().AsSpan(0, (int)body.Length));
        var file = new byte[6 + packed.Length];
        Magic.CopyTo(file);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), (ushort)version);
        packed.CopyTo(file, 6);
        return file;
    }

    /// <summary>Версия файла, не распаковывая; не сохранение — <see cref="FormatException"/>.</summary>
    public static int VersionOf(ReadOnlySpan<byte> file)
    {
        if (file.Length < 6 || !file[..4].SequenceEqual(Magic))
            throw new FormatException("не сохранение хоста");
        return BinaryPrimitives.ReadUInt16LittleEndian(file[4..]);
    }

    /// <summary>Разделы файла по тегам.</summary>
    public static Dictionary<string, byte[]> Unpack(ReadOnlySpan<byte> file)
    {
        var version = VersionOf(file);
        if (version != Version)
            throw new SaveVersionException(version, Version);
        var body = ContentPack.Inflate(file[6..], MaxBody);
        var sections = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var at = 0; at < body.Length;)
        {
            if (at + 8 > body.Length)
                throw new FormatException("сохранение обрывается");
            var tag = Encoding.ASCII.GetString(body, at, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + 4));
            if (length > (uint)(body.Length - at - 8))
                throw new FormatException($"раздел {tag} обрывается");
            sections[tag] = body.AsSpan(at + 8, (int)length).ToArray();
            at += 8 + (int)length;
        }
        return sections;
    }

    /// <summary>
    /// Пишет файл целиком или не пишет вовсе: сначала во временный рядом, на диск, затем заменяет прежний. Оборванная
    /// запись не портит прежнее сохранение.
    /// </summary>
    public static void WriteAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        var full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
        var temp = full + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, full, overwrite: true);
    }
}
