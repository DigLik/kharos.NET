using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

/// <summary>Вид поля компонента (как FieldKind движка).</summary>
public enum FieldKind : byte
{
    /// <summary>Число f64.</summary>
    Number = ModAbi.KindNumber,
    /// <summary>Флаг: в столбце 0 или 1.</summary>
    Flag = ModAbi.KindFlag,
    /// <summary>Строка: в столбце — номер в строках мира.</summary>
    String = ModAbi.KindString,
    /// <summary>Байты: значение целиком, не столбец.</summary>
    Bytes = ModAbi.KindBytes,
}

/// <summary>Поле компонента схемы мира: номер для команд и номер столбца.</summary>
public readonly record struct FieldRef(string Name, FieldKind Kind, int Index, int Slot)
{
    /// <summary>Лежит ли поле в столбце f64.</summary>
    public bool Columnar => Kind != FieldKind.Bytes;
}

/// <summary>Компонент схемы мира: номер для команд и поля. Берётся по имени при загрузке (<see cref="LoadContext.Component"/>).</summary>
public sealed class ComponentRef
{
    private readonly FieldRef[] _fields;

    internal ComponentRef(ModComponentInfo info)
        : this(info.Name, info.Id, info.Fields.Select(f => new FieldRef(f.Name, (FieldKind)f.Kind, f.Index, f.Slot)))
    {
    }

    /// <summary>Компонент вручную (тесты на хосте); в моде — из схемы при загрузке.</summary>
    public ComponentRef(string name, int id, IEnumerable<FieldRef> fields)
    {
        Name = name;
        Id = id;
        _fields = [.. fields];
        Columns = _fields.Count(f => f.Columnar);
    }

    public string Name { get; }

    /// <summary>Номер компонента в схеме.</summary>
    public int Id { get; }

    /// <summary>Сколько у компонента столбцов f64.</summary>
    public int Columns { get; }

    public IReadOnlyList<FieldRef> Fields => _fields;

    /// <summary>Поле по имени; нет — исключение (опечатка не пройдёт молча).</summary>
    public FieldRef this[string field]
    {
        get
        {
            foreach (var f in _fields)
                if (f.Name == field)
                    return f;
            throw new KeyNotFoundException($"у {Name} нет поля {field}");
        }
    }
}

/// <summary>Схема мира, как её прислал хост.</summary>
internal sealed class WorldSchema
{
    private readonly Dictionary<string, ComponentRef> _byName = [];

    public WorldSchema(IEnumerable<ModComponentInfo> components)
    {
        foreach (var info in components)
            _byName[info.Name] = new ComponentRef(info);
    }

    public ComponentRef Get(string name) =>
        _byName.TryGetValue(name, out var component) ? component : throw new KeyNotFoundException($"нет компонента {name}");

    public ComponentRef? Find(string name) => _byName.GetValueOrDefault(name);
}
