using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

/// <summary>
/// Система мода — чистая функция: столбцы запроса на вход, изменённые столбцы и команды на выход. Состояния между
/// вызовами у неё нет (экземпляр мода могут выбросить в любой момент): всё, что надо помнить, — в компонентах мира.
/// </summary>
public interface ISystem
{
    /// <summary>Раз при загрузке: взять столбцы по именам (<see cref="SystemBinding.Column"/>) и компоненты для команд.</summary>
    void Bind(SystemBinding binding);

    /// <summary>Тик: столбцы — в <paramref name="ctx"/>, команды — в <see cref="SystemContext.Commands"/>.</summary>
    void Run(SystemContext ctx);
}

/// <summary>Столбец запроса: место в блоке столбцов кадра. Берётся при загрузке, в тике — <see cref="SystemContext.Column"/>.</summary>
public readonly record struct Column(int Offset, FieldRef Field);

/// <summary>Привязка системы к схеме мира при загрузке.</summary>
public sealed class SystemBinding
{
    private readonly string[] _query;
    private readonly int[] _offsets;
    private readonly WorldSchema _schema;

    internal SystemBinding(string name, string[] query, WorldSchema schema)
    {
        Name = name;
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

    /// <summary>Имя системы.</summary>
    public string Name { get; }

    /// <summary>Столбцов f64 в кадре.</summary>
    public int Width { get; }

    /// <summary>Столбец поля компонента запроса.</summary>
    public Column Column(string component, string field)
    {
        var at = Array.IndexOf(_query, component);
        if (at < 0)
            throw new ArgumentException($"{Name}: {component} нет в запросе");
        var def = _schema.Get(component)[field];
        if (!def.Columnar)
            throw new ArgumentException($"{Name}: {component}.{field} — не столбец");
        return new Column(_offsets[at] + def.Slot, def);
    }

    /// <summary>Компонент схемы — для команд (Add, Set, Remove).</summary>
    public ComponentRef Component(string name) => _schema.Get(name);
}

/// <summary>Что видит система в тике: n сущностей, их столбцы и номера, тик, буфер команд.</summary>
public readonly unsafe ref struct SystemContext
{
    private readonly double* _data;
    private readonly uint* _entities;

    internal SystemContext(double* data, uint* entities, int count, ulong tick, double dt, CommandWriter commands)
    {
        _data = data;
        _entities = entities;
        Count = count;
        Tick = tick;
        Dt = dt;
        Commands = commands;
    }

    /// <summary>
    /// Контекст над готовым блоком столбцов (для запуска системы вне wasm: тесты, сверка). Блок и номера должны
    /// быть закреплены на время вызова.
    /// </summary>
    public static SystemContext Over(double* data, uint* entities, int count, ulong tick, double dt, CommandWriter commands) =>
        new(data, entities, count, tick, dt, commands);

    /// <summary>Сколько сущностей.</summary>
    public int Count { get; }

    public ulong Tick { get; }

    /// <summary>Длина тика, секунды.</summary>
    public double Dt { get; }

    /// <summary>Команды: применятся после системы, по порядку.</summary>
    public CommandWriter Commands { get; }

    /// <summary>Номера сущностей по порядку строк.</summary>
    public ReadOnlySpan<uint> Entities => new(_entities, Count);

    /// <summary>Столбец: читать и писать (в мир вернутся компоненты из Write).</summary>
    public Span<double> Column(Column column) => new(_data + (long)column.Offset * Count, Count);

    /// <summary>Случайное в [0, 1) из зерна мира — одинаковое у всех, кто считает тот же мир.</summary>
    public double Random() => Host.Random();
}

/// <summary>Объявление системы: запрос, условия, запись, порядок. Пишется цепочкой из <see cref="ModBuilder.System"/>.</summary>
public sealed class SystemDeclaration
{
    private readonly List<string> _query = [];
    private readonly List<string> _write = [];
    private readonly List<string> _after = [];
    private readonly List<string> _before = [];
    private readonly List<ModWhereInfo> _where = [];
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

    /// <summary>Условие: число в поле равно value.</summary>
    public SystemDeclaration Where(string component, string field, double value) =>
        AddWhere(new ModWhereInfo { Component = component, Field = field, Kind = ModAbi.KindNumber, Number = value });

    /// <summary>Условие: флаг равен value.</summary>
    public SystemDeclaration Where(string component, string field, bool value) =>
        AddWhere(new ModWhereInfo { Component = component, Field = field, Kind = ModAbi.KindFlag, Number = value ? 1 : 0 });

    /// <summary>Условие: строка равна value.</summary>
    public SystemDeclaration Where(string component, string field, string value) =>
        AddWhere(new ModWhereInfo { Component = component, Field = field, Kind = ModAbi.KindString, Text = value });

    /// <summary>Идти после этих систем (ключ или полное имя).</summary>
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

    internal ModSystemInfo ToInfo()
    {
        foreach (var component in _write)
            if (!_query.Contains(component))
                throw new InvalidOperationException($"{Name}: Write {component} — не из запроса");
        return new ModSystemInfo
        {
            Name = Name,
            Key = _key,
            Query = [.. _query],
            Where = [.. _where],
            Write = [.. _write],
            Every = _every,
            After = [.. _after],
            Before = [.. _before],
            Instead = _instead,
        };
    }
}
