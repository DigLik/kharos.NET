using System.Text.Json;
using Kharos.Engine.World;

namespace Kharos.Engine.Movement;

/// <summary>
/// Ходовая (запись moves.json, docs/movement.md §2): слой и доли скорости по местности. Нынешние классы TS — legs
/// (пехота), wheels, tracks, air; правила местности TS (vehicleSand, infantryCliff, frozenSwamp…) уже сложены в числа.
/// </summary>
public sealed class Chassis
{
    /// <summary>Медленнее этой доли скорости местность не замедляет (SLOWEST в units.ts).</summary>
    public const double DefaultSlowest = 0.05;

    public string Name { get; init; } = "";

    /// <summary>Летает: местность, здания и наземные ему не мешают.</summary>
    public bool Air { get; init; }

    // Доли скорости по виду земли: rock, sand, swamp; brine — болото солончаков, toxic — красных пустошей; cliff —
    // подножие обрыва (0 — туда нельзя), mountain — гора.
    public double Rock { get; init; } = 1;
    public double Sand { get; init; } = 1;
    public double Swamp { get; init; } = 1;
    public double Brine { get; init; } = 1;
    public double Toxic { get; init; } = 1;
    public double Cliff { get; init; }
    public double Mountain { get; init; }

    /// <summary>Есть ли множители покрытия (paved); нет — покрытие ходовой безразлично.</summary>
    public bool Paved { get; init; }
    public double Road { get; init; } = 1;
    public double Foundation { get; init; } = 1;

    public double Slowest { get; init; } = DefaultSlowest;

    /// <summary>Пехота: можно к подножию обрыва (TS onFoot).</summary>
    public bool Foot => Cliff > 0;

    /// <summary>Самая высокая доля скорости, какая бывает (TS fastestOf): по ней поиск оценивает, сколько ещё ехать.</summary>
    public double Fastest => Air || !Paved ? 1 : Math.Max(1, Math.Max(Road, Foundation));

    /// <summary>Наземная ходовая без записи: везде как по скале, к подножию обрыва и в горы — нельзя.</summary>
    public static Chassis Ground { get; } = new() { Name = "" };
}

/// <summary>Ходовые мира по имени и по номеру строки Mover.chassis (кэш: строки мира не меняют номеров).</summary>
public sealed class ChassisTable
{
    private readonly Dictionary<string, Chassis> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, Chassis> _byString = [];

    public ChassisTable(IEnumerable<Chassis> chassis)
    {
        foreach (var item in chassis)
            _byName[item.Name] = item;
    }

    /// <summary>Пустая таблица (кэш по номерам строк — свой у каждого мира, поэтому новая каждый раз).</summary>
    public static ChassisTable Empty => new([]);

    public IEnumerable<Chassis> All => _byName.Values;

    /// <summary>Ходовая по имени; нет — <see cref="Chassis.Ground"/>.</summary>
    public Chassis Of(string name) => _byName.GetValueOrDefault(name) ?? Chassis.Ground;

    /// <summary>Ходовая по номеру строки (поле Mover.chassis).</summary>
    public Chassis Of(GameWorld world, double stringId)
    {
        var id = (uint)stringId;
        if (_byString.TryGetValue(id, out var found))
            return found;
        found = Of(id == 0 ? "" : world.Strings.Text(id));
        _byString[id] = found;
        return found;
    }

    /// <summary>Ходовые из moves.json: { имя: { layer, terrain {…}, paved {road, foundation}, slowest } }.</summary>
    public static ChassisTable Parse(JsonElement moves)
    {
        var list = new List<Chassis>();
        if (moves.ValueKind != JsonValueKind.Object)
            return new ChassisTable(list);
        foreach (var entry in moves.EnumerateObject())
        {
            var record = entry.Value;
            if (record.ValueKind != JsonValueKind.Object)
                continue;
            var terrain = Child(record, "terrain");
            var paved = Child(record, "paved");
            double Share(JsonElement? table, string name, double fallback) =>
                table is { } value && value.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number ? number.GetDouble() : fallback;
            list.Add(new Chassis
            {
                Name = entry.Name,
                Air = record.TryGetProperty("layer", out var layer) && layer.ValueKind == JsonValueKind.String && layer.GetString() == "air",
                Rock = Share(terrain, "rock", 1),
                Sand = Share(terrain, "sand", 1),
                Swamp = Share(terrain, "swamp", 1),
                Brine = Share(terrain, "brine", Share(terrain, "swamp", 1)),
                Toxic = Share(terrain, "toxic", Share(terrain, "swamp", 1)),
                Cliff = Share(terrain, "cliff", 0),
                Mountain = Share(terrain, "mountain", 0),
                Paved = paved is not null,
                Road = Share(paved, "road", 1),
                Foundation = Share(paved, "foundation", 1),
                Slowest = record.TryGetProperty("slowest", out var slowest) && slowest.ValueKind == JsonValueKind.Number ? slowest.GetDouble() : Chassis.DefaultSlowest,
            });
        }
        return new ChassisTable(list);
    }

    private static JsonElement? Child(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;
}
