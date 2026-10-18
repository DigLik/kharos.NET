using System.Text.Json;

namespace Kharos.Engine.World;

/// <summary>
/// Вид поля компонента. Числа, флаги и строки лежат столбцами f64 (флаг — 0 или 1, строка — номер в
/// <see cref="Strings"/>) и уходят системам столбцами; <see cref="Bytes"/> — значение целиком байтами, по сущности:
/// движок его не читает, формат знает мод (таблицы Luau-пилота: очередь завода и т. п.).
/// </summary>
public enum FieldKind : byte
{
    Number,
    Flag,
    String,
    Bytes,
}

/// <summary>Поле компонента в схеме.</summary>
public sealed class FieldDef
{
    internal FieldDef(string name, FieldKind kind, int index, int slot, FieldValue @default)
    {
        Name = name;
        Kind = kind;
        Index = index;
        Slot = slot;
        Default = @default;
    }

    public string Name { get; }
    public FieldKind Kind { get; }

    /// <summary>Номер поля в компоненте по порядку объявления: им поле названо в буфере команд и в маске изменений.</summary>
    public int Index { get; }

    /// <summary>Номер столбца: среди столбцов f64 компонента или среди полей <see cref="FieldKind.Bytes"/>.</summary>
    public int Slot { get; }

    public FieldValue Default { get; }

    /// <summary>Лежит ли поле в столбце f64.</summary>
    public bool Columnar => Kind != FieldKind.Bytes;

    /// <summary>Скрытое поле (<c>@private</c>): видят хозяин и союзники, чужим оно не уходит.</summary>
    public bool Private { get; internal set; }
}

/// <summary>Кому компонент уходит по сети (<c>@net</c>).</summary>
public enum NetMode : byte
{
    /// <summary>Всем, кто видит сущность.</summary>
    All,

    /// <summary>Хозяину и союзникам (как PRIVATE в wire.ts).</summary>
    Owner,

    /// <summary>Никому: только хосту.</summary>
    None,
}

/// <summary>
/// Компонент в схеме: имя и поля по порядку объявления. Без полей — метка: важно только, есть ли он у сущности.
/// Атрибуты (<see cref="Net"/>, <see cref="Private"/>, <see cref="Save"/>) читают сеть и сохранение.
/// </summary>
public sealed class ComponentDef
{
    private readonly List<FieldDef> _fields = [];

    internal ComponentDef(string name, int id)
    {
        Name = name;
        Id = id;
    }

    public string Name { get; }
    public int Id { get; }
    public IReadOnlyList<FieldDef> Fields => _fields;

    /// <summary>Сколько столбцов f64 и сколько полей байтами.</summary>
    public int Columns { get; private set; }
    public int BytesFields { get; private set; }

    /// <summary>Метка: компонент без полей.</summary>
    public bool Tag => _fields.Count == 0;

    /// <summary>Кому уходит по сети; по умолчанию — всем, кто видит сущность.</summary>
    public NetMode Net { get; internal set; } = NetMode.All;

    /// <summary>Идёт ли в сохранение; по умолчанию — да.</summary>
    public bool Save { get; internal set; } = true;

    /// <summary>Маска скрытых полей (<c>@private</c>) по <see cref="FieldDef.Index"/>.</summary>
    public ulong PrivateMask { get; private set; }

    /// <summary>Имена скрытых полей по порядку объявления.</summary>
    public IEnumerable<string> Private => _fields.Where(def => def.Private).Select(def => def.Name);

    public FieldDef? Field(string name)
    {
        foreach (var field in _fields)
            if (field.Name == name)
                return field;
        return null;
    }

    /// <summary>Номер поля по имени; нет — исключение (опечатка не пройдёт молча).</summary>
    public int FieldIndex(string name) => Field(name)?.Index ?? throw new KeyNotFoundException($"у {Name} нет поля {name}");

    internal void MarkPrivate(FieldDef field)
    {
        field.Private = true;
        PrivateMask |= 1UL << field.Index;
    }

    internal FieldDef Add(string name, FieldKind kind, FieldValue @default)
    {
        if (_fields.Count == Schema.MaxFields)
            throw new InvalidOperationException($"{Name}: больше {Schema.MaxFields} полей");
        var slot = kind == FieldKind.Bytes ? BytesFields++ : Columns++;
        var field = new FieldDef(name, kind, _fields.Count, slot, @default);
        _fields.Add(field);
        return field;
    }
}

