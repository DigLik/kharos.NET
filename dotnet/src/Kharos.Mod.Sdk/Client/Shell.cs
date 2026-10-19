using System.Text;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk.Client;

/// <summary>Режим оболочки на этот кадр (вход кадра, ABI клиента 2).</summary>
[Flags]
public enum ShellMode : byte
{
    None = 0,
    /// <summary>Меню открыто поверх партии.</summary>
    Menu = ClientAbi.ShellMenu,
    /// <summary>Редактор карты.</summary>
    Editor = ClientAbi.ShellEditor,
    /// <summary>Идёт запись ролика.</summary>
    Recording = ClientAbi.ShellRecording,
}

/// <summary>Просьба мода к оболочке (запись Shell, ABI клиента 2).</summary>
public enum ShellAction : byte
{
    /// <summary>Открыть меню.</summary>
    Menu = ClientAbi.ShellActionMenu,
    /// <summary>Открыть чат с текстом.</summary>
    Chat = ClientAbi.ShellActionChat,
    /// <summary>Открыть настройки.</summary>
    Settings = ClientAbi.ShellActionSettings,
    /// <summary>Начать заново: оболочка просит хост (событие player_respawn), что делать — решают моды хоста.</summary>
    Respawn = ClientAbi.ShellActionRespawn,
}

/// <summary>Игрок партии (инструмент Roster): номер, ник, в сети ли, администратор ли.</summary>
public readonly record struct RosterEntry(uint Player, string Nick, bool Online, bool Admin);

/// <summary>
/// Настройки мода у игрока (пройденные шаги, громкости): хранит их движок (IStorage клиента) под ключом мода, между
/// партиями. Чтение — инструментом Prefs, запись — записью Prefs выхода кадра (сохраняется после кадра; в этом кадре
/// <see cref="Get"/> уже отдаёт новое).
/// </summary>
public sealed class Prefs
{
    private readonly ByteWriter _out;
    private readonly Dictionary<string, byte[]> _known = new(StringComparer.Ordinal);

    internal Prefs(ByteWriter output) => _out = output;

    /// <summary>Байты настройки key; нет — пусто.</summary>
    public byte[] Get(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!_known.TryGetValue(key, out var bytes))
            _known[key] = bytes = ClientTools.Prefs(key);
        return bytes;
    }

    /// <summary>Настройка key строкой UTF-8; нет — fallback.</summary>
    public string GetString(string key, string fallback = "")
    {
        var bytes = Get(key);
        return bytes.Length == 0 ? fallback : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Запомнить настройку key; пустые байты — стереть.</summary>
    public void Set(string key, ReadOnlySpan<byte> value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _known[key] = value.ToArray();
        _out.U8(ClientAbi.OpPrefs).Str(key).Blob(value);
    }

    /// <summary>Запомнить настройку key строкой UTF-8.</summary>
    public void Set(string key, string value) => Set(key, Encoding.UTF8.GetBytes(value));

    /// <summary>Стереть настройку key.</summary>
    public void Remove(string key) => Set(key, ReadOnlySpan<byte>.Empty);
}
