using System.Reflection;

namespace Kharos.Protocol;

/// <summary>
/// Версия сборки (Н7-4): номер из <c>KharosVersion</c> в Directory.Build.props, короткий хеш коммита и репозиторий релизов.
/// Читается из атрибутов сборки <see cref="AssemblyMetadataAttribute"/>, которые кладёт сборка; работает и в NativeAOT.
/// </summary>
public static class BuildInfo
{
    /// <summary>Репозиторий, из которого выходят релизы, если сборка не сказала иного.</summary>
    public const string DefaultRepo = "DigLik/kharos.NET";

    private static readonly Dictionary<string, string> Meta = Read();

    /// <summary>Версия сборки, SemVer: <c>0.2.0</c> или <c>0.2.0-dev</c>.</summary>
    public static string Version { get; } = Meta.GetValueOrDefault("KharosVersion") is { Length: > 0 } version ? version : "0.2.0-dev";

    /// <summary>Короткий хеш коммита; пусто, если сборка шла без git.</summary>
    public static string Commit { get; } = Meta.GetValueOrDefault("KharosCommit") ?? "";

    /// <summary>Репозиторий релизов, <c>владелец/имя</c>.</summary>
    public static string Repo { get; } = Meta.GetValueOrDefault("KharosRepo") is { Length: > 0 } repo ? repo : DefaultRepo;

    /// <summary>Страница последнего релиза: куда ведёт «скачать новую версию».</summary>
    public static string ReleasesUrl { get; } = $"https://github.com/{Repo}/releases/latest";

    /// <summary>Версия с коммитом: <c>0.2.0-dev+4f29383</c>.</summary>
    public static string Full => Commit.Length > 0 ? $"{Version}+{Commit}" : Version;

    private static Dictionary<string, string> Read()
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            if (attribute.Value is { } value)
                meta[attribute.Key] = value;
        return meta;
    }
}
