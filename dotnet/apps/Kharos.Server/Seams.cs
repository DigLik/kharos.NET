using Kharos.Protocol;

namespace Kharos.Server;

/// <summary>
/// Файл сохранения мира: читает, откладывает негодное в сторону и пишет атомарно. Формат (KHSV) и загрузка — у хоста
/// (<c>Host.Save</c>, <c>Host.Load</c>); здесь только судьба файла.
/// </summary>
public static class SaveFiles
{
    /// <summary>Содержимое файла; нет файла — null.</summary>
    public static byte[]? Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    /// <summary>Откладывает файл в <c>&lt;имя&gt;.v&lt;версия&gt;</c>: сохранение другой версии игры. Молча перезаписать его значило бы потерять мир.</summary>
    public static string AsideVersion(string path, int version, Action<string> log)
    {
        var aside = $"{path}.v{version}";
        File.Move(path, aside, overwrite: true);
        log($"сохранение версии {version}, а игра версии {SaveFile.Version}: отложено в {aside}, мир начинается заново");
        return aside;
    }

    /// <summary>Откладывает нечитаемый файл в <c>&lt;имя&gt;.&lt;время&gt;.old</c>.</summary>
    public static string AsideBroken(string path, string reason, Action<string> log)
    {
        var aside = $"{path}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.old";
        for (var n = 2; File.Exists(aside); n++)
            aside = $"{path}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{n}.old";
        File.Move(path, aside);
        log($"сохранение не читается ({reason}), отложено в {aside}, мир начинается заново");
        return aside;
    }

    /// <summary>Атомарная запись (временный файл, диск, подмена).</summary>
    public static void Write(string path, byte[] bytes) => SaveFile.WriteAtomic(path, bytes);
}