/// <summary>
/// Компоненты движка: номера постоянные, объявлены всегда (по ним клиент рисует, обзор считает, идут ход и пути).
/// Поля — docs/dotnet-n5.md, Н5.1; здание стоит по левому верхнему тайлу, как в TS.
/// </summary>
public static class Builtin
{
    public const int Position = 0;
    public const int Facing = 1;
    public const int Owner = 2;
    public const int Proto = 3;
    public const int Sight = 4;

    /// <summary>Основание: <c>w, h</c> в тайлах, <c>solid</c> — занимает тайлы (готовое здание), <c>open</c> — открытые ворота.</summary>
    public const int Footprint = 5;

    /// <summary>Тело: <c>radius</c>, <c>mass</c> (0 — radius²), <c>rooted</c> — не сдвигается (TS Converting).</summary>
    public const int Body = 6;

    /// <summary>Ход: <c>chassis</c> — запись moves.json, <c>speed, turn</c>.</summary>
    public const int Mover = 7;

    /// <summary>Путь: цель и состояние поиска; <c>points</c> — байты, f64 x, y подряд. Уходит только хозяину.</summary>
    public const int Path = 8;

    /// <summary>Место на носителе (TS Attached и Turret.angle): <c>parent</c> — сущность (0 — нет), <c>along, across, angle</c>.</summary>
    public const int Mount = 9;

    /// <summary>Союз: <c>player</c>, <c>head</c> — глава (союзны одинаковые head ≥ 0, −1 — вне союза).</summary>
    public const int Team = 10;

    /// <summary>Кому видна сущность (sightingOf в TS): <c>rule</c> — <see cref="Sightings"/>, точки a, b, c; <c>viewers</c> — байты, u32 подряд. Не уходит в сеть.</summary>
    public const int Sighting = 11;

    /// <summary>Вспышка обзора: <c>radius</c>, <c>until</c> — тик конца, <c>all</c> — всем (Glare), иначе хозяину (радар Scan).</summary>
    public const int Reveal = 12;

    /// <summary>Сколько компонентов движка: компоненты модов получают номера с этого.</summary>
    public const int Count = 13;
}

/// <summary>Правила <c>Sighting.rule</c> (sightingOf в vision.ts): кто видит сущность.</summary>
public static class Sightings
{
    /// <summary>Те, кто видит её место.</summary>
    public const int Seen = 0;

    /// <summary>Хозяин и союзники.</summary>
    public const int Allies = 1;

    /// <summary>Все, мимо тумана.</summary>
    public const int Everyone = 2;

    /// <summary>Игроки из <c>viewers</c>.</summary>
    public const int Listed = 3;

    /// <summary>Те, кто видит хотя бы одну из точек a, b, c.</summary>
    public const int Points = 4;
}

/// <summary>
/// Схема компонентов. Компоненты объявляют моды (данными, не типами C#): имя, поля, вид поля и значение по умолчанию;
/// компонент без полей — метка. Один компонент могут объявлять несколько модов — поля складываются; одно поле с
/// разными видами — ошибка. Атрибуты (<see cref="Attributes"/>) — у последнего, кто их задал. После того как по схеме
/// создан мир, она закрыта.
/// </summary>
public sealed class Schema
{
    /// <summary>Полей в компоненте не больше — по маске изменений (u64) и байту счёта полей в команде.</summary>
    public const int MaxFields = 64;

    private readonly List<ComponentDef> _components = [];
    private readonly Dictionary<string, int> _byName = [];

