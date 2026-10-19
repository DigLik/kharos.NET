using System.Buffers.Binary;
using System.Text.Json;

namespace Kharos.Engine.Layers;

/// <summary>Тип канала сетки или поля предмета. Числа пишутся little-endian; целые при записи обрезаются к нулю и до своего диапазона, NaN — 0.</summary>
public enum Channel : byte
{
    U8,
    U16,
    U32,
    F32,
    F64,
}

/// <summary>Чтение и запись каналов.</summary>
public static class Channels
{
    public static int Size(this Channel channel) => channel switch
    {
        Channel.U8 => 1,
        Channel.U16 => 2,
        Channel.U32 or Channel.F32 => 4,
        _ => 8,
    };

    public static double Read(this Channel channel, ReadOnlySpan<byte> bytes) => channel switch
    {
        Channel.U8 => bytes[0],
        Channel.U16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
        Channel.U32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
        Channel.F32 => BinaryPrimitives.ReadSingleLittleEndian(bytes),
        _ => BinaryPrimitives.ReadDoubleLittleEndian(bytes),
    };

    public static void Write(this Channel channel, Span<byte> bytes, double value)
    {
        switch (channel)
        {
            case Channel.U8:
                bytes[0] = (byte)Saturate(value, byte.MaxValue);
                break;
            case Channel.U16:
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)Saturate(value, ushort.MaxValue));
                break;
            case Channel.U32:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)Saturate(value, uint.MaxValue));
                break;
            case Channel.F32:
                BinaryPrimitives.WriteSingleLittleEndian(bytes, (float)value);
                break;
            default:
                BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
                break;
        }
    }

    /// <summary>Как <c>value as u8</c> в Rust: к нулю, в пределах [0, max], NaN — 0.</summary>
    private static double Saturate(double value, double max) => double.IsNaN(value) ? 0 : Math.Clamp(Math.Truncate(value), 0, max);

    internal static Channel Parse(string text) => text switch
    {
        "u8" => Channel.U8,
        "u16" => Channel.U16,
        "u32" => Channel.U32,
        "f32" => Channel.F32,
        "f64" => Channel.F64,
        _ => throw new FormatException($"нет канала {text}"),
    };
}

/// <summary>Чей слой: один на мир или свой у каждого игрока.</summary>
public enum LayerScope : byte
{
    World,
    Player,
}

/// <summary>Объявление слоя: строка layers.json (docs/layers.md, §2).</summary>
public sealed class LayerSpec
{
    private static readonly HashSet<string> Keys = ["scope", "shape", "channels", "cell", "fields", "save", "send", "remember", "sight", "allies", "about"];

    public required string Name { get; init; }
    public required LayerScope Scope { get; init; }

    /// <summary>Каналы тайла сетки; у предметов — пусто.</summary>
    public IReadOnlyList<Channel> Channels { get; init; } = [];

    /// <summary>Сторона ячейки предметов в тайлах; у сетки — 0.</summary>
    public int Cell { get; init; }

    /// <summary>Поля предмета по порядку (кроме места — x и y, они есть всегда, f64).</summary>
    public IReadOnlyList<(string Name, Channel Channel)> Fields { get; init; } = [];

    /// <summary>Сохраняется ли слой. Выводимый (занятость, видимое сейчас) — нет: его пересчитывают.</summary>
    public bool Save { get; init; }

    /// <summary>Уходит ли слой клиентам.</summary>
    public bool Send { get; init; } = true;

    /// <summary>Слой мира, который игроки запоминают: у каждого — версия, какой он её видел.</summary>
    public bool Remember { get; init; }

    /// <summary>
    /// Слой предметов мира, который игрок видит только в обзоре (следы): предмет уходит ему, пока его место в обзоре, и
    /// забывается, когда уходит из обзора. Запоминаемый слой предметов (<see cref="Remember"/>) уходит, каким игрок его
    /// видел последним. Оба — <see cref="ItemMemory"/>.
    /// </summary>
    public bool Sight { get; init; }

    /// <summary>Слой игрока, который читают и союзники.</summary>
    public bool Allies { get; init; }

