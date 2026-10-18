using Kharos.Mod.Abi;
using Kharos.Mod.Sdk.Runtime;

namespace Kharos.Mod.Sdk;

/// <summary>
/// Система мода — чистая функция: столбцы запроса на вход, изменённые столбцы и команды на выход. Состояния между
/// вызовами у неё нет (экземпляр мода могут выбросить в любой момент): всё, что надо помнить, — в компонентах мира.
/// Кэш, производный от мира, можно держать с ключом <see cref="Tools.Revision"/>.
/// </summary>
public interface ISystem
{
    /// <summary>Раз при загрузке: взять столбцы по именам (<see cref="PartBinding.Column"/>) и компоненты для команд.</summary>
    void Bind(SystemBinding binding);

    /// <summary>Тик: столбцы — в <paramref name="ctx"/>, команды — в <see cref="SystemContext.Commands"/>.</summary>
    void Run(SystemContext ctx);
}

/// <summary>
/// Столбец кадра или побочной таблицы: место в блоке столбцов. Берётся при загрузке, в тике —
/// <see cref="SystemContext.Column"/> или <see cref="TableView.Column"/>.
/// </summary>
public readonly record struct Column(int Offset, FieldRef Field);

/// <summary>Раскладка части кадра (главный блок или таблица): компоненты запроса и их первые столбцы.</summary>
public class PartBinding
{
    private readonly string[] _query;
    private readonly int[] _offsets;
    private readonly WorldSchema _schema;
    private readonly string _owner;

    internal PartBinding(string owner, string[] query, WorldSchema schema)
    {
        _owner = owner;
        _query = query;
        _schema = schema;
        _offsets = new int[query.Length];
        var width = 0;
        for (var at = 0; at < query.Length; at++)
        {
            _offsets[at] = width;
            width += schema.Get(query[at]).Columns;
        }
        Width = width;
    }

    /// <summary>Столбцов f64.</summary>
    public int Width { get; }

    /// <summary>Компоненты запроса части.</summary>
    public IReadOnlyList<string> Query => _query;

    /// <summary>Столбец поля компонента запроса.</summary>
    public Column Column(string component, string field)
    {
        var at = Array.IndexOf(_query, component);
        if (at < 0)
            throw new ArgumentException($"{_owner}: {component} нет в запросе");
        var def = _schema.Get(component)[field];
        if (!def.Columnar)
            throw new ArgumentException($"{_owner}: {component}.{field} — не столбец");
        return new Column(_offsets[at] + def.Slot, def);
    }

    /// <summary>Первый столбец компонента в блоке; нет в запросе — -1.</summary>
    internal int OffsetOf(int component)
    {
        for (var at = 0; at < _query.Length; at++)
            if (_schema.Get(_query[at]).Id == component)
                return _offsets[at];
        return -1;
    }
}

/// <summary>Привязка системы к схеме мира при загрузке: столбцы кадра и побочных таблиц.</summary>
public sealed class SystemBinding : PartBinding
{
    private readonly WorldSchema _schema;
    private readonly PartBinding[] _tables;

    internal SystemBinding(string name, string[] query, IReadOnlyList<ModTableInfo> tables, WorldSchema schema) : base(name, query, schema)
    {
        Name = name;
        _schema = schema;
        _tables = [.. tables.Select((table, at) => new PartBinding($"{name}, таблица {at}", table.Query, schema))];
    }

    /// <summary>Имя системы.</summary>
    public string Name { get; }

    /// <summary>Побочная таблица i (по порядку объявления): её столбцы.</summary>
    public PartBinding Table(int i) => _tables[i];

    /// <summary>Сколько побочных таблиц.</summary>
    public int Tables => _tables.Length;

    /// <summary>Компонент схемы — для команд (Add, Set, Remove).</summary>
    public ComponentRef Component(string name) => _schema.Get(name);

    /// <summary>Слой мира по имени; нет — null.</summary>
    public LayerRef? Layer(string name) => _schema.Layer(name);
}

