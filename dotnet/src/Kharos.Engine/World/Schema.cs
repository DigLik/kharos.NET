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
}

/// <summary>Компонент в схеме: имя и поля по порядку объявления.</summary>
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

    public FieldDef? Field(string name)
    {
        foreach (var field in _fields)
            if (field.Name == name)
                return field;
        return null;
    }

    /// <summary>Номер поля по имени; нет — исключение (опечатка не пройдёт молча).</summary>
    public int FieldIndex(string name) => Field(name)?.Index ?? throw new KeyNotFoundException($"у {Name} нет поля {name}");

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

/// <summary>Компоненты движка: номера постоянные, объявлены всегда (по ним клиент рисует, обзор считает).</summary>
public static class Builtin
{
    public const int Position = 0;
    public const int Facing = 1;
    public const int Owner = 2;
    public const int Proto = 3;
    public const int Sight = 4;
}

/// <summary>
/// Схема компонентов. Компоненты объявляют моды (данными, не типами C#): имя, поля, вид поля и значение по умолчанию.
/// Один компонент могут объявлять несколько модов — поля складываются; одно поле с разными видами — ошибка. После
/// того как по схеме создан мир, она закрыта.
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
        if (!_byName.TryGetValue(component, out var id))
        {
            id = _components.Count;
            _components.Add(new ComponentDef(component, id));
            _byName.Add(component, id);
        }
        var def = _components[id];
        if (def.Field(field) is { } existing)
            return existing.Kind == kind ? existing : throw new ArgumentException($"{component}.{field}: объявлено и как {Word(existing.Kind)}, и как {Word(kind)}");
        return def.Add(field, kind, @default);
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
