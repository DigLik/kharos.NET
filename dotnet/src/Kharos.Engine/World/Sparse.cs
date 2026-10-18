namespace Kharos.Engine.World;

/// <summary>
/// Номер сущности → строка хранилища: страницы по 1024 номера, пустая строка — -1. Быстрее словаря в разы, а память —
/// только на страницы, где сущности есть.
/// </summary>
internal sealed class Sparse
{
    private const int PageBits = 10;
    private const int PageSize = 1 << PageBits;
    private int[]?[] _pages = [];

    public int Get(uint entity)
    {
        var page = (int)(entity >> PageBits);
        var rows = (uint)page < (uint)_pages.Length ? _pages[page] : null;
        return rows is null ? -1 : rows[entity & (PageSize - 1)];
    }

    public void Set(uint entity, int row)
    {
        var page = (int)(entity >> PageBits);
        if (page >= _pages.Length)
            Array.Resize(ref _pages, Math.Max(page + 1, _pages.Length * 2));
        var rows = _pages[page];
        if (rows is null)
        {
            rows = new int[PageSize];
            rows.AsSpan().Fill(-1);
            _pages[page] = rows;
        }
        rows[entity & (PageSize - 1)] = row;
    }

    public void Clear(uint entity)
    {
        var page = (int)(entity >> PageBits);
        if ((uint)page < (uint)_pages.Length && _pages[page] is { } rows)
            rows[entity & (PageSize - 1)] = -1;
    }
}

/// <summary>Множество номеров битами — живые сущности: проверка за шаг, обход по возрастанию.</summary>
internal sealed class BitSet
{
    private ulong[] _words = [];

    public bool Contains(uint value)
    {
        var word = (int)(value >> 6);
        return (uint)word < (uint)_words.Length && (_words[word] & (1UL << (int)(value & 63))) != 0;
    }

    /// <summary>Добавляет; true — не было.</summary>
    public bool Add(uint value)
    {
        var word = (int)(value >> 6);
        if (word >= _words.Length)
            Array.Resize(ref _words, Math.Max(word + 1, _words.Length * 2));
        var bit = 1UL << (int)(value & 63);
        if ((_words[word] & bit) != 0)
            return false;
        _words[word] |= bit;
        return true;
    }

    /// <summary>Убирает; true — было.</summary>
    public bool Remove(uint value)
    {
        var word = (int)(value >> 6);
        if ((uint)word >= (uint)_words.Length)
            return false;
        var bit = 1UL << (int)(value & 63);
        if ((_words[word] & bit) == 0)
            return false;
        _words[word] &= ~bit;
        return true;
    }

    /// <summary>Все номера по возрастанию.</summary>
    public void CopyTo(List<uint> into)
    {
        for (var word = 0; word < _words.Length; word++)
        {
            var bits = _words[word];
            while (bits != 0)
            {
                var bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                into.Add((uint)(word * 64 + bit));
                bits &= bits - 1;
            }
        }
    }
}
