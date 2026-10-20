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
    /// <summary>Есть ли компонент в мире до вызова — у меток и у того, что читали только на наличие (Prefetch).</summary>
    private readonly Dictionary<(uint Entity, int Component), bool> _present = [];
    private readonly Dictionary<uint, bool> _alive = [];
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
        // Мир до вызова не меняется: ответ — в кэш вызова.
        if (!_alive.TryGetValue(entity, out var alive))
            _alive[entity] = alive = Tools.Alive(entity);
        return alive;
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
        // То же, что Has, но запись о компоненте ищется раз (горячий путь: Num на каждое поле).
        if (_destroyed.Contains(entity))
            return false;
        _slots.TryGetValue((entity, component.Id), out var slot);
        if (!(slot?.Present ?? Before(entity, component)) || !field.Columnar)
            return false;
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

    /// <summary>
    /// Прочитать компоненты многих сущностей разом (мир до вызова): поля-столбцы — одним Columns на компонент; метки и
    /// компоненты с полями-байтами — только наличие, одним Has (байты по-прежнему читает Get, когда спросят). Дальше Has и
    /// Number по ним вызова инструмента не стоят. Ничего не меняет в ответах сцены — только сколько раз она зовёт движок.
    /// </summary>
    public void Prefetch(ReadOnlySpan<uint> entities, params ReadOnlySpan<ComponentRef> components)
    {
        var asked = new List<uint>(entities.Length);
        foreach (var component in components)
        {
            var fields = component.Fields;
            var columnar = true;
            foreach (var field in fields)
                columnar &= field.Columnar;
            var whole = columnar && !component.Tag;
            asked.Clear();
            foreach (var entity in entities)
                if (!_spawned.Contains(entity) && !_read.ContainsKey((entity, component.Id)) && (whole || !_present.ContainsKey((entity, component.Id))))
                    asked.Add(entity);
            if (asked.Count == 0)
                continue;
            var list = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(asked);
            if (!whole)
            {
                var has = new bool[list.Length];
                Tools.Has(list, component, has);
                for (var i = 0; i < list.Length; i++)
                    _present[(list[i], component.Id)] = has[i];
                continue;
            }
            var pairs = new (ComponentRef, FieldRef)[fields.Count];
            for (var k = 0; k < pairs.Length; k++)
                pairs[k] = (component, fields[k]);
            var values = new double[pairs.Length * list.Length];
            var present = new bool[pairs.Length * list.Length];
            Tools.Columns(list, pairs, values, present);
            for (var i = 0; i < list.Length; i++)
            {
                if (!present[i])
                {
                    _read[(list[i], component.Id)] = null;
                    continue;
                }
                var numbers = new double[fields.Count];
                for (var k = 0; k < numbers.Length; k++)
                    numbers[fields[k].Index] = values[k * list.Length + i];
                _read[(list[i], component.Id)] = new ComponentValues(numbers, new byte[]?[fields.Count]);
            }
        }
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
        // Наличие уже известно (Prefetch, метка) — без вызова инструмента; мир до вызова не меняется.
        if (_present.TryGetValue((entity, component.Id), out var known))
            return known;
        if (!component.Tag)
            return Read(entity, component) is not null;
        // У метки полей нет: Get ответит пусто и тогда, когда она есть.
        var has = Tools.Has(entity, component);
        _present[(entity, component.Id)] = has;
        return has;
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
