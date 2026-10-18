using System.Buffers.Binary;
using Kharos.Mod.Abi;

namespace Kharos.Mod.Sdk;

// 11 Columns: пакетное чтение полей-столбцов чужих сущностей (ModAbi.ToolColumns).
public static partial class Tools
{
    /// <summary>
    /// Поля у многих сущностей разом: на пару (компонент, поле) — n значений подряд в values (k·n; флаг — 0/1, строка —
    /// номер; нет компонента — 0), has — есть ли компонент (k·n; пусто — не нужно).
    /// </summary>
    public static void Columns(ReadOnlySpan<uint> entities, ReadOnlySpan<(ComponentRef Component, FieldRef Field)> pairs, Span<double> values, Span<bool> has = default)
    {
        var count = entities.Length * pairs.Length;
        if (values.Length < count || (!has.IsEmpty && has.Length < count))
            throw new ArgumentException("мало места под ответ");
        Begin();
        Entities(entities);
        U32((uint)pairs.Length);
        foreach (var (component, field) in pairs)
        {
            if (!field.Columnar)
                throw new ArgumentException($"{component.Name}.{field.Name} — не столбец");
            U32((uint)component.Id).U32((uint)field.Index);
        }
        var output = Call(ModAbi.ToolColumns);
        for (var at = 0; at < count; at++)
        {
            values[at] = BinaryPrimitives.ReadDoubleLittleEndian(output[(at * 8)..]);
            if (!has.IsEmpty)
                has[at] = output[count * 8 + at] != 0;
        }
    }

    /// <summary>Одно поле у многих сущностей; нет компонента — 0.</summary>
    public static double[] Column(ReadOnlySpan<uint> entities, ComponentRef component, FieldRef field)
    {
        var values = new double[entities.Length];
        Columns(entities, [(component, field)], values);
        return values;
    }
}
