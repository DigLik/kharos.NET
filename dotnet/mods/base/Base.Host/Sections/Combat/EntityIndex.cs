namespace Base.Host.Sections.Combat;

/// <summary>
/// Номер сущности → число (строка, индекс) на один тик: массив по номеру с отметкой поколения, без словаря. Begin — новый
/// тик (прежнее забыто без очистки).
/// </summary>
internal sealed class EntityIndex
{
    private int[] _stamp = new int[1024];
    private int[] _value = new int[1024];
    private int _generation;

    public void Begin()
    {
        if (++_generation == int.MaxValue)
        {
            Array.Clear(_stamp);
            _generation = 1;
        }
    }

    public void Set(uint entity, int value)
    {
        if (entity >= (uint)_stamp.Length)
        {
            var size = _stamp.Length;
            while (size <= entity)
                size *= 2;
            Array.Resize(ref _stamp, size);
            Array.Resize(ref _value, size);
        }
        _stamp[entity] = _generation;
        _value[entity] = value;
    }

    /// <summary>Значение; нет — −1.</summary>
    public int Get(uint entity) => entity < (uint)_stamp.Length && _stamp[entity] == _generation ? _value[entity] : -1;

    public bool Has(uint entity) => entity < (uint)_stamp.Length && _stamp[entity] == _generation;
}
