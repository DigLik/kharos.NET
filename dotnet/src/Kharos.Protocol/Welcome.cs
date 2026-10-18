using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;

namespace Kharos.Protocol;

/// <summary>Поле компонента в описании схемы; Scale — точность числа в сети (<see cref="NetFormat"/>), 0 — точно.</summary>
public sealed record FieldInfo(string Name, FieldKind Kind, FieldValue Default, double Scale = 0);

/// <summary>Мод хоста для доставки: имя, версия, хеш пакета (<see cref="ContentPack"/>) и его сжатый размер.</summary>
public sealed record ModRef(string Name, string Version, byte[] Hash, int Size);

/// <summary>Компонент в описании схемы: номер — место в списке.</summary>
public sealed record ComponentInfo(string Name, IReadOnlyList<FieldInfo> Fields);

/// <summary>Обзор: прямоугольник карты в тайлах [Left, Right) × [Top, Bottom) и есть ли туман.</summary>
public readonly record struct MapBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>
/// Приветствие (хост → клиент, первым): кто игрок, партия, зерно и местность, длина тика, сумма модов, схема мира и
/// слои. По схеме клиент строит свой мир-реплику — номера компонентов совпадают с хостом.
/// <code>
/// 0x01 version:varint player:varint token:str name:str game:str seed:varint dt:f64 tick:varint checksum:blob
///      mods:varint (name:str version:str hash:32 size:varint)
///      map:u8 [left top right bottom:svarint] fog:u8
///      land:u8 [GeneratorConfig: seed:svarint, 24 × f64 по порядку свойств, blank:u8]
///      components:varint (name:str fields:varint (name:str kind:u8 default [scale:f64 у числа]))
///      layers:varint (name:str scope:u8 channels:varint u8× cell:varint fields:varint (name:str channel:u8) flags:u8)
///   default: число — f64, флаг — u8, строка — str, байты — ничего
///   flags: 1 save, 2 send, 4 remember, 8 allies
/// </code>
/// </summary>
public sealed class Welcome
{
    /// <summary>Версия протокола: разошлась — клиент предлагает обновиться.</summary>
    public const int ProtocolVersion = 2;

    public int Version { get; init; } = ProtocolVersion;
    public required uint Player { get; init; }
    public required string Game { get; init; }

    /// <summary>По чему хост узнает игрока при следующем входе (и после перезапуска сервера); клиенту — запомнить.</summary>
    public string Token { get; init; } = "";

    /// <summary>Ник игрока, как его видят все.</summary>
    public string Name { get; init; } = "";

    /// <summary>Моды хоста в порядке загрузки: чего нет в кэше, клиент просит (<see cref="MessageType.Fetch"/>).</summary>
    public IReadOnlyList<ModRef> Mods { get; init; } = [];

    /// <summary>Зерно мира (случайный ряд хоста).</summary>
    public ulong Seed { get; init; }

    /// <summary>Длина тика, секунды.</summary>
    public double Dt { get; init; } = 0.05;

    /// <summary>Тик хоста при входе.</summary>
    public ulong Tick { get; init; }

    /// <summary>Сумма модов хоста (GameContent.Checksum): не совпала со своей — клиенту нужны моды хоста.</summary>
    public byte[] Checksum { get; init; } = [];

    /// <summary>Карта партии; null — карты и обзора нет.</summary>
    public MapBounds? Map { get; init; }

    /// <summary>Туман войны.</summary>
    public bool Fog { get; init; }

    /// <summary>Местность: клиент строит её сам по генератору; null — нет.</summary>
    public GeneratorConfig? Land { get; init; }

    public required IReadOnlyList<ComponentInfo> Components { get; init; }
    public IReadOnlyList<LayerSpec> Layers { get; init; } = [];

    /// <summary>Описание схемы мира с точностью чисел (null — точно).</summary>
    public static List<ComponentInfo> Describe(Schema schema, NetFormat? format = null) =>
        [.. schema.Components.Select(c => new ComponentInfo(c.Name, [.. c.Fields.Select(f => new FieldInfo(f.Name, f.Kind, f.Default, format?.Scale(c.Id, f.Index) ?? 0))]))];

    /// <summary>Точность чисел по описанию схемы.</summary>
    public NetFormat BuildFormat() => new([.. Components.Select(c => c.Fields.Select(f => f.Kind == FieldKind.Number ? f.Scale : 0).ToArray())]);

