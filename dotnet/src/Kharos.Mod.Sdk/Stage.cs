using Kharos.Mod.Sdk.Runtime;

namespace Kharos.Mod.Sdk;

/// <summary>
/// Сцена вызова (docs/dotnet-n5.md, Н5-4). В TS изменения видны сразу, у нас команды применяются после вызова; сцена
/// читает мир так, будто они уже применены: записи этого вызова (Add, Set, Remove, Destroy через
/// <see cref="CommandWriter"/>), новые сущности (Spawn) и столбцы кадра в их нынешнем виде, а остальное — инструментами
/// (мир до вызова). Берётся из контекста (<c>ctx.Stage()</c>), живёт до конца вызова; запись через сцену — те же
/// <see cref="Commands"/>. Чего сцена не знает: каскад Destroy по Mount (носитель уносит турели) и правки слоёв и земли.
/// </summary>
public sealed class Stage
{
    private readonly FramePart? _frame;
    private readonly SystemBinding? _binding;
    private readonly Dictionary<(uint Entity, int Component), Slot> _slots = [];
    private readonly Dictionary<(uint Entity, int Component), ComponentValues?> _read = [];
    private readonly HashSet<uint> _spawned = [];
    private readonly List<uint> _born = [];
    private readonly HashSet<uint> _destroyed = [];
    private Slot? _current;

    internal Stage(CommandWriter commands, FramePart? frame, SystemBinding? binding)
    {
        Commands = commands;
        _frame = frame;
        _binding = binding;
    }

    /// <summary>Команды вызова: всё, что пишется через них, сцена видит.</summary>
    public CommandWriter Commands { get; }

    /// <summary>Сущности, созданные в этом вызове, по порядку.</summary>
    public IReadOnlyList<uint> Spawned => _born;

    /// <summary>Жива ли сущность с учётом этого вызова.</summary>
    public bool Alive(uint entity)
    {
        if (_destroyed.Contains(entity))
            return false;
        if (_spawned.Contains(entity) || (_frame is not null && _frame.RowOf(entity) >= 0))
            return true;
        return Tools.Alive(entity);
    }

    /// <summary>Есть ли у сущности компонент с учётом этого вызова.</summary>
    public bool Has(uint entity, ComponentRef component)
    {
        if (_destroyed.Contains(entity))
            return false;
        if (_slots.TryGetValue((entity, component.Id), out var slot) && slot.Present is { } present)
            return present;
        return Before(entity, component);
    }

    /// <summary>Число поля (флаг — 0/1, строка — номер); компонента нет — false.</summary>
    public bool TryNumber(uint entity, ComponentRef component, FieldRef field, out double value)
    {
        value = 0;
        if (!Has(entity, component) || !field.Columnar)
            return false;
        var slot = _slots.GetValueOrDefault((entity, component.Id));
        if (slot is not null && slot.Written(field.Index))
        {
            value = slot.Texts[field.Index] is { } text ? Tools.StringId(text) ?? 0 : slot.Numbers[field.Index];
            return true;
        }
        if (slot is { Fresh: true })
        {
            value = field.DefaultText is { Length: > 0 } text ? Tools.StringId(text) ?? 0 : field.Default;
            return true;
        }
        value = BaseNumber(entity, component, field);
        return true;
    }

    /// <summary>Число поля; компонента нет — исключение.</summary>
    public double Number(uint entity, ComponentRef component, FieldRef field) =>
        TryNumber(entity, component, field, out var value) ? value : throw new InvalidOperationException($"у {entity} нет {component.Name}");

    /// <summary>Флаг поля; компонента нет — исключение.</summary>
    public bool Flag(uint entity, ComponentRef component, FieldRef field) => Number(entity, component, field) != 0;

    /// <summary>Строка поля-строки; компонента нет — null.</summary>
    public string? Text(uint entity, ComponentRef component, FieldRef field)
    {
        if (!Has(entity, component))
            return null;
        var slot = _slots.GetValueOrDefault((entity, component.Id));
        if (slot is not null && slot.Written(field.Index) && slot.Texts[field.Index] is { } text)
            return text;
        if (slot is { Fresh: true } && !slot.Written(field.Index))
            return field.DefaultText ?? "";
        return TryNumber(entity, component, field, out var id) ? Tools.Text((uint)id) : null;
    }

