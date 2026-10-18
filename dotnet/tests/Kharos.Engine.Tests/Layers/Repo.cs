namespace Kharos.Engine.Tests.Layers;

/// <summary>Файлы репозитория для тестов: корень — вверх от папки сборки, до того, где лежит packages/base.</summary>
internal static class Repo
{
    private static readonly Lazy<string> Root = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "packages", "base", "data")))
                return dir.FullName;
        throw new DirectoryNotFoundException($"корень репозитория не найден выше {AppContext.BaseDirectory}");
    });

    public static string File(string relative) => Path.Combine(Root.Value, relative);
}
