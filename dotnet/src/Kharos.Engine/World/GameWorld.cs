namespace Kharos.Engine.World;

/// <summary>
/// Мир столбцами (docs/dotnet.md, §4.2). Сущность — номер с 1, номера не повторяются. У каждого компонента —
/// <see cref="ComponentStore"/>: плотный список сущностей и столбцы полей. Системы получают столбцы целиком
/// (<see cref="QueryColumns"/>), а меняют мир командами (<see cref="CommandBuffer"/>).
/// </summary>
public sealed class GameWorld
{
    private readonly ComponentStore[] _stores;
    private readonly BitSet _alive = new();
    private readonly HashSet<uint> _reserved = [];
    private readonly List<uint> _spawned = [];
    private readonly List<uint> _destroyed = [];
    private readonly List<ComponentChange> _added = [];
    private readonly List<ComponentChange> _removed = [];
    private SpatialGrid? _grid;

    public GameWorld(Schema schema) : this(schema, []) { }

    /// <summary>Мир со строками заранее (загрузка): номера строк — как в списке, с 1.</summary>
    internal GameWorld(Schema schema, IReadOnlyList<string> strings)
    {
        for (var at = 0; at < strings.Count; at++)
            if (Strings.Id(strings[at]) != at + 1)
                throw new FormatException($"строка «{strings[at]}» дважды");
        schema.Seal();
        Schema = schema;
        _stores = new ComponentStore[schema.Count];
        for (var id = 0; id < schema.Count; id++)
        {
            var def = schema[id];
            var defaults = new double[def.Columns];
            foreach (var field in def.Fields)
                if (field.Columnar)
                    defaults[field.Slot] = Encode(field.Default);
            _stores[id] = new ComponentStore(def, defaults);
        }
    }

    public Schema Schema { get; }
    public Strings Strings { get; } = new();

    /// <summary>Номер, который получит следующая сущность.</summary>
    public uint NextEntity { get; private set; } = 1;

    /// <summary>Живых сущностей.</summary>
    public int Count { get; private set; }

    /// <summary>Номер правки состава: растёт, когда сущность появилась, пропала или у неё добавился или снят компонент.</summary>
    public ulong Revision { get; private set; }

    /// <summary>Номер правки мест: растёт при любом изменении <see cref="Builtin.Position"/>. По нему устаревает сетка <see cref="Near"/>.</summary>
    public ulong PositionRevision { get; private set; }

    /// <summary>Вести ли изменения для <see cref="TakeChanges"/>. Выключено — ничего не копится.</summary>
    public bool TrackChanges { get; set; }

    public ComponentStore Store(int component) => _stores[component];

    // Сущности.

    public uint Spawn()
    {
        var entity = Reserve();
        Revive(entity);
        return entity;
    }

    /// <summary>Следующий номер заранее, до того как сущность появится: команды раздают номера сразу.</summary>
    public uint Reserve()
    {
        if (NextEntity == uint.MaxValue)
            throw new InvalidOperationException("номера сущностей кончились");
        var entity = NextEntity++;
        _reserved.Add(entity);
        return entity;
    }

    /// <summary>Выдан заранее и ещё не ожил.</summary>
    public bool IsReserved(uint entity) => _reserved.Contains(entity);

    /// <summary>Оживляет заранее выданный номер. Другой номер — ошибка: номера не повторяются.</summary>
    public void Revive(uint entity)
    {
        if (!_reserved.Remove(entity))
            throw new InvalidOperationException($"номер {entity} не выдан заранее или уже жил");
        _alive.Add(entity);
        Count++;
        Revision++;
        if (TrackChanges)
            _spawned.Add(entity);
    }

    /// <summary>
    /// Оживляет номер хоста, жил он раньше или нет: реплика клиента, куда сущность вернулась в обзор. Живой — ничего;
    /// номера до него не выдаются заранее, <see cref="NextEntity"/> — за ним.
    /// </summary>
    public void Restore(uint entity)
    {
        if (entity is 0 or uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(entity), entity, "нет такого номера сущности");
        if (_alive.Contains(entity))
            return;
        _reserved.Remove(entity);
        if (entity >= NextEntity)
            NextEntity = entity + 1;
        _alive.Add(entity);
        Count++;
        Revision++;
        if (TrackChanges)
            _spawned.Add(entity);
    }

    public bool Alive(uint entity) => _alive.Contains(entity);

    /// <summary>Убирает сущность со всеми компонентами. Неживая — ничего.</summary>
    public void Destroy(uint entity)
    {
        if (!_alive.Remove(entity))
            return;
        for (var id = 0; id < _stores.Length; id++)
            if (_stores[id].RemoveRow(entity) && id == Builtin.Position)
                PositionRevision++;
        Count--;
        Revision++;
        if (TrackChanges)
            _destroyed.Add(entity);
    }

    /// <summary>Все живые по возрастанию номеров.</summary>
    public void Entities(List<uint> into) => _alive.CopyTo(into);

    // Компоненты.

    public bool Has(uint entity, int component) => _stores[component].Contains(entity);