    /// <summary>Предметы этого слоя уходят игроку по обзору (<see cref="ItemMemory"/>): запоминаемые или только в обзоре.</summary>
    public bool Fogged => !IsGrid && Scope == LayerScope.World && (Remember || Sight);

    public bool IsGrid => Cell == 0;

    /// <summary>Байт на тайл сетки или на запись предмета (с местом).</summary>
    public int Stride => IsGrid ? Channels.Sum(channel => channel.Size()) : 16 + Fields.Sum(item => item.Channel.Size());

    /// <summary>Объявления из layers.json: объект «имя → слой», порядок — как в файле. Неверное — <see cref="FormatException"/>.</summary>
    public static List<LayerSpec> ParseAll(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("слои — объект «имя → слой»");
        var specs = new List<LayerSpec>();
        foreach (var property in document.RootElement.EnumerateObject())
            specs.Add(Parse(property.Name, property.Value));
        return specs;
    }

    private static LayerSpec Parse(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new FormatException($"{name}: слой — объект");
        foreach (var key in value.EnumerateObject())
            if (!Keys.Contains(key.Name))
                throw new FormatException($"{name}: незнакомое поле {key.Name}");
        string Text(string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new FormatException($"{name}.{key}: нужна строка");
        bool Flag(string key, bool fallback)
        {
            if (!value.TryGetProperty(key, out var item))
                return fallback;
            return item.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new FormatException($"{name}.{key}: нужно true или false"),
            };
        }
        var scope = Text("scope") switch
        {
            "world" => LayerScope.World,
            "player" => LayerScope.Player,
            var other => throw new FormatException($"{name}: нет scope {other}"),
        };
        var channels = new List<Channel>();
        if (value.TryGetProperty("channels", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array)
                throw new FormatException($"{name}.channels: нужен список");
            foreach (var channel in list.EnumerateArray())
                channels.Add(channel.ValueKind == JsonValueKind.String ? Parse(name, channel.GetString()!) : throw new FormatException($"{name}.channels: канал — строка"));
        }
        var cell = value.TryGetProperty("cell", out var size) ? (size.TryGetInt32(out var number) ? number : throw new FormatException($"{name}.cell: нужно целое")) : 0;
        var fields = new List<(string, Channel)>();
        if (value.TryGetProperty("fields", out var table))
        {
            if (table.ValueKind != JsonValueKind.Object)
                throw new FormatException($"{name}.fields: нужен объект");
            foreach (var field in table.EnumerateObject())
                fields.Add((field.Name, field.Value.ValueKind == JsonValueKind.String ? Parse($"{name}.{field.Name}", field.Value.GetString()!) : throw new FormatException($"{name}.{field.Name}: канал — строка")));
        }
        var shape = Text("shape");
        var grid = shape switch
        {
            "grid" when channels.Count > 0 => true,
            "items" when cell > 0 => false,
            _ => throw new FormatException($"{name}: форма {shape} без каналов или ячейки"),
        };
        var spec = new LayerSpec
        {
            Name = name,
            Scope = scope,
            Channels = grid ? channels : [],
            Cell = grid ? 0 : cell,
            Fields = grid ? [] : fields,
            Save = Flag("save", false),
            Send = Flag("send", true),
            Remember = Flag("remember", false),
            Sight = Flag("sight", false),
            Allies = Flag("allies", false),
        };
        if (spec.Remember && scope != LayerScope.World)
            throw new FormatException($"{name}: запоминают только слои мира");
        if (spec.Sight && (scope != LayerScope.World || grid))
            throw new FormatException($"{name}: только в обзоре бывают предметы мира");
        if (spec.Sight && spec.Remember)
            throw new FormatException($"{name}: слой или запоминают, или видят только в обзоре");
        if (spec.Allies && scope != LayerScope.Player)
            throw new FormatException($"{name}: союзникам открывают только слой игрока");
        return spec;
    }

    private static Channel Parse(string where, string text)
    {
        try
        {
            return Layers.Channels.Parse(text);
        }
        catch (FormatException error)
        {
            throw new FormatException($"{where}: {error.Message}");
        }
    }
}
