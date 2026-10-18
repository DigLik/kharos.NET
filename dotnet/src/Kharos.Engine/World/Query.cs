using System.Runtime.InteropServices;

namespace Kharos.Engine.World;

/// <summary>Условие запроса: столбец компонента равен значению (флаг — 0/1, строка — номер).</summary>
public readonly record struct QueryFilter(int Component, int Slot, double Value);

/// <summary>
/// Запрос: сущности со всеми компонентами списка, с полями, равными условиям, и без компонентов <see cref="Without"/>.
/// Порядок — строки самого короткого хранилища: одинаков у всех, кто применил те же команды.
/// </summary>
public sealed class Query
{
    private readonly List<uint> _scratch = [];

    /// <param name="world">Мир: строки условий переводятся в номера его строк.</param>
    /// <param name="components">Компоненты, чьи столбцы уходят системе.</param>
    /// <param name="where">Условия: компонент, поле, значение.</param>
    /// <param name="without">Компоненты, которых у сущности быть не должно.</param>
    public Query(GameWorld world, IEnumerable<string> components, IEnumerable<(string Component, string Field, FieldValue Value)>? where = null, IEnumerable<string>? without = null)
    {
        var schema = world.Schema;
        Components = [.. components.Select(schema.Id)];
        if (Components.Length == 0)
            throw new ArgumentException("запрос без компонентов");
        if (Components.Distinct().Count() != Components.Length)
            throw new ArgumentException("компонент в запросе дважды");
        var filters = new List<QueryFilter>();
        foreach (var (component, field, value) in where ?? [])
        {
            var id = schema.Id(component);
            var def = schema[id].Field(field);
            if (def is null || !def.Columnar)
                throw new ArgumentException($"where — нет поля-столбца {component}.{field}");
            if (value.Kind != def.Kind)
                throw new ArgumentException($"where.{component}.{field}: {value} — не {Schema.Word(def.Kind)}");
            filters.Add(new QueryFilter(id, def.Slot, world.Encode(value)));
        }
        Filters = [.. filters];
        Without = [.. (without ?? []).Select(schema.Id)];
        if (Without.Any(Components.Contains))
            throw new ArgumentException("компонент и в запросе, и в without");
        Offsets = new int[Components.Length];
        var width = 0;
        for (var at = 0; at < Components.Length; at++)
        {
            Offsets[at] = width;
            width += schema[Components[at]].Columns;
        }
        Width = width;
        World = world;
    }

    public GameWorld World { get; }
    public int[] Components { get; }
    public QueryFilter[] Filters { get; }

    /// <summary>Компоненты, которых нет у сущностей запроса.</summary>
    public int[] Without { get; }

    /// <summary>Столбцов f64 у всех компонентов запроса вместе.</summary>
    public int Width { get; }

    /// <summary>Номер первого столбца компонента запроса (по месту в <see cref="Components"/>).</summary>
    public int[] Offsets { get; }

    /// <summary>Место компонента в запросе; нет — -1.</summary>
    public int IndexOf(int component) => Array.IndexOf(Components, component);

    /// <summary>Сущности запроса по порядку.</summary>
    public void Entities(List<uint> into)
    {
        var start = into.Count;
        World.Query(Components, into);
        if (Filters.Length == 0 && Without.Length == 0)
            return;
        var kept = start;
        for (var at = start; at < into.Count; at++)
        {
            var entity = into[at];
            var pass = true;
            foreach (var filter in Filters)
            {
                var store = World.Store(filter.Component);
                if (store.RawColumn(filter.Slot)[store.RowOf(entity)] != filter.Value)
                {
                    pass = false;
                    break;
                }
            }
            foreach (var component in Without)
                pass &= !World.Has(entity, component);
            if (pass)
                into[kept++] = entity;
        }
        into.RemoveRange(kept, into.Count - kept);
    }

    /// <summary>Собирает сущности и все их столбцы в columns.</summary>
    public void Gather(QueryColumns columns)
    {
        _scratch.Clear();
        Entities(_scratch);
        columns.Fill(this, CollectionsMarshal.AsSpan(_scratch));
    }

    /// <summary>
    /// Возвращает в мир столбцы компонентов write (каждый должен быть в запросе). Изменённые значения отмечаются для
    /// дельт; одинаковые по битам — нет. Между сборкой и возвратом состав мира меняться не должен.
    /// </summary>
    public void Scatter(QueryColumns columns, ReadOnlySpan<int> write)
    {
        if (columns.Query != this)
            throw new ArgumentException("столбцы другого запроса");
        if (columns.Revision != World.Revision)
            throw new InvalidOperationException("состав мира изменился между сборкой и возвратом столбцов");
        var n = columns.Count;
        foreach (var component in write)
        {
            var at = IndexOf(component);
            if (at < 0)
                throw new ArgumentException($"{World.Schema[component].Name} нет в запросе");
            var store = World.Store(component);
            var rows = columns.Rows(at);
            foreach (var field in store.Def.Fields)
            {
                if (!field.Columnar)
                    continue;
                var source = columns.Column(at, field.Slot);
                var target = store.RawColumn(field.Slot);
                if (columns.Identity(at))
                {
                    var current = target.AsSpan(0, n);
                    if (MemoryMarshal.Cast<double, long>(current).SequenceEqual(MemoryMarshal.Cast<double, long>(source)))
                        continue;
                    var bits = 1UL << field.Index;
                    for (var row = 0; row < n; row++)
                        if (BitConverter.DoubleToInt64Bits(current[row]) != BitConverter.DoubleToInt64Bits(source[row]))
                        {
                            current[row] = source[row];
                            World.Touched(store, row, bits);
                        }
                }
                else
                {
                    var bits = 1UL << field.Index;
                    for (var i = 0; i < n; i++)
                    {
                        ref var cell = ref target[rows[i]];
                        if (BitConverter.DoubleToInt64Bits(cell) != BitConverter.DoubleToInt64Bits(source[i]))
                        {
                            cell = source[i];
                            World.Touched(store, rows[i], bits);
                        }
                    }
                }
            }
        }
    }
}

