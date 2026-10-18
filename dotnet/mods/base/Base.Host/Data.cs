using System.Text.Json;
using Kharos.Mod.Sdk;

namespace Base.Host;

/// <summary>Турель на носителе: вид из turrets/, место вперёд и вправо от центра (тайлы).</summary>
internal sealed record Mount(string Turret, double Along, double Across);

/// <summary>Вид юнита (data/units.json): то, что читают производство, сборка сущности и приказы.</summary>
internal sealed record UnitKind(string Name, double Speed, double Turn, double Radius, double Cost, double Time, double Sight);

/// <summary>Вид здания (data/buildings.json).</summary>
internal sealed record BuildingKind(string Name, double Width, double Height, string[] Produces, double Sight, double ExitX, double ExitY);

/// <summary>Вид месторождения (data/deposits.json): запас от Min до Max, доли вида по биомам (эрг, солончаки, красные пустоши, топи).</summary>
internal sealed record DepositKind(string Name, double Min, double Max, double[] Biomes);

/// <summary>Партия (data/games.json): стартовые кредиты и набор, места игроков, поле и местность.</summary>
internal sealed record GameRules(string Name, double Credits, (string Kind, int Count)[] Start, double Radius, double Size, string? Land);

/// <summary>Местность (data/lands.json): что нужно раскладке месторождений.</summary>
internal sealed record LandRules(string Name, double Seed, double DepositChance, double DepositRichness);

/// <summary>
/// Данные мода из data/*.json после патчей (docs/dotnet.md, Р12) — то, что в пилоте лежало в <c>data</c> видов.
/// Читается раз при загрузке; правка JSON другим модом доходит до правил без пересборки.
/// </summary>
internal sealed class BaseData
{
    public Dictionary<string, UnitKind> Units { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, BuildingKind> Buildings { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, DepositKind> Deposits { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, GameRules> Games { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, LandRules> Lands { get; } = new(StringComparer.Ordinal);

    /// <summary>Турели на видах по «раздел/имя» (data.mounts пилота).</summary>
    public Dictionary<string, Mount[]> Mounts { get; } = new(StringComparer.Ordinal);

    /// <summary>mechanics/crowd.strength — доля перекрытия корпусов, снимаемая за тик.</summary>
    public double CrowdStrength { get; private set; } = 0.5;

    /// <summary>mechanics/orders.spacing — шаг мест группы, тайлы.</summary>
    public double Spacing { get; private set; } = 1.2;

    /// <summary>mechanics/players.income — доход игрока, кредитов в секунду.</summary>
    public double Income { get; private set; } = 5;

    /// <summary>mechanics/production.queue — сколько заказов держит завод (текущий и очередь).</summary>
    public int QueueLimit { get; private set; } = 5;

    public static BaseData Load(LoadContext ctx)
    {
        var data = new BaseData();
        foreach (var (name, record) in Records(ctx, "units"))
        {
            data.Units[name] = new UnitKind(name, Num(record, "speed"), Num(record, "turn"), Num(record, "radius", 0.5), Num(record, "cost"), Num(record, "time"), Num(record, "sight"));
            data.Mounts["units/" + name] = MountsOf(record);
        }
        foreach (var (name, record) in Records(ctx, "buildings"))
        {
            var exit = record.TryGetProperty("exit", out var e) && e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 2
                ? (e[0].GetDouble(), e[1].GetDouble())
                : (0.0, 1.0);
            data.Buildings[name] = new BuildingKind(name, Num(record, "width", 1), Num(record, "height", 1), Strings(record, "produces"), Num(record, "sight"), exit.Item1, exit.Item2);
            data.Mounts["buildings/" + name] = MountsOf(record);
        }
        foreach (var (name, record) in Records(ctx, "deposits"))
        {
            var biomes = new double[4];
            if (record.TryGetProperty("biomes", out var shares) && shares.ValueKind == JsonValueKind.Object)
                for (var biome = 0; biome < biomes.Length; biome++)
                    biomes[biome] = Num(shares, DepositMap.Biomes[biome]);
            data.Deposits[name] = new DepositKind(name, Num(record, "min"), Num(record, "max"), biomes);
        }
        foreach (var (name, record) in Records(ctx, "games"))
        {
            var start = new List<(string, int)>();
            if (record.TryGetProperty("start", out var kinds) && kinds.ValueKind == JsonValueKind.Object)
                foreach (var kind in kinds.EnumerateObject())
                    start.Add((kind.Name, (int)kind.Value.GetDouble()));
            var land = record.TryGetProperty("land", out var landName) && landName.ValueKind == JsonValueKind.String ? landName.GetString() : null;
            data.Games[name] = new GameRules(name, Num(record, "credits"), [.. start], Num(record, "radius", 24), Num(record, "size"), land);
        }
        foreach (var (name, record) in Records(ctx, "lands"))
            data.Lands[name] = new LandRules(name, Num(record, "seed"), Num(record, "depositChance", DepositMap.Chance), Num(record, "depositRichness", 1));
        foreach (var (name, record) in Records(ctx, "mechanics"))
            switch (name)
            {
                case "crowd": data.CrowdStrength = Num(record, "strength", 0.5); break;
                case "orders": data.Spacing = Num(record, "spacing", 1.2); break;
                case "players": data.Income = Num(record, "income", 5); break;
                case "production": data.QueueLimit = (int)Num(record, "queue", 5); break;
            }
        return data;
    }

    /// <summary>Записи-объекты файла данных (виды раздела) по порядку файла.</summary>
    private static List<(string Name, JsonElement Record)> Records(LoadContext ctx, string section)
    {
        var records = new List<(string, JsonElement)>();
        if (!ctx.TryData(section, out var bytes))
            return records;
        using var document = JsonDocument.Parse(bytes.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return records;
        foreach (var property in document.RootElement.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Object)
                records.Add((property.Name, property.Value.Clone()));
        return records;
    }

    private static double Num(JsonElement record, string name, double fallback = 0) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;

    private static string[] Strings(JsonElement record, string name)
    {
        if (!record.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        return [.. list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)];
    }

    private static Mount[] MountsOf(JsonElement record)
    {
        if (!record.TryGetProperty("mounts", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        return [.. list.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("turret", out var t) && t.ValueKind == JsonValueKind.String)
            .Select(item => new Mount(item.GetProperty("turret").GetString()!, Num(item, "along"), Num(item, "across")))];
    }
}