    /// <summary>Схема по описанию; номера компонентов — как у хоста, иначе <see cref="FormatException"/>.</summary>
    public Schema BuildSchema()
    {
        var schema = new Schema();
        for (var id = 0; id < Components.Count; id++)
        {
            var component = Components[id];
            foreach (var field in component.Fields)
                schema.Declare(component.Name, field.Name, field.Kind, field.Default);
            if (schema.Find(component.Name) != id)
                throw new FormatException($"компонент {component.Name}: номер {schema.Find(component.Name)}, у хоста {id}");
        }
        if (schema.Count != Components.Count)
            throw new FormatException("в схеме лишние компоненты");
        return schema;
    }

    public byte[] Encode()
    {
        var w = new WireWriter(1024);
        w.Byte((byte)MessageType.Welcome).Varint((ulong)Version).Varint(Player).Text(Token).Text(Name).Text(Game).Varint(Seed).F64(Dt).Varint(Tick).Blob(Checksum);
        w.Varint((ulong)Mods.Count);
        foreach (var mod in Mods)
        {
            if (mod.Hash.Length != Messages.HashSize)
                throw new ArgumentException($"мод {mod.Name}: хеш не 32 байта");
            w.Text(mod.Name).Text(mod.Version).Raw(mod.Hash).Varint((ulong)mod.Size);
        }
        w.Bool(Map is not null);
        if (Map is { } map)
            w.SVarint(map.Left).SVarint(map.Top).SVarint(map.Right).SVarint(map.Bottom);
        w.Bool(Fog);
        w.Bool(Land is not null);
        if (Land is { } land)
            WriteGenerator(w, land);
        w.Varint((ulong)Components.Count);
        foreach (var component in Components)
        {
            w.Text(component.Name).Varint((ulong)component.Fields.Count);
            foreach (var field in component.Fields)
            {
                w.Text(field.Name).Byte((byte)field.Kind);
                switch (field.Kind)
                {
                    case FieldKind.Number: w.F64(field.Default.Number).F64(field.Scale); break;
                    case FieldKind.Flag: w.Bool(field.Default.Flag); break;
                    case FieldKind.String: w.Text(field.Default.Text ?? ""); break;
                }
            }
        }
        w.Varint((ulong)Layers.Count);
        foreach (var layer in Layers)
        {
            w.Text(layer.Name).Byte((byte)layer.Scope).Varint((ulong)layer.Channels.Count);
            foreach (var channel in layer.Channels)
                w.Byte((byte)channel);
            w.Varint((ulong)layer.Cell).Varint((ulong)layer.Fields.Count);
            foreach (var (name, channel) in layer.Fields)
                w.Text(name).Byte((byte)channel);
            w.Byte((byte)((layer.Save ? 1 : 0) | (layer.Send ? 2 : 0) | (layer.Remember ? 4 : 0) | (layer.Allies ? 8 : 0)));
        }
        return w.ToArray();
    }