    /// <summary>Добавляет компонент со значениями по умолчанию; уже есть — ничего. Возвращает строку.</summary>
    public int Add(uint entity, int component)
    {
        var store = _stores[component];
        var row = store.RowOf(entity);
        if (row >= 0)
            return row;
        if (!Alive(entity))
            throw new InvalidOperationException($"сущности {entity} нет");
        row = store.AddRow(entity);
        Revision++;
        if (component == Builtin.Position)
            PositionRevision++;
        if (TrackChanges)
            _added.Add(new ComponentChange(entity, component));
        return row;
    }

    /// <summary>Снимает компонент; не было — ничего.</summary>
    public void Remove(uint entity, int component)
    {
        if (!_stores[component].RemoveRow(entity))
            return;
        Revision++;
        if (component == Builtin.Position)
            PositionRevision++;
        if (TrackChanges)
            _removed.Add(new ComponentChange(entity, component));
    }

    // Поля.

    /// <summary>Число из столбца (флаг — 0 или 1, строка — номер); нет компонента — false.</summary>
    public bool TryNumber(uint entity, int component, int field, out double value)
    {
        var store = _stores[component];
        var def = store.Def.Fields[field];
        var row = store.RowOf(entity);
        if (row < 0 || !def.Columnar)
        {
            value = 0;
            return false;
        }
        value = store.RawColumn(def.Slot)[row];
        return true;
    }

    /// <summary>Число из столбца; нет компонента — исключение.</summary>
    public double Number(uint entity, int component, int field) =>
        TryNumber(entity, component, field, out var value) ? value : throw Missing(entity, component);

    /// <summary>Пишет число в столбец как есть (номер строки — тоже числом). Изменение отмечается, если значение другое.</summary>
    public void SetNumber(uint entity, int component, int field, double value)
    {
        var store = _stores[component];
        var row = store.RowOf(entity);
        if (row < 0)
            throw Missing(entity, component);
        var def = store.Def.Fields[field];
        if (!def.Columnar)
            throw new ArgumentException($"{store.Def.Name}.{def.Name}: байты, а не столбец");
        WriteCell(store, row, def, value);
    }

    /// <summary>Значение поля: строки — строками.</summary>
    public FieldValue Get(uint entity, int component, int field)
    {
        var store = _stores[component];
        var row = store.RowOf(entity);
        if (row < 0)
            throw Missing(entity, component);
        var def = store.Def.Fields[field];
        if (def.Kind == FieldKind.Bytes)
            return FieldValue.Of(store.RawBytes(def.Slot, row).ToArray());
        var number = store.RawColumn(def.Slot)[row];
        return def.Kind switch
        {
            FieldKind.Number => FieldValue.Of(number),
            FieldKind.Flag => FieldValue.Of(number != 0),
            _ => FieldValue.Of(Strings.Text((uint)number)),
        };
    }

    public FieldValue Get(uint entity, int component, string field) => Get(entity, component, Schema[component].FieldIndex(field));

    /// <summary>Пишет поле значением своего вида; не того вида — исключение.</summary>
    public void Set(uint entity, int component, int field, FieldValue value)
    {
        var store = _stores[component];
        var row = store.RowOf(entity);
        if (row < 0)
            throw Missing(entity, component);
        var def = store.Def.Fields[field];
        if (value.Kind != def.Kind)
            throw new ArgumentException($"{store.Def.Name}.{def.Name}: {value} — не {Schema.Word(def.Kind)}");
        if (def.Kind == FieldKind.Bytes)
            WriteBytes(store, row, def, value.Bytes ?? []);
        else
            WriteCell(store, row, def, Encode(value));
    }

    public void Set(uint entity, int component, string field, FieldValue value) => Set(entity, component, Schema[component].FieldIndex(field), value);

    public ReadOnlySpan<byte> GetBytes(uint entity, int component, int field)
    {
        var store = _stores[component];
        var row = store.RowOf(entity);
        return row < 0 ? throw Missing(entity, component) : store.BytesAt(store.Def.Fields[field].Slot, row);
    }

    public void SetBytes(uint entity, int component, int field, ReadOnlySpan<byte> value)
    {
        var store = _stores[component];
        var row = store.RowOf(entity);
        if (row < 0)
            throw Missing(entity, component);
        var def = store.Def.Fields[field];
        if (def.Kind != FieldKind.Bytes)
            throw new ArgumentException($"{store.Def.Name}.{def.Name}: не байты");
        WriteBytes(store, row, def, value.ToArray());
    }

    internal void WriteCell(ComponentStore store, int row, FieldDef def, double value)
    {
        ref var cell = ref store.RawColumn(def.Slot)[row];
        if (BitConverter.DoubleToInt64Bits(cell) == BitConverter.DoubleToInt64Bits(value))
            return;
        cell = value;
        store.Version++;
        if (TrackChanges)
            store.Mark(row, def.Index);
        if (store.Def.Id == Builtin.Position)
            PositionRevision++;
    }

