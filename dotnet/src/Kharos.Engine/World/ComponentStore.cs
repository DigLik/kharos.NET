namespace Kharos.Engine.World;

/// <summary>
/// Хранилище одного компонента: плотный список сущностей и столбцы полей той же длины (SoA). Снятие — обменом с
/// последней строкой, поэтому порядок строк зависит только от порядка добавлений и снятий — одинаков у всех, кто
/// применил те же команды. Рядом со строкой — маска изменённых полей (для дельт сети).
/// </summary>
public sealed class ComponentStore
{
    private readonly Sparse _sparse = new();
    private readonly double[] _defaults;
    private uint[] _entities = [];
    private readonly double[][] _columns;
    private readonly byte[][][] _bytes;
    private ulong[] _changed = [];
    private readonly List<uint> _dirty = [];
    private int _count;

    internal ComponentStore(ComponentDef def, double[] defaults)
    {
        Def = def;
        _defaults = defaults;
        _columns = new double[def.Columns][];
        for (var slot = 0; slot < _columns.Length; slot++)
            _columns[slot] = [];
        _bytes = new byte[def.BytesFields][][];
        for (var slot = 0; slot < _bytes.Length; slot++)
            _bytes[slot] = [];
    }

    public ComponentDef Def { get; }
    public int Count => _count;

    /// <summary>
    /// Номер правки хранилища: растёт при любой правке — строка добавлена или снята, записано поле. Ключ кэша движка,
    /// производного от этого компонента (стоящие юниты для поиска пути).
    /// </summary>
    public ulong Version { get; internal set; }

    /// <summary>Сущности в порядке строк.</summary>
    public ReadOnlySpan<uint> Entities => _entities.AsSpan(0, _count);

    /// <summary>Строка сущности; нет компонента — -1.</summary>
    public int RowOf(uint entity) => _sparse.Get(entity);

    public bool Contains(uint entity) => _sparse.Get(entity) >= 0;

    /// <summary>Столбец f64 поля (по <see cref="FieldDef.Slot"/>) в порядке строк. Писать — через мир: он ведёт изменения.</summary>
    public ReadOnlySpan<double> Column(int slot) => _columns[slot].AsSpan(0, _count);

    /// <summary>Значение байтового поля (по <see cref="FieldDef.Slot"/>) в строке.</summary>
    public ReadOnlySpan<byte> BytesAt(int slot, int row) => _bytes[slot][row];

    internal Span<double> MutableColumn(int slot)
    {
        Version++;
        return _columns[slot].AsSpan(0, _count);
    }

    internal double[] RawColumn(int slot) => _columns[slot];

    internal byte[] RawBytes(int slot, int row) => _bytes[slot][row];
    internal void SetBytes(int slot, int row, byte[] value)
    {
        Version++;
        _bytes[slot][row] = value;
    }

    /// <summary>Новая строка со значениями по умолчанию.</summary>
    internal int AddRow(uint entity)
    {
        Version++;
        var row = _count;
        if (row == _entities.Length)
            Grow(Math.Max(16, row * 2));
        _entities[row] = entity;
        for (var slot = 0; slot < _columns.Length; slot++)
            _columns[slot][row] = _defaults[slot];
        for (var slot = 0; slot < _bytes.Length; slot++)
            _bytes[slot][row] = [];
        _changed[row] = 0;
        _sparse.Set(entity, row);
        _count++;
        return row;
    }

    /// <summary>Снимает строку обменом с последней. false — компонента не было.</summary>
    internal bool RemoveRow(uint entity)
    {
        var row = _sparse.Get(entity);
        if (row < 0)
            return false;
        Version++;
        _sparse.Clear(entity);
        var last = --_count;
        if (row != last)
        {
            var moved = _entities[last];
            _entities[row] = moved;
            foreach (var column in _columns)
                column[row] = column[last];
            foreach (var column in _bytes)
                column[row] = column[last];
            _changed[row] = _changed[last];
            _sparse.Set(moved, row);
        }
        foreach (var column in _bytes)
            column[last] = null!;
        return true;
    }

    /// <summary>Отмечает поле строки изменённым.</summary>
    internal void Mark(int row, int field) => MarkMask(row, 1UL << field);

    internal void MarkMask(int row, ulong mask)
    {
        ref var changed = ref _changed[row];
        if (changed == 0)
            _dirty.Add(_entities[row]);
        changed |= mask;
    }

    /// <summary>Изменённые поля по сущностям с прошлого раза; маски сбрасываются.</summary>
    internal void TakeChanged(int component, List<FieldChange> into)
    {
        foreach (var entity in _dirty)
        {
            var row = _sparse.Get(entity);
            if (row < 0 || _changed[row] == 0)
                continue;
            into.Add(new FieldChange(entity, component, _changed[row]));
            _changed[row] = 0;
        }
        _dirty.Clear();
    }

    internal void Reserve(int capacity)
    {
        if (capacity > _entities.Length)
            Grow(capacity);
    }

    private void Grow(int capacity)
    {
        Array.Resize(ref _entities, capacity);
        Array.Resize(ref _changed, capacity);
        for (var slot = 0; slot < _columns.Length; slot++)
            Array.Resize(ref _columns[slot], capacity);
        for (var slot = 0; slot < _bytes.Length; slot++)
            Array.Resize(ref _bytes[slot], capacity);
    }
}
