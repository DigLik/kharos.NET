using Kharos.Content;
using Kharos.Protocol;

namespace Kharos.Client.Net;

/// <summary>
/// Кэш пакетов модов, присланных серверами (docs/dotnet.md §4.4): файл на пакет — <c>&lt;sha256&gt;.khmp</c>, байты
/// <see cref="ContentPack"/> как есть. Испорченный файл (хеш не сошёлся) выбрасывается. Запись — через временный файл:
/// оборванная не оставит полупакета.
/// </summary>
public sealed class ContentCache(string directory)
{
    public string Directory { get; } = directory;

    public static string Key(ReadOnlySpan<byte> hash) => Convert.ToHexStringLower(hash);

    public string PathOf(ReadOnlySpan<byte> hash) => Path.Combine(Directory, Key(hash) + ".khmp");

    /// <summary>Пакет по хешу; нет или испорчен — null.</summary>
    public byte[]? Get(ReadOnlySpan<byte> hash)
    {
        var path = PathOf(hash);
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
                return null;
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        if (ContentPack.Hash(bytes).AsSpan().SequenceEqual(hash))
            return bytes;
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        return null;
    }

    /// <summary>Кладёт пакет; хеш не тот — <see cref="ArgumentException"/>.</summary>
    public void Put(ReadOnlySpan<byte> hash, byte[] pack)
    {
        if (!ContentPack.Hash(pack).AsSpan().SequenceEqual(hash))
            throw new ArgumentException("хеш пакета не сошёлся", nameof(pack));
        SaveFile.WriteAtomic(PathOf(hash), pack);
    }

    /// <summary>Мод клиента из пакета (без кода хоста).</summary>
    public static ModPackage ToPackage(ContentPack pack) =>
        new(pack.Name, pack.Manifest, pack.Data.ToDictionary(file => file.Name, file => file.Bytes, StringComparer.Ordinal), hostWasm: null, clientWasm: pack.ClientWasm);

    /// <summary>Пакет мода, каким его шлёт хост (хеш совпадёт с хешем в приветствии).</summary>
    public static ContentPack ToPack(ModPackage package) =>
        new(package.Name, package.Version, System.Text.Encoding.UTF8.GetBytes(package.Manifest.GetRawText()), [.. package.Data.Select(pair => (pair.Key, pair.Value))], package.ClientWasm);
}
