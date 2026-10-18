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

    /// <summary>Значение по умолчанию (число, флаг 0/1); у строки — 0, текст — <see cref="DefaultText"/>.</summary>
    public double Default { get; init; }

    /// <summary>Строка по умолчанию у поля-строки.</summary>
    public string? DefaultText { get; init; }

    /// <summary>Скрытое (<c>@private</c>): чужим клиентам не уходит.</summary>
    public bool Private { get; init; }
}

/// <summary>Кому компонент уходит по сети (<c>@net</c>, как NetMode движка).</summary>
public enum NetMode : byte
{
    /// <summary>Всем, кто видит сущность.</summary>
    All,
    /// <summary>Хозяину и союзникам.</summary>
    Owner,
    /// <summary>Никому: только хосту.</summary>
    None,
}

/// <summary>Компонент схемы мира: номер для команд и поля. Берётся по имени при загрузке (<see cref="LoadContext.Component"/>).</summary>
public sealed class ComponentRef
{
    private readonly FieldRef[] _fields;

    internal ComponentRef(ModComponentInfo info)
        : this(info.Name, info.Id, info.Fields.Select(f => new FieldRef(f.Name, (FieldKind)f.Kind, f.Index, f.Slot) { Default = f.Default, DefaultText = f.DefaultText, Private = f.Private }))
    {
        Net = (NetMode)info.Net;
        Save = info.Save;
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

    /// <summary>Метка: полей нет, важно только, есть ли компонент.</summary>
    public bool Tag => _fields.Length == 0;

    /// <summary>Кому уходит по сети.</summary>
    public NetMode Net { get; init; }

    /// <summary>Идёт ли в сохранение.</summary>
    public bool Save { get; init; } = true;

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

    /// <summary>Поле по имени; нет — null.</summary>
    public FieldRef? Find(string field)
    {
        foreach (var f in _fields)
            if (f.Name == field)
                return f;
        return null;
    }
}

/// <summary>
/// Слой мира (docs/layers.md): номер — для команд <see cref="CommandWriter.Layer"/>, <see cref="CommandWriter.PushItem"/>
/// и инструментов Tools.Layer, <see cref="Tools.Items"/>. Сетка — каналы тайла; предметы — поля записи.
/// </summary>
public sealed record LayerRef(string Name, int Id, bool PerPlayer, int Cell, int Channels, IReadOnlyList<string> Fields)
{
    /// <summary>Сетка (иначе — предметы).</summary>
    public bool Grid => Cell == 0;

    /// <summary>Номер поля предмета по имени; нет — исключение.</summary>
    public int Field(string name)
    {
        for (var at = 0; at < Fields.Count; at++)
            if (Fields[at] == name)
                return at;
        throw new KeyNotFoundException($"у слоя {Name} нет поля {name}");
    }
}

/// <summary>Схема мира и слои, как их прислал хост.</summary>
internal sealed class WorldSchema
{
    private readonly Dictionary<string, ComponentRef> _byName = [];
    private readonly Dictionary<string, LayerRef> _layers = [];

    public WorldSchema(IEnumerable<ModComponentInfo> components, IEnumerable<ModLayerInfo>? layers = null)
    {
        foreach (var info in components)
            _byName[info.Name] = new ComponentRef(info);
        foreach (var layer in layers ?? [])
            _layers[layer.Name] = new LayerRef(layer.Name, layer.Id, layer.PerPlayer, layer.Cell, layer.Channels, layer.Fields);
    }

    public ComponentRef Get(string name) =>
        _byName.TryGetValue(name, out var component) ? component : throw new KeyNotFoundException($"нет компонента {name}");

    public ComponentRef? Find(string name) => _byName.GetValueOrDefault(name);

    public LayerRef? Layer(string name) => _layers.GetValueOrDefault(name);
}