    public Schema()
    {
        DeclareNumber("Position", "x", 0);
        DeclareNumber("Position", "y", 0);
        DeclareNumber("Facing", "angle", 0);
        DeclareNumber("Owner", "player", 0);
        DeclareNumber("Proto", "id", 0);
        DeclareNumber("Sight", "radius", 0);

        DeclareNumber("Footprint", "w", 1);
        DeclareNumber("Footprint", "h", 1);
        DeclareFlag("Footprint", "solid", false);
        DeclareFlag("Footprint", "open", false);

        DeclareNumber("Body", "radius", 0.5);
        DeclareNumber("Body", "mass", 0);
        DeclareFlag("Body", "rooted", false);

        DeclareString("Mover", "chassis", "");
        DeclareNumber("Mover", "speed", 0);
        DeclareNumber("Mover", "turn", 0);

        DeclareNumber("Path", "goalX", 0);
        DeclareNumber("Path", "goalY", 0);
        DeclareNumber("Path", "wait", 0);
        DeclareNumber("Path", "tries", 0);
        DeclareNumber("Path", "near", 0);
        DeclareFlag("Path", "roads", false);
        DeclareFlag("Path", "direct", false);
        DeclareFlag("Path", "stuck", false);
        DeclareNumber("Path", "best", -1);
        DeclareFlag("Path", "slot", false);
        DeclareBytes("Path", "points");
        Attributes("Path", NetMode.Owner);

        DeclareNumber("Mount", "parent", 0);
        DeclareNumber("Mount", "along", 0);
        DeclareNumber("Mount", "across", 0);
        DeclareNumber("Mount", "angle", 0);

        DeclareNumber("Team", "player", 0);
        DeclareNumber("Team", "head", -1);

        DeclareNumber("Sighting", "rule", Sightings.Seen);
        foreach (var point in (string[])["ax", "ay", "bx", "by", "cx", "cy"])
            DeclareNumber("Sighting", point, 0);
        DeclareBytes("Sighting", "viewers");
        Attributes("Sighting", NetMode.None);

        DeclareNumber("Reveal", "radius", 0);
        DeclareNumber("Reveal", "until", 0);
        DeclareFlag("Reveal", "all", false);
    }

    public IReadOnlyList<ComponentDef> Components => _components;
    public int Count => _components.Count;

    /// <summary>Закрыта ли схема: по ней уже создан мир.</summary>
    public bool Sealed { get; private set; }

    public ComponentDef this[int id] => _components[id];

    public int? Find(string name) => _byName.TryGetValue(name, out var id) ? id : null;

    /// <summary>Номер компонента; нет — исключение.</summary>
    public int Id(string name) => Find(name) ?? throw new KeyNotFoundException($"нет компонента {name}");

    public ComponentDef Get(string name) => _components[Id(name)];

    /// <summary>Объявляет компонент без полей (метку), если его ещё нет; поля ему можно добавить и потом.</summary>
    public ComponentDef DeclareTag(string component)
    {
        if (Sealed)
            throw new InvalidOperationException($"{component}: схема закрыта, мир уже создан");
        return _components[Ensure(component)];
    }

    /// <summary>
    /// Атрибуты компонента: кому уходит по сети, скрытые поля (добавляются к прежним), идёт ли в сохранение; null — не
    /// менять. Компонент и скрытые поля должны быть объявлены, иначе <see cref="ArgumentException"/>.
    /// </summary>
    public ComponentDef Attributes(string component, NetMode? net = null, IEnumerable<string>? @private = null, bool? save = null)
    {
        if (Sealed)
            throw new InvalidOperationException($"{component}: схема закрыта, мир уже создан");
        var def = Find(component) is { } id ? _components[id] : throw new ArgumentException($"{component}: атрибуты у необъявленного компонента");
        if (net is { } mode)
            def.Net = mode;
        if (save is { } saved)
            def.Save = saved;
        foreach (var name in @private ?? [])
            def.MarkPrivate(def.Field(name) ?? throw new ArgumentException($"{component}: в @private нет поля {name}"));
        return def;
    }

    public FieldDef DeclareNumber(string component, string field, double @default) => Declare(component, field, FieldKind.Number, @default);
    public FieldDef DeclareFlag(string component, string field, bool @default) => Declare(component, field, FieldKind.Flag, @default);
    public FieldDef DeclareString(string component, string field, string @default) => Declare(component, field, FieldKind.String, @default);
    public FieldDef DeclareBytes(string component, string field) => Declare(component, field, FieldKind.Bytes, FieldValue.Of([]));