    /// <summary>Разбор; не приветствие или мусор — <see cref="FormatException"/>.</summary>
    public static Welcome Decode(ReadOnlySpan<byte> message)
    {
        var r = new WireReader(message);
        if (r.Byte() != (byte)MessageType.Welcome)
            throw new FormatException("не приветствие");
        var version = r.Int();
        if (version != ProtocolVersion)
            throw new FormatException($"протокол хоста {version}, клиента {ProtocolVersion}");
        var player = r.U32();
        var token = r.Text();
        var name = r.Text();
        var game = r.Text();
        var seed = r.Varint();
        var dt = r.F64();
        var tick = r.Varint();
        var checksum = r.Blob().ToArray();
        var mods = new ModRef[r.Count(Messages.HashSize)];
        for (var at = 0; at < mods.Length; at++)
            mods[at] = new ModRef(r.Text(), r.Text(), r.Take(Messages.HashSize).ToArray(), r.Int());
        MapBounds? map = r.Bool() ? new MapBounds((int)r.SVarint(), (int)r.SVarint(), (int)r.SVarint(), (int)r.SVarint()) : null;
        var fog = r.Bool();
        var land = r.Bool() ? ReadGenerator(ref r) : null;
        var components = new ComponentInfo[r.Count()];
        for (var id = 0; id < components.Length; id++)
        {
            var componentName = r.Text();
            var fields = new FieldInfo[r.Count()];
            for (var at = 0; at < fields.Length; at++)
            {
                var fieldName = r.Text();
                var kind = (FieldKind)r.Byte();
                var value = kind switch
                {
                    FieldKind.Number => FieldValue.Of(r.F64()),
                    FieldKind.Flag => FieldValue.Of(r.Bool()),
                    FieldKind.String => FieldValue.Of(r.Text()),
                    FieldKind.Bytes => FieldValue.Of([]),
                    _ => throw new FormatException($"{componentName}.{fieldName}: вид {(int)kind}"),
                };
                var scale = kind == FieldKind.Number ? r.F64() : 0;
                if (!(scale >= 0 && double.IsFinite(scale)))
                    throw new FormatException($"{componentName}.{fieldName}: точность {scale}");
                fields[at] = new FieldInfo(fieldName, kind, value, scale);
            }
            components[id] = new ComponentInfo(componentName, fields);
        }
        var layers = new LayerSpec[r.Count()];
        for (var at = 0; at < layers.Length; at++)
        {
            var layerName = r.Text();
            var scope = (LayerScope)r.Byte();
            var channels = new Channel[r.Count()];
            for (var c = 0; c < channels.Length; c++)
                channels[c] = ChannelOf(r.Byte());
            var cell = r.Int();
            var fields = new (string, Channel)[r.Count()];
            for (var f = 0; f < fields.Length; f++)
                fields[f] = (r.Text(), ChannelOf(r.Byte()));
            var flags = r.Byte();
            layers[at] = new LayerSpec
            {
                Name = layerName,
                Scope = scope <= LayerScope.Player ? scope : throw new FormatException($"{layerName}: scope {(int)scope}"),
                Channels = channels,
                Cell = cell,
                Fields = fields,
                Save = (flags & 1) != 0,
                Send = (flags & 2) != 0,
                Remember = (flags & 4) != 0,
                Allies = (flags & 8) != 0,
            };
        }
        if (!r.End)
            throw new FormatException("лишние байты в приветствии");
        return new Welcome
        {
            Version = version, Player = player, Token = token, Name = name, Game = game, Seed = seed, Dt = dt, Tick = tick, Checksum = checksum, Mods = mods,
            Map = map, Fog = fog, Land = land, Components = components, Layers = layers,
        };
    }

    private static Channel ChannelOf(byte value) => value <= (byte)Channel.F64 ? (Channel)value : throw new FormatException($"канал {value}");

    private static void WriteGenerator(WireWriter w, GeneratorConfig c)
    {
        w.SVarint(c.Seed);
        foreach (var value in Numbers(c))
            w.F64(value);
        w.Bool(c.Blank);
    }

    private static double[] Numbers(GeneratorConfig c) =>
    [
        c.ZoneScale, c.ZoneWarp, c.SwampLevel, c.RockLevel, c.BiomeScale, c.PeakChance, c.PeakMinRadius, c.PeakMaxRadius,
        c.DoubleChance, c.TripleChance, c.SaltSwampShift, c.SaltRockShift, c.SaltPeakFactor, c.RedSwampShift, c.RedRockShift,
        c.RedPeakFactor, c.MarshSwampShift, c.MarshRockShift, c.MarshPeakFactor, c.TierStep, c.CliffScale, c.CliffShare,
        c.DepositChance, c.DepositRichness,
    ];

    private static GeneratorConfig ReadGenerator(ref WireReader r)
    {
        var seed = (int)r.SVarint();
        Span<double> n = stackalloc double[24];
        for (var at = 0; at < n.Length; at++)
            n[at] = r.F64();
        return new GeneratorConfig
        {
            Seed = seed, ZoneScale = n[0], ZoneWarp = n[1], SwampLevel = n[2], RockLevel = n[3], BiomeScale = n[4],
            PeakChance = n[5], PeakMinRadius = n[6], PeakMaxRadius = n[7], DoubleChance = n[8], TripleChance = n[9],
            SaltSwampShift = n[10], SaltRockShift = n[11], SaltPeakFactor = n[12], RedSwampShift = n[13], RedRockShift = n[14],
            RedPeakFactor = n[15], MarshSwampShift = n[16], MarshRockShift = n[17], MarshPeakFactor = n[18], TierStep = n[19],
            CliffScale = n[20], CliffShare = n[21], DepositChance = n[22], DepositRichness = n[23], Blank = r.Bool(),
        };
    }
}
