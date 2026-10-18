namespace Kharos.Engine.World;

/// <summary>
/// Сохранение мира по схеме. Компоненты и поля — по именам: мод добавил поле — у загруженных оно по умолчанию;
/// мод убрал компонент или поле — сохранённое пропускается; поле сменило вид — ошибка. Порядок строк хранилищ
/// сохраняется: загруженный мир идёт дальше так же, как шёл бы исходный.
/// <code>
/// "KHWD" version:u32 next:varint
/// strings: count:varint str × count            — строки с номера 1 (0 — пустая)
/// alive:   count:varint (номер − предыдущий):varint × count
/// components: count:varint, на компонент:
///   name:str fields:u8 (name:str kind:u8) × fields
///   rows:varint entity:varint × rows             — в порядке строк
///   на поле по порядку: Number — f64 × rows; Flag — u8 × rows; String — номер:varint × rows; Bytes — bytes × rows
/// </code>
/// Номер тика, случайный ряд и память модов — у хоста, не здесь.
/// </summary>
public static class WorldSave
{
    public const uint Version = 1;
    private static ReadOnlySpan<byte> Magic => "KHWD"u8;

    public static byte[] Save(GameWorld world)
    {
        var writer = new ByteWriter();
        foreach (var b in Magic)
            writer.Byte(b);
        writer.U32(Version);
        writer.Varint(world.NextEntity);
        var strings = world.Strings.All;
        writer.Varint((ulong)(strings.Count - 1));
        for (var id = 1; id < strings.Count; id++)
            writer.Text(strings[id]);
        var alive = new List<uint>(world.Count);
        world.Entities(alive);
        writer.Varint((ulong)alive.Count);
        uint previous = 0;
        foreach (var entity in alive)
        {
            writer.Varint(entity - previous);
            previous = entity;
        }
        var schema = world.Schema;
        writer.Varint((ulong)schema.Count);
        for (var id = 0; id < schema.Count; id++)
        {
            var def = schema[id];
            var store = world.Store(id);
            writer.Text(def.Name);
            writer.Byte((byte)def.Fields.Count);
            foreach (var field in def.Fields)
            {
                writer.Text(field.Name);
                writer.Byte((byte)field.Kind);
            }
            var n = store.Count;
            writer.Varint((ulong)n);
            foreach (var entity in store.Entities)
                writer.Varint(entity);
            foreach (var field in def.Fields)
            {
                switch (field.Kind)
                {
                    case FieldKind.Number:
                        writer.Doubles(store.Column(field.Slot));
                        break;
                    case FieldKind.Flag:
                        foreach (var value in store.Column(field.Slot))
                            writer.Byte(value != 0 ? (byte)1 : (byte)0);
                        break;
                    case FieldKind.String:
                        foreach (var value in store.Column(field.Slot))
                            writer.Varint((uint)value);
                        break;
                    default:
                        for (var row = 0; row < n; row++)
                            writer.Blob(store.BytesAt(field.Slot, row));
                        break;
                }
            }
        }
        return writer.ToArray();
    }

    /// <summary>Мир из сохранения по схеме модов; неверные данные — <see cref="FormatException"/>.</summary>
    public static GameWorld Load(Schema schema, ReadOnlySpan<byte> bytes)
    {
        var reader = new ByteReader(bytes);
        if (!reader.Take(4).SequenceEqual(Magic))
            throw new FormatException("не сохранение мира");
        var version = reader.U32();
        if (version != Version)
            throw new FormatException($"версия сохранения {version}, а нужна {Version}");
        var next = reader.Varint32();
        var strings = new string[reader.Count()];
        for (var at = 0; at < strings.Length; at++)
            strings[at] = reader.Text();
        var world = new GameWorld(schema, strings);
        var alive = new uint[reader.Count()];
        uint previous = 0;
        for (var at = 0; at < alive.Length; at++)
            alive[at] = previous = checked(previous + reader.Varint32());
        world.LoadEntities(next, alive);
        var components = reader.Count();
        for (var c = 0; c < components; c++)
        {
            var name = reader.Text();
            var fieldCount = reader.Byte();
            var fields = new (string Name, FieldKind Kind)[fieldCount];
            for (var at = 0; at < fieldCount; at++)
            {
                fields[at] = (reader.Text(), (FieldKind)reader.Byte());
                if (fields[at].Kind > FieldKind.Bytes)
                    throw new FormatException($"{name}.{fields[at].Name}: вид {(int)fields[at].Kind}");
            }
            var n = reader.Count();
            var entities = new uint[n];
            for (var row = 0; row < n; row++)
                entities[row] = reader.Varint32();
            var id = schema.Find(name);
            var store = id is { } known ? world.Store(known) : null;
            if (store is not null)
            {
                store.Reserve(n);
                foreach (var entity in entities)
                {
                    if (!world.Alive(entity) || store.Contains(entity))
                        throw new FormatException($"{name}: сущность {entity} не к месту");
                    store.AddRow(entity);
                }
            }
            var numbers = new double[n];
            foreach (var (fieldName, kind) in fields)
            {
                var field = store?.Def.Field(fieldName);
                if (field is not null && field.Kind != kind)
                    throw new FormatException($"{name}.{fieldName}: в сохранении {Schema.Word(kind)}, а в схеме {Schema.Word(field.Kind)}");
                if (kind == FieldKind.Bytes)
                {
                    for (var row = 0; row < n; row++)
                    {
                        var value = reader.Blob();
                        if (field is not null)
                            store!.SetBytes(field.Slot, row, value.ToArray());
                    }
                    continue;
                }
                switch (kind)
                {
                    case FieldKind.Number:
                        reader.Doubles(numbers);
                        break;
                    case FieldKind.Flag:
                        for (var row = 0; row < n; row++)
                            numbers[row] = reader.Byte() != 0 ? 1 : 0;
                        break;
                    default:
                        for (var row = 0; row < n; row++)
                        {
                            var text = reader.Varint32();
                            numbers[row] = world.Strings.Contains(text) ? text : throw new FormatException($"{name}.{fieldName}: нет строки №{text}");
                        }
                        break;
                }
                if (field is not null)
                    numbers.CopyTo(store!.MutableColumn(field.Slot));
            }
        }
        if (!reader.End)
            throw new FormatException("лишние байты в конце");
        return world;
    }
}