    /// <summary>
    /// Поле по значению JSON, как в данных мода: число → <see cref="FieldKind.Number"/>, true/false →
    /// <see cref="FieldKind.Flag"/>, строка → <see cref="FieldKind.String"/>, таблица, список или null →
    /// <see cref="FieldKind.Bytes"/> (по умолчанию пусто: формат значения знает мод).
    /// </summary>
    public FieldDef Declare(string component, string field, JsonElement @default) => @default.ValueKind switch
    {
        JsonValueKind.Number => DeclareNumber(component, field, @default.GetDouble()),
        JsonValueKind.True or JsonValueKind.False => DeclareFlag(component, field, @default.GetBoolean()),
        JsonValueKind.String => DeclareString(component, field, @default.GetString()!),
        _ => DeclareBytes(component, field),
    };

    /// <summary>Объявляет поле (и компонент, если его ещё нет). То же поле того же вида — не ошибка, значение по умолчанию остаётся первым.</summary>
    public FieldDef Declare(string component, string field, FieldKind kind, FieldValue @default)
    {
        if (Sealed)
            throw new InvalidOperationException($"{component}.{field}: схема закрыта, мир уже создан");
        if (@default.Kind != kind)
            throw new ArgumentException($"{component}.{field}: значение по умолчанию — {Word(@default.Kind)}, а поле — {Word(kind)}");
        var def = _components[Ensure(component)];
        if (def.Field(field) is { } existing)
            return existing.Kind == kind ? existing : throw new ArgumentException($"{component}.{field}: объявлено и как {Word(existing.Kind)}, и как {Word(kind)}");
        return def.Add(field, kind, @default);
    }

    private int Ensure(string component)
    {
        if (!_byName.TryGetValue(component, out var id))
        {
            id = _components.Count;
            _components.Add(new ComponentDef(component, id));
            _byName.Add(component, id);
        }
        return id;
    }

    internal void Seal() => Sealed = true;

    internal static string Word(FieldKind kind) => kind switch
    {
        FieldKind.Number => "число",
        FieldKind.Flag => "флаг",
        FieldKind.String => "строка",
        _ => "байты",
    };
}

/// <summary>Значение поля: число, флаг, строка или байты. Для объявлений, условий систем и удобного API мира — не для горячих путей.</summary>
public readonly struct FieldValue : IEquatable<FieldValue>
{
    private FieldValue(FieldKind kind, double number, string? text, byte[]? bytes)
    {
        Kind = kind;
        Number = number;
        Text = text;
        Bytes = bytes;
    }

    public FieldKind Kind { get; }

    /// <summary>Число; у флага — 0 или 1.</summary>
    public double Number { get; }

    public string? Text { get; }
    public byte[]? Bytes { get; }

    public bool Flag => Number != 0;

    public static FieldValue Of(double number) => new(FieldKind.Number, number, null, null);
    public static FieldValue Of(bool flag) => new(FieldKind.Flag, flag ? 1 : 0, null, null);
    public static FieldValue Of(string text) => new(FieldKind.String, 0, text, null);
    public static FieldValue Of(byte[] bytes) => new(FieldKind.Bytes, 0, null, bytes);

    public static implicit operator FieldValue(double number) => Of(number);
    public static implicit operator FieldValue(bool flag) => Of(flag);
    public static implicit operator FieldValue(string text) => Of(text);
    public static implicit operator FieldValue(byte[] bytes) => Of(bytes);

    public bool Equals(FieldValue other) => Kind == other.Kind && Kind switch
    {
        FieldKind.String => Text == other.Text,
        FieldKind.Bytes => (Bytes ?? []).AsSpan().SequenceEqual(other.Bytes ?? []),
        _ => BitConverter.DoubleToInt64Bits(Number) == BitConverter.DoubleToInt64Bits(other.Number),
    };

    public override bool Equals(object? obj) => obj is FieldValue other && Equals(other);

    public override int GetHashCode() => Kind switch
    {
        FieldKind.String => HashCode.Combine(Kind, Text),
        FieldKind.Bytes => HashCode.Combine(Kind, Bytes?.Length),
        _ => HashCode.Combine(Kind, Number),
    };

    public static bool operator ==(FieldValue left, FieldValue right) => left.Equals(right);
    public static bool operator !=(FieldValue left, FieldValue right) => !left.Equals(right);

    public override string ToString() => Kind switch
    {
        FieldKind.Number => Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        FieldKind.Flag => Flag ? "true" : "false",
        FieldKind.String => $"\"{Text}\"",
        _ => $"[{Bytes?.Length ?? 0} байт]",
    };
}
