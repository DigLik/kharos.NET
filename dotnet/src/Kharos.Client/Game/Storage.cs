namespace Kharos.Client.Game;

/// <summary>
/// Хранилище клиента между партиями (storage.ts): ключ — байты. Настройки модов (инструмент и запись Prefs, ABI 2) лежат
/// под ключом «prefs/&lt;мод&gt;/&lt;ключ&gt;». TODO(Д4): файловое хранилище оболочки (слоты, настройки игрока) — по этому же
/// договору; до него — <see cref="MemoryStorage"/>.
/// </summary>
public interface IStorage
{
    /// <summary>Байты ключа; нет — null.</summary>
    byte[]? Get(string key);

    /// <summary>Запомнить байты ключа; пустые — стереть.</summary>
    void Set(string key, ReadOnlySpan<byte> value);
}

/// <summary>Хранилище в памяти: живёт, пока жив процесс (тесты, клиент без оболочки).</summary>
public sealed class MemoryStorage : IStorage
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

    public byte[]? Get(string key) => _values.GetValueOrDefault(key);

    public void Set(string key, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            _values.Remove(key);
        else
            _values[key] = value.ToArray();
    }

    /// <summary>Сколько ключей.</summary>
    public int Count => _values.Count;
}
