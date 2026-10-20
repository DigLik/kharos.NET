using System.Text;
using Kharos.Client.Game;

namespace Kharos.Client.Shell;

/// <summary>
/// Хранилище клиента в файлах (localStorage и IndexedDB у TS): ключ — путь в папке, каждый кусок ключа между «/» — имя
/// папки или файла. Знаки, которым не место в имени файла, пишутся %XX (UTF-8), так что ключ восстанавливается по
/// пути без потерь. Запись — через временный файл: оборванная не портит прежнюю. Пустое значение стирает файл.
/// Вызовы — из одного потока.
/// </summary>
public sealed class FileStorage(string root) : IStorage
{
    /// <summary>Хвост временного файла записи: такие не ключи.</summary>
    private const string Temp = ".tmp~";

    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>
    /// Папка профиля игрока: KHAROS_HOME, если задана (смоук и ручные прогоны не трогают настоящий профиль, Н7.3), иначе
    /// %LOCALAPPDATA%\Kharos, ~/.local/share/Kharos, ~/Library/Application Support/Kharos. Читается при каждом обращении.
    /// </summary>
    public static string ProfileDirectory =>
        Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home
            ? Path.GetFullPath(home)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kharos");

    /// <summary>Переменная окружения, что заменяет папку профиля.</summary>
    public const string HomeVariable = "KHAROS_HOME";

    public byte[]? Get(string key)
    {
        var path = PathOf(key);
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Set(string key, ReadOnlySpan<byte> value)
    {
        var path = PathOf(key);
        try
        {
            if (value.IsEmpty)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + Temp;
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(value);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Как у localStorage: недоступное хранилище просто не запоминает.
        }
    }

    public IEnumerable<string> Keys
    {
        get
        {
            if (!Directory.Exists(Root))
                return [];
            var keys = new List<string>();
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(Temp, StringComparison.Ordinal))
                    continue;
                var relative = Path.GetRelativePath(Root, file).Replace(Path.DirectorySeparatorChar, '/');
                keys.Add(string.Join('/', relative.Split('/').Select(Unescape)));
            }
            return keys;
        }
    }

    /// <summary>Путь файла ключа.</summary>
    public string PathOf(string key)
    {
        if (key.Length == 0)
            throw new ArgumentException("пустой ключ", nameof(key));
        return Path.Combine([Root, .. key.Split('/').Select(Escape)]);
    }

    /// <summary>Кусок ключа — в имя файла: буквы, цифры, «-», «_» и «.» как есть (кроме «.» и «..» целиком), прочее — %XX.</summary>
    internal static string Escape(string part)
    {
        if (part.Length == 0)
            return "%";
        if (part is "." or "..")
            return part.Replace(".", "%2E");
        var text = new StringBuilder(part.Length);
        Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in part.EnumerateRunes())
        {
            if (rune.IsAscii && (char.IsAsciiLetterOrDigit((char)rune.Value) || rune.Value is '-' or '_' or '.'))
            {
                text.Append((char)rune.Value);
                continue;
            }
            var count = rune.EncodeToUtf8(bytes);
            foreach (var b in bytes[..count])
                text.Append('%').Append(b.ToString("X2"));
        }
        return text.ToString();
    }

    internal static string Unescape(string name)
    {
        if (name == "%")
            return "";
        if (!name.Contains('%'))
            return name;
        var bytes = new List<byte>(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '%' && i + 2 < name.Length && byte.TryParse(name.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                bytes.Add(b);
                i += 2;
            }
            else
                bytes.AddRange(Encoding.UTF8.GetBytes(name[i].ToString()));
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }
}