/// <summary>Что видит система в тике: n сущностей, их столбцы и номера, побочные таблицы, тик, буфер команд.</summary>
public readonly unsafe ref struct SystemContext
{
    private readonly FramePart _main;
    private readonly FramePart[] _tables;
    private readonly SystemBinding? _binding;

    internal SystemContext(FramePart main, FramePart[] tables, SystemBinding? binding, ulong tick, double dt, CommandWriter commands)
    {
        _main = main;
        _tables = tables;
        _binding = binding;
        Tick = tick;
        Dt = dt;
        Commands = commands;
    }

    /// <summary>
    /// Контекст над готовым блоком столбцов (для запуска системы вне мода: тесты, сверка). Блок и номера должны
    /// быть закреплены на время вызова. Побочных таблиц нет.
    /// </summary>
    public static SystemContext Over(double* data, uint* entities, int count, ulong tick, double dt, CommandWriter commands) =>
        new(FramePart.Over(0, data, entities, count), [], null, tick, dt, commands);

    /// <summary>Сколько сущностей.</summary>
    public int Count => _main.Count;

    public ulong Tick { get; }

    /// <summary>Длина тика, секунды.</summary>
    public double Dt { get; }

    /// <summary>Команды: применятся после системы, по порядку.</summary>
    public CommandWriter Commands { get; }

    /// <summary>Номера сущностей по порядку строк.</summary>
    public ReadOnlySpan<uint> Entities => _main.EntitySpan;

    /// <summary>Столбец: читать и писать (в мир вернутся компоненты из Write).</summary>
    public Span<double> Column(Column column) => _main.Column(column.Offset);

    /// <summary>Строка сущности в кадре; нет — -1.</summary>
    public int RowOf(uint entity) => _main.RowOf(entity);

    /// <summary>Побочная таблица i (<see cref="SystemDeclaration.Table(string[])"/>).</summary>
    public TableView Table(int i) => new(_tables[i]);

    /// <summary>Сколько побочных таблиц.</summary>
    public int Tables => _tables.Length;

    /// <summary>
    /// Сцена вызова (docs/dotnet-n5.md, Н5-4): чтение мира с записями этого вызова поверх — команды, кадр, новые
    /// сущности. Одна на вызов: повторный вызов вернёт ту же.
    /// </summary>
    public Stage Stage() => Commands.Stage ??= new Stage(Commands, _main, _binding);

    /// <summary>Случайное в [0, 1) из зерна мира — одинаковое у всех, кто считает тот же мир.</summary>
    public double Random() => Host.Random();
}

/// <summary>Побочная таблица в тике: сущности, столбцы (писать — если она объявлена WriteTable), строка по номеру.</summary>
public readonly ref struct TableView
{
    private readonly FramePart _part;

    internal TableView(FramePart part) => _part = part;

    public int Count => _part.Count;

    public ReadOnlySpan<uint> Entities => _part.EntitySpan;

    /// <summary>Столбец таблицы (<see cref="SystemBinding.Table"/>).</summary>
    public Span<double> Column(Column column) => _part.Column(column.Offset);

    /// <summary>Строка сущности в таблице; нет — -1.</summary>
    public int RowOf(uint entity) => _part.RowOf(entity);
}

/// <summary>Объявление системы: запрос, условия, таблицы, запись, порядок. Пишется цепочкой из <see cref="ModBuilder.System"/>.</summary>
public sealed class SystemDeclaration
{
    private readonly List<string> _query = [];
    private readonly List<string> _write = [];
    private readonly List<string> _without = [];
    private readonly List<string> _after = [];
    private readonly List<string> _before = [];
    private readonly List<ModWhereInfo> _where = [];
    private readonly List<ModTableInfo> _tables = [];
    private string? _key;
    private string? _instead;
    private int _every = 1;

    internal SystemDeclaration(string name, ISystem system)
    {
        Name = name;
        System = system;
    }

    public string Name { get; }
    internal ISystem System { get; }
    internal string[] QueryNames => [.. _query];
    internal IReadOnlyList<ModTableInfo> TableInfos => _tables;

    /// <summary>Сущности со всеми этими компонентами; их столбцы — в кадре, в этом порядке.</summary>
    public SystemDeclaration Query(params string[] components)
    {
        _query.AddRange(components);
        return this;
    }

    /// <summary>Чьи столбцы вернуть в мир — подмножество запроса.</summary>
    public SystemDeclaration Write(params string[] components)
    {
        _write.AddRange(components);
        return this;
    }