    internal void WriteBytes(ComponentStore store, int row, FieldDef def, byte[] value)
    {
        if (store.RawBytes(def.Slot, row).AsSpan().SequenceEqual(value))
            return;
        store.SetBytes(def.Slot, row, value);
        if (TrackChanges)
            store.Mark(row, def.Index);
    }

    /// <summary>Отмечает изменения после записи столбцами в обход <see cref="WriteCell"/> (возврат столбцов системы).</summary>
    internal void Touched(ComponentStore store, int row, ulong mask)
    {
        store.Version++;
        if (TrackChanges)
            store.MarkMask(row, mask);
        if (store.Def.Id == Builtin.Position)
            PositionRevision++;
    }

    /// <summary>Значение не байтового поля в столбец f64: строка — номером (новая заводится).</summary>
    public double Encode(FieldValue value) => value.Kind switch
    {
        FieldKind.Number or FieldKind.Flag => value.Number,
        FieldKind.String => Strings.Id(value.Text ?? ""),
        _ => throw new ArgumentException("байты не кладутся в столбец"),
    };

    // Запросы.

    /// <summary>Сущности со всеми компонентами списка — в порядке строк самого короткого хранилища.</summary>
    public void Query(ReadOnlySpan<int> components, List<uint> into)
    {
        if (components.IsEmpty)
            return;
        var shortest = components[0];
        foreach (var id in components)
            if (_stores[id].Count < _stores[shortest].Count)
                shortest = id;
        foreach (var entity in _stores[shortest].Entities)
        {
            var all = true;
            foreach (var id in components)
                if (id != shortest && !_stores[id].Contains(entity))
                {
                    all = false;
                    break;
                }
            if (all)
                into.Add(entity);
        }
    }

    /// <summary>Сущности с <see cref="Builtin.Position"/> в круге (x, y, r) и со всеми components — по возрастанию номеров. Сетка 4×4 тайла строится лениво.</summary>
    public void Near(double x, double y, double radius, ReadOnlySpan<int> components, List<uint> into)
    {
        // Клеток в круге больше, чем сущностей в самом коротком хранилище запроса, — обход хранилища дешевле сетки
        // (Core.All в моде — круг на всю карту). Набор тот же: та же проверка круга, после — та же сортировка.
        var positions = _stores[Builtin.Position];
        var shortest = positions;
        foreach (var id in components)
            if (_stores[id].Count < shortest.Count)
                shortest = _stores[id];
        var cellsWide = Math.Floor((x + radius) / SpatialGrid.Cell) - Math.Floor((x - radius) / SpatialGrid.Cell) + 1;
        var cellsHigh = Math.Floor((y + radius) / SpatialGrid.Cell) - Math.Floor((y - radius) / SpatialGrid.Cell) + 1;
        if (cellsWide * cellsHigh >= shortest.Count)
        {
            var begin = into.Count;
            var xs = positions.Column(0);
            var ys = positions.Column(1);
            var reach = radius * radius;
            foreach (var entity in shortest.Entities)
            {
                var row = positions.RowOf(entity);
                if (row < 0)
                    continue;
                var (dx, dy) = (xs[row] - x, ys[row] - y);
                if (!(dx * dx + dy * dy <= reach))
                    continue;
                var all = true;
                foreach (var id in components)
                    all &= _stores[id].Contains(entity);
                if (all)
                    into.Add(entity);
            }
            into.Sort(begin, into.Count - begin, null);
            return;
        }
        if (_grid is null || _grid.Revision != PositionRevision)
            (_grid ??= new SpatialGrid()).Build(this);
        var start = into.Count;
        _grid.Find(x, y, radius, into);
        if (!components.IsEmpty)
        {
            var kept = start;
            for (var at = start; at < into.Count; at++)
            {
                var all = true;
                foreach (var id in components)
                    all &= _stores[id].Contains(into[at]);
                if (all)
                    into[kept++] = into[at];
            }
            into.RemoveRange(kept, into.Count - kept);
        }
        into.Sort(start, into.Count - start, null);
    }

    // Изменения.

    /// <summary>Изменения с прошлого раза (нужно <see cref="TrackChanges"/>); копилка очищается.</summary>
    public void TakeChanges(WorldChanges into)
    {
        into.Clear();
        into.Spawned.AddRange(_spawned);
        into.Destroyed.AddRange(_destroyed);
        into.Added.AddRange(_added);
        into.Removed.AddRange(_removed);
        _spawned.Clear();
        _destroyed.Clear();
        _added.Clear();
        _removed.Clear();
        for (var id = 0; id < _stores.Length; id++)
            _stores[id].TakeChanged(id, into.Fields);
    }

    // Загрузка (WorldSave).

    internal void LoadEntities(uint next, ReadOnlySpan<uint> alive)
    {
        NextEntity = next;
        foreach (var entity in alive)
        {
            if (entity == 0 || entity >= next || !_alive.Add(entity))
                throw new FormatException($"сохранение: сущность {entity} не к месту");
            Count++;
        }
    }

    private Exception Missing(uint entity, int component) => new InvalidOperationException($"у сущности {entity} нет {Schema[component].Name}");
}
