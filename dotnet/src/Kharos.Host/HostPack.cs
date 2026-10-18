using System.Text;
using Kharos.Content;
using Kharos.Protocol;

namespace Kharos.Host;

/// <summary>
/// Пакет мода для клиентов (Р2): манифест, данные и client.wasm, без host.wasm. По сети — сжатым (<see cref="Packed"/>),
/// кусками до 64 КБ; ссылка на него — в приветствии.
/// </summary>
public sealed class HostPack
{
    private HostPack(ModRef reference, byte[] packed)
    {
        Ref = reference;
        Packed = packed;
    }

    /// <summary>Имя, версия, хеш пакета и сжатый размер.</summary>
    public ModRef Ref { get; }

    /// <summary>Пакет, сжатый deflate-raw.</summary>
    public byte[] Packed { get; }

    public static HostPack Of(ModPackage package)
    {
        var pack = new ContentPack(package.Name, package.Version, Encoding.UTF8.GetBytes(package.Manifest.GetRawText()),
            [.. package.Data.Select(pair => (pair.Key, pair.Value))], package.ClientWasm);
        var bytes = pack.Encode();
        var packed = ContentPack.Deflate(bytes);
        return new HostPack(new ModRef(package.Name, package.Version, ContentPack.Hash(bytes), packed.Length), packed);
    }
}