    /// <summary>Байты поля-байтов; компонента нет — null.</summary>
    public byte[]? Bytes(uint entity, ComponentRef component, FieldRef field)
    {
        if (!Has(entity, component))
            return null;
        var slot = _slots.GetValueOrDefault((entity, component.Id));
        if (slot is not null && slot.Bytes[field.Index] is { } bytes)
            return bytes;
        if (slot is { Fresh: true })
            return [];
        return Read(entity, component)?.Bytes(field) ?? [];
    }

    // Записи CommandWriter.

    internal void OnSpawn(uint entity)
    {
        _current = null;
        if (_spawned.Add(entity))
            _born.Add(entity);
    }

    internal void OnDestroy(uint entity)
    {
        _current = null;
        _destroyed.Add(entity);
    }

    internal void OnFields(bool add, uint entity, ComponentRef component)
    {
        _current = null;
        if (_destroyed.Contains(entity))
            return;
        var slot = SlotOf(entity, component);
        var had = slot.Present ?? Before(entity, component);
        if (add)
        {
            // Add поверх имеющегося оставляет прежние значения, на новом — значения по умолчанию.
            if (!had)
            {
                slot.Fresh = true;
                slot.Clear();
            }
            slot.Present = true;
            _current = slot;
        }
        else if (had)
        {
            slot.Present = true;
            _current = slot;
        }
    }

    internal void OnRemove(uint entity, ComponentRef component)
    {
        _current = null;
        var slot = SlotOf(entity, component);
        slot.Present = false;
        slot.Fresh = false;
        slot.Clear();
    }

    internal void OnNumber(FieldRef field, double value) => _current?.Write(field.Index, value, null);

    internal void OnText(FieldRef field, string value) => _current?.Write(field.Index, 0, value);

    internal void OnBytes(FieldRef field, ReadOnlySpan<byte> value)
    {
        if (_current is { } slot)
            slot.Bytes[field.Index] = value.ToArray();
    }

    /// <summary>Был ли компонент до вызова (новая сущность — без компонентов).</summary>
    private bool Before(uint entity, ComponentRef component)
    {
        if (_spawned.Contains(entity))
            return false;
        if (_frame is not null && _binding is not null && _binding.OffsetOf(component.Id) >= 0 && _frame.RowOf(entity) >= 0)
            return true;
        return Read(entity, component) is not null;
    }

    /// <summary>Значение до вызова: из кадра (в нынешнем виде — со своими правками столбцов) или инструментом.</summary>
    private double BaseNumber(uint entity, ComponentRef component, FieldRef field)
    {
        if (_frame is not null && _binding is not null && _binding.OffsetOf(component.Id) is >= 0 and var offset && _frame.RowOf(entity) is >= 0 and var row)
            return _frame.Column(offset + field.Slot)[row];
        return Read(entity, component)?.Number(field) ?? 0;
    }

    private ComponentValues? Read(uint entity, ComponentRef component)
    {
        if (!_read.TryGetValue((entity, component.Id), out var values))
            _read[(entity, component.Id)] = values = Tools.Get(entity, component);
        return values;
    }

    private Slot SlotOf(uint entity, ComponentRef component)
    {
        if (!_slots.TryGetValue((entity, component.Id), out var slot))
            _slots[(entity, component.Id)] = slot = new Slot(component.Fields.Count);
        return slot;
    }

    /// <summary>Записи вызова об одном компоненте сущности.</summary>
    private sealed class Slot(int fields)
    {
        private ulong _written;

        /// <summary>Есть ли компонент после записей; null — записей о наличии не было.</summary>
        public bool? Present { get; set; }

        /// <summary>Добавлен в этом вызове там, где его не было: незаписанные поля — по умолчанию.</summary>
        public bool Fresh { get; set; }

        public double[] Numbers { get; } = new double[fields];
        public string?[] Texts { get; } = new string?[fields];
        public byte[]?[] Bytes { get; } = new byte[]?[fields];

        public bool Written(int index) => (_written & (1UL << index)) != 0;

        public void Write(int index, double number, string? text)
        {
            _written |= 1UL << index;
            Numbers[index] = number;
            Texts[index] = text;
        }

        public void Clear()
        {
            _written = 0;
            Array.Clear(Numbers);
            Array.Clear(Texts);
            Array.Clear(Bytes);
        }
    }
}
