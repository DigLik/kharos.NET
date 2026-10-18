namespace Kharos.Engine.World;

/// <summary>Строки полей <see cref="FieldKind.String"/> по номерам. Номер 0 — пустая строка. Номера не меняются: столбцы держат их.</summary>
public sealed class Strings
{
    private readonly List<string> _list = [""];
    private readonly Dictionary<string, uint> _index = new() { [""] = 0 };

    public int Count => _list.Count;

    /// <summary>Номер строки; новой — заводит.</summary>
    public uint Id(string text)
    {
        if (_index.TryGetValue(text, out var id))
            return id;
        id = (uint)_list.Count;
        _list.Add(text);
        _index.Add(text, id);
        return id;
    }

    /// <summary>Номер строки, если она уже встречалась.</summary>
    public uint? Find(string text) => _index.TryGetValue(text, out var id) ? id : null;

    /// <summary>Строка по номеру; неизвестный номер — пустая строка.</summary>
    public string Text(uint id) => id < (uint)_list.Count ? _list[(int)id] : "";

    public bool Contains(uint id) => id < (uint)_list.Count;

    internal IReadOnlyList<string> All => _list;
}