/// <summary>
/// Столбцы запроса одним блоком f64: компонент за компонентом (в порядке запроса), внутри — столбцы полей по
/// <see cref="FieldDef.Slot"/>, у каждого — <see cref="Count"/> значений подряд. Блок <see cref="Data"/> копируется в
/// память модуля wasm одним <c>CopyTo</c> и обратно; <see cref="Entities"/> — номера сущностей того же порядка.
/// </summary>
public sealed class QueryColumns
{
    private uint[] _entities = [];
    private double[] _data = [];
    private int[][] _rows = [];
    private bool[] _identity = [];
    private int _width;
    private int[] _rowOf = [];
    private int[] _rowStamp = [];
    private int _stamp;
    private bool _indexed;

    public Query? Query { get; private set; }
    public int Count { get; private set; }
    internal ulong Revision { get; private set; }

    public ReadOnlySpan<uint> Entities => _entities.AsSpan(0, Count);

    /// <summary>Все столбцы подряд: <see cref="Query.Width"/> × <see cref="Count"/>.</summary>
    public Span<double> Data => _data.AsSpan(0, _width * Count);

    /// <summary>Столбец поля компонента по месту в запросе и <see cref="FieldDef.Slot"/>.</summary>
    public Span<double> Column(int queryIndex, int slot) => _data.AsSpan((Query!.Offsets[queryIndex] + slot) * Count, Count);

    /// <summary>Столбец по номеру компонента и имени поля.</summary>
    public Span<double> Column(int component, string field)
    {
        var at = Query!.IndexOf(component);
        if (at < 0)
            throw new ArgumentException($"{Query.World.Schema[component].Name} нет в запросе");
        var def = Query.World.Schema[component].Field(field);
        return def is { Columnar: true } ? Column(at, def.Slot) : throw new ArgumentException($"нет поля-столбца {field}");
    }

    /// <summary>Строка сущности в блоке; нет — -1. Указатель строится при первом вызове после сборки (O(n)).</summary>
    public int RowOf(uint entity)
    {
        if (!_indexed)
            Index();
        return entity < (uint)_rowStamp.Length && _rowStamp[entity] == _stamp ? _rowOf[entity] : -1;
    }

    private void Index()
    {
        _indexed = true;
        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_rowStamp);
            _stamp = 1;
        }
        var top = 0u;
        foreach (var entity in Entities)
            top = Math.Max(top, entity);
        if (Count > 0 && top >= _rowStamp.Length)
        {
            var size = (int)Math.Min(int.MaxValue - 64, Math.Max(top + 1, (uint)_rowStamp.Length * 2));
            Array.Resize(ref _rowStamp, size);
            Array.Resize(ref _rowOf, size);
        }
        var entities = Entities;
        for (var row = 0; row < entities.Length; row++)
        {
            _rowStamp[entities[row]] = _stamp;
            _rowOf[entities[row]] = row;
        }
    }

    internal ReadOnlySpan<int> Rows(int queryIndex) => _rows[queryIndex].AsSpan(0, Count);

    /// <summary>Строки компонента — 0..n-1 по порядку: столбцы копируются целиком.</summary>
    internal bool Identity(int queryIndex) => _identity[queryIndex];

    internal void Fill(Query query, ReadOnlySpan<uint> entities)
    {
        var n = entities.Length;
        var components = query.Components;
        Query = query;
        Count = n;
        _indexed = false;
        _width = query.Width;
        Revision = query.World.Revision;
        if (_entities.Length < n)
            _entities = new uint[Math.Max(n, _entities.Length * 2)];
        entities.CopyTo(_entities);
        if (_data.Length < _width * n)
            _data = new double[Math.Max(_width * n, _data.Length * 2)];
        if (_rows.Length != components.Length)
        {
            _rows = new int[components.Length][];
            _identity = new bool[components.Length];
            for (var at = 0; at < _rows.Length; at++)
                _rows[at] = [];
        }
        for (var at = 0; at < components.Length; at++)
        {
            var store = query.World.Store(components[at]);
            if (_rows[at].Length < n)
                _rows[at] = new int[Math.Max(n, _rows[at].Length * 2)];
            var rows = _rows[at];
            var identity = store.Count == n;
            for (var i = 0; i < n; i++)
            {
                var row = store.RowOf(entities[i]);
                rows[i] = row;
                identity &= row == i;
            }
            _identity[at] = identity;
            for (var slot = 0; slot < store.Def.Columns; slot++)
            {
                var target = Column(at, slot);
                var source = store.RawColumn(slot);
                if (identity)
                    source.AsSpan(0, n).CopyTo(target);
                else
                    for (var i = 0; i < n; i++)
                        target[i] = source[rows[i]];
            }
        }
    }
}