    /// <summary>Только сущности без этих компонентов.</summary>
    public SystemDeclaration Without(params string[] components)
    {
        _without.AddRange(components);
        return this;
    }

    /// <summary>
    /// Побочная таблица на чтение: все сущности с этими компонентами — своим блоком столбцов (номер — по порядку
    /// объявления; столбцы — <see cref="SystemBinding.Table"/>, в тике — <see cref="SystemContext.Table"/>).
    /// </summary>
    public SystemDeclaration Table(params string[] components) => AddTable(components, [], false);

    /// <summary>Побочная таблица на чтение без сущностей с компонентами without.</summary>
    public SystemDeclaration Table(string[] components, string[] without) => AddTable(components, without, false);

    /// <summary>
    /// Побочная таблица, чьи столбцы вернутся в мир (по битам: что не менялось, не отмечается). Компонент пишет одна
    /// часть системы: кадр (Write) или одна таблица — иначе ошибка при загрузке.
    /// </summary>
    public SystemDeclaration WriteTable(params string[] components) => AddTable(components, [], true);

    /// <summary>Пишущая побочная таблица без сущностей с компонентами without.</summary>
    public SystemDeclaration WriteTable(string[] components, string[] without) => AddTable(components, without, true);

    /// <summary>Условие: число в поле равно value.</summary>
    public SystemDeclaration Where(string component, string field, double value) =>
        AddWhere(new ModWhereInfo { Component = component, Field = field, Kind = ModAbi.KindNumber, Number = value });

    /// <summary>Условие: флаг равен value.</summary>
    public SystemDeclaration Where(string component, string field, bool value) =>
        AddWhere(new ModWhereInfo { Component = component, Field = field, Kind = ModAbi.KindFlag, Number = value ? 1 : 0 });

    /// <summary>Условие: строка равна value.</summary>
    public SystemDeclaration Where(string component, string field, string value) =>
        AddWhere(new ModWhereInfo { Component = component, Field = field, Kind = ModAbi.KindString, Text = value });

    /// <summary>Идти после этих систем (ключ или полное имя; системы движка — engine.*).</summary>
    public SystemDeclaration After(params string[] systems)
    {
        _after.AddRange(systems);
        return this;
    }

    /// <summary>Идти до этих систем.</summary>
    public SystemDeclaration Before(params string[] systems)
    {
        _before.AddRange(systems);
        return this;
    }

    /// <summary>Раз в столько тиков.</summary>
    public SystemDeclaration Every(int ticks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ticks, 1);
        _every = ticks;
        return this;
    }

    /// <summary>Короткое имя для чужих After/Before.</summary>
    public SystemDeclaration Key(string key)
    {
        _key = key;
        return this;
    }

    /// <summary>Встать на место системы с этим именем.</summary>
    public SystemDeclaration Instead(string system)
    {
        _instead = system;
        return this;
    }

    private SystemDeclaration AddWhere(ModWhereInfo where)
    {
        _where.Add(where);
        return this;
    }

    private SystemDeclaration AddTable(string[] components, string[] without, bool write)
    {
        if (components.Length == 0)
            throw new ArgumentException($"{Name}: таблица без компонентов");
        _tables.Add(new ModTableInfo { Query = [.. components], Without = [.. without], Write = write });
        return this;
    }

    internal ModSystemInfo ToInfo()
    {
        foreach (var component in _write)
            if (!_query.Contains(component))
                throw new InvalidOperationException($"{Name}: Write {component} — не из запроса");
        var writers = new HashSet<string>(_write, StringComparer.Ordinal);
        foreach (var table in _tables)
            if (table.Write)
                foreach (var component in table.Query)
                    if (!writers.Add(component))
                        throw new InvalidOperationException($"{Name}: {component} пишут две части системы");
        return new ModSystemInfo
        {
            Name = Name,
            Key = _key,
            Query = [.. _query],
            Where = [.. _where],
            Without = [.. _without],
            Tables = [.. _tables],
            Write = [.. _write],
            Every = _every,
            After = [.. _after],
            Before = [.. _before],
            Instead = _instead,
        };
    }
}
