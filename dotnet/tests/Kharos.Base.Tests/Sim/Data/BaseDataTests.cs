using System.Globalization;
using System.Text.Json;
using Base.Shared.Data;
using Kharos.Content;

namespace Kharos.Base.Tests.Data;

/// <summary>
/// Перенос packages/tests/base.test.ts: данные мода base (mods/base/data) без проверки типов — у каждой записи только
/// известные поля нужного вида, ссылки называют существующие записи, записи родов держат свои правила. К таблицам TS
/// добавлены таблицы .NET (rules, moves, games, lands), ходовая юнита и облик пилота (look).
/// </summary>
public sealed class BaseDataTests
{
    // ===== Вид поля (Field в base.test.ts) =====

    private abstract record Field;
    private sealed record Kind(JsonValueKind Type, string Word) : Field;
    private sealed record OneOf(string[] Values) : Field;
    private sealed record ListOf(Field Item) : Field;
    private sealed record RecordOf(string[] Keys, Field Of) : Field;
    private sealed record MapOf(Field Of) : Field;
    private sealed record Shape((string Key, Field Field)[] Fields, string[] Optional) : Field;

    private static readonly Field Number = new Kind(JsonValueKind.Number, "number");
    private static readonly Field Boolean = new Kind(JsonValueKind.True, "boolean");
    private static readonly Field Text = new Kind(JsonValueKind.String, "string");

    private static Shape Of(params (string, Field)[] fields) => new(fields, []);

    /// <summary>Данные base после разбора (без чужих модов): файл → корень.</summary>
    private static readonly Lazy<GameContent> Content = new(BaseGame.Content);

    private static JsonElement Table(string name) => Content.Value.Data(name) ?? throw new InvalidOperationException($"нет data/{name}.json");

    private static string[] Names(string table) => [.. Table(table).EnumerateObject().Select(p => p.Name)];

    private static BaseTables Tables() => BaseTables.Load(name => Content.Value.DataBytes(name));

    /// <summary>Поля записей каждой таблицы: обязательные — без «?».</summary>
    private static Dictionary<string, (string Key, Field Field)[]> Schema()
    {
        string[] goods = [.. Names("resources"), .. Names("products"), .. Names("ores")];
        string[] wares = [.. Names("resources"), .. Names("products")];
        string[] armor = ["infantry", "vehicle", "heavy", "air", "building"];
        Field Amounts(string[]? of = null) => new RecordOf(of ?? goods, Number);
        var channel = new OneOf(["u8", "u16", "u32", "f32", "f64"]);
        var beam = new Shape([("radius", Number), ("rate", Number), ("give", Boolean), ("take", Boolean)], ["give", "take"]);
        var mounts = new ListOf(Of(("turret", new OneOf(Names("turrets"))), ("along", Number), ("across", Number)));
        // Облик пилота для подачи: игре не нужен.
        var look = new Shape([("title", Text), ("motor", Text), ("pitch", Number), ("exit", new ListOf(Number)), ("color", Number)], ["title", "motor", "pitch", "exit", "color"]);
        string[] terrain = ["rock", "sand", "swamp", "brine", "toxic", "cliff", "mountain"];
        return new()
        {
            ["units"] =
            [
                ("speed", Number), ("turn", Number), ("radius", Number), ("cost", Number), ("buildTime", Number), ("kind", new OneOf(armor[..4])), ("hp", Number), ("supply", Number),
                ("chassis", new OneOf(Names("moves"))),
                ("weapon?", new OneOf(Names("weapons"))), ("repair?", Number), ("mounts?", mounts), ("materials?", Amounts(wares)), ("requires?", new ListOf(new OneOf(Names("buildings")))),
                ("research?", new OneOf(Names("research"))), ("inventory?", Number), ("accepts?", new ListOf(new OneOf(goods))), ("hauls?", Boolean), ("beam?", beam), ("sight?", Number),
                ("ammo?", Number), ("supplies?", Of(("radius", Number), ("rate", Number))), ("look?", look),
            ],
            ["buildings"] =
            [
                ("width", Number), ("height", Number), ("cost", Number), ("zone?", Number), ("expand?", Number), ("defense?", Boolean), ("hp?", Number), ("mounts?", mounts), ("sight?", Number),
                ("gate?", Boolean), ("scan?", Number), ("power?", Number), ("extract?", Boolean), ("materials?", Amounts(wares)), ("inventory?", Number), ("accepts?", new ListOf(new OneOf(goods))),
                ("limits?", Amounts()), ("stores?", Boolean), ("refines?", new OneOf(Names("ores"))), ("assembles?", new OneOf(Names("products"))), ("ammo?", Boolean), ("beam?", beam), ("depot?", Boolean),
                ("rearms?", Boolean), ("requester?", Boolean), ("trades?", Boolean), ("produces?", new ListOf(new OneOf(Names("units")))), ("income?", Number), ("crowding?", Boolean),
                ("repair?", Number), ("pipe?", new OneOf(["surface", "well"])), ("isolated?", Boolean), ("researches?", Boolean),
                ("shield?", Of(("radius", Number), ("capacity", Number), ("regen", Number))), ("battery?", Of(("capacity", Number), ("rate", Number))),
                ("burns?", Of(("good", new OneOf(Names("products"))), ("seconds", Number))), ("meltdown?", Of(("radius", Number), ("damage", Number))),
                ("warhead?", Of(("cost", Number), ("materials", Amounts(wares)), ("seconds", Number), ("launch", Number), ("speed", Number), ("blast", Of(("radius", Number), ("core", Number), ("damage", Number))))),
                ("intercept?", Of(("radius", Number), ("cost", Number), ("materials", Amounts(wares)), ("seconds", Number), ("speed", Number), ("capacity", Number))),
                ("requires?", new ListOf(new OneOf(Names("buildings")))), ("research?", new OneOf(Names("research"))), ("menu?", Number), ("look?", look),
            ],
            ["weapons"] =
            [
                ("shot", new OneOf(["bullet", "shell", "rocket", "laser", "arc", "flame"])), ("range", Number), ("damage", Number), ("reload", Number), ("air", Boolean), ("speed?", Number),
                ("splash?", Number), ("chain?", Number), ("minRange?", Number), ("lob?", Boolean), ("onlyAir?", Boolean), ("vs?", new RecordOf(armor, Number)), ("ammo?", Number),
            ],
            ["turrets"] = [("turn", Number), ("radius", Number), ("weapon?", new OneOf(Names("weapons"))), ("repair?", Number), ("look?", look)],
            ["resources"] = [("price", Number), ("mined?", Boolean), ("research?", new OneOf(Names("research")))],
            ["ores"] = [("resource", new OneOf(Names("resources")))],
            ["products"] = [("recipe", Amounts(Names("resources"))), ("yield", Number)],
            ["research"] =
            [
                ("after?", new ListOf(new OneOf(Names("research")))), ("uses?", Amounts()),
                ("trigger?", new OneOf(["lab", "deposit", "metal", "silicon", "fuel", "kharite", "lithium", "uranium", "refined"])),
            ],
            ["deposits"] = [("min", Number), ("max", Number), ("rate", Number), ("biomes", new RecordOf(["erg", "saltFlats", "redWastes", "marsh"], Number)), ("look?", look)],
            ["rewards"] = [("credits", Number)],
            // Слои (docs/layers.md); их же разбирает и проверяет движок, LayerSpec.ParseAll.
            ["layers"] =
            [
                ("scope", new OneOf(["world", "player"])), ("shape", new OneOf(["grid", "items"])), ("channels?", new ListOf(channel)), ("cell?", Number), ("fields?", new MapOf(channel)),
                ("save?", Boolean), ("send?", Boolean), ("remember?", Boolean), ("allies?", Boolean), ("sight?", Boolean),
            ],
            // Таблицы .NET: правила мира, ходовые, партии, местности.
            ["rules"] =
            [
                ("repairSpeed", Number), ("repairCost", Number), ("repairPause", Number), ("techTree", Boolean), ("offlineIncome", Number), ("offlineMinutes", Number),
                ("protection", Number), ("researchScale", Number), ("researchGrowth", Number), ("researchSlow", Number), ("upgradeScale", Number), ("depositRenewal", Number),
                ("armyLimit", Number), ("unitAmmo", Boolean),
            ],
            ["moves"] =
            [
                ("layer", new OneOf(["ground", "air"])), ("terrain?", new RecordOf(terrain, Number)), ("harm?", new RecordOf(terrain, Number)),
                ("paved?", new RecordOf(["road", "foundation"], Number)), ("slowest?", Number), ("steer", new OneOf(["pivot", "arc", "free"])),
            ],
            ["games"] =
            [
                ("title?", Text), ("size?", Number), ("fog?", Boolean), ("land?", new OneOf(Names("lands"))), ("start?", Boolean), ("sandbox?", Boolean), ("credits?", Number),
                ("units?", new ListOf(new OneOf(Names("units")))),
                ("spawn?", Of(("apart", Number), ("tries", Number), ("area", Number), ("rock", Number), ("margin", Number), ("radius", Number), ("slots", Number))),
                ("remember?", new ListOf(Text)),
                ("client?", new Shape([("camera", Text), ("controls", new ListOf(Text)), ("ui", new ListOf(Text)), ("atmosphere", new ListOf(Text)), ("effects", new ListOf(Text))], ["camera", "controls", "ui", "atmosphere", "effects"])),
            ],
            ["lands"] =
            [
                ("seed", Number), ("zoneScale", Number), ("zoneWarp", Number), ("swampLevel", Number), ("rockLevel", Number), ("biomeScale", Number), ("peakChance", Number),
                ("peakMinRadius", Number), ("peakMaxRadius", Number), ("doubleChance", Number), ("tripleChance", Number), ("saltSwampShift", Number), ("saltRockShift", Number),
                ("saltPeakFactor", Number), ("redSwampShift", Number), ("redRockShift", Number), ("redPeakFactor", Number), ("marshSwampShift", Number), ("marshRockShift", Number),
                ("marshPeakFactor", Number), ("tierStep", Number), ("cliffScale", Number), ("cliffShare", Number), ("depositChance", Number), ("depositRichness", Number), ("blank?", Boolean),
            ],
        };
    }

    /// <summary>Что не так со значением: пустой список — всё так.</summary>
    private static IEnumerable<string> Problems(JsonElement value, Field field, string at)
    {
        switch (field)
        {
            case Kind kind:
                var fits = kind.Type == JsonValueKind.True ? value.ValueKind is JsonValueKind.True or JsonValueKind.False : value.ValueKind == kind.Type;
                return fits ? [] : [$"{at}: не {kind.Word}"];
            case OneOf one:
                return value.ValueKind == JsonValueKind.String && one.Values.Contains(value.GetString()) ? [] : [$"{at}: {value.GetRawText()} — нет такого"];
            case MapOf map:
                return value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().SelectMany(p => Problems(p.Value, map.Of, $"{at}.{p.Name}")).ToList() : [$"{at}: не объект"];
            case ListOf list:
                return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().SelectMany((item, i) => Problems(item, list.Item, $"{at}[{i}]")).ToList() : [$"{at}: не список"];
        }
        if (value.ValueKind != JsonValueKind.Object)
            return [$"{at}: не объект"];
        if (field is RecordOf record)
            return value.EnumerateObject().SelectMany(p => (record.Keys.Contains(p.Name) ? [] : new[] { $"{at}.{p.Name}: нет такого" }).Concat(Problems(p.Value, record.Of, $"{at}.{p.Name}"))).ToList();
        var shape = (Shape)field;
        return Fields(value, shape.Fields, at, shape.Optional);
    }

    private static List<string> Fields(JsonElement record, (string Key, Field Field)[] shape, string at, string[]? optional = null)
    {
        var output = new List<string>();
        var known = shape.ToDictionary(
            entry => entry.Key.TrimEnd('?'),
            entry => (entry.Field, Optional: entry.Key.EndsWith('?') || (optional ?? []).Contains(entry.Key)));
        foreach (var property in record.EnumerateObject())
        {
            if (property.Name == "about")
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    output.Add($"{at}.about: не строка");
                continue;
            }
            if (!known.TryGetValue(property.Name, out var spec))
                output.Add($"{at}.{property.Name}: неизвестное поле");
            else
                output.AddRange(Problems(property.Value, spec.Field, $"{at}.{property.Name}"));
        }
        foreach (var (key, spec) in known)
            if (!spec.Optional && !record.TryGetProperty(key, out _))
                output.Add($"{at}.{key}: нет поля");
        return output;
    }

    private static string Show(IEnumerable<string> list) => "[" + string.Join(", ", list) + "]";

    private static string Show(IReadOnlyDictionary<string, double> amounts) =>
        "{" + string.Join(", ", amounts.Select(pair => $"{pair.Key}: {pair.Value.ToString(CultureInfo.InvariantCulture)}")) + "}";

    [Fact(DisplayName = "у записей данных только известные поля нужного вида, а ссылки называют существующие записи")]
    public void RecordsHaveOnlyKnownFieldsAndReferencesNameExistingRecords()
    {
        var output = new List<string>();
        foreach (var (name, shape) in Schema())
            foreach (var record in Table(name).EnumerateObject())
                output.AddRange(Fields(record.Value, shape, $"{name}.{record.Name}"));
        Assert.Empty(output);
        // Всё, что есть в data, проверено: новой таблице — своя строка в схеме.
        Assert.Equal(Schema().Keys.Order(StringComparer.Ordinal), Content.Value.DataNames.Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "переработка, цеха, хранилища и турели с патронами держат правила своего рода")]
    public void RefineriesWorkshopsStoresAndAmmoTurretsKeepTheirRules()
    {
        // PIPE_REACH (buildings.ts) и STOCKED_CYCLES (resources.ts).
        const double pipeReach = 3, stockedCycles = 4;
        var tables = Tables();
        foreach (var (type, spec) in tables.Buildings)
        {
            if (spec.Refines is { } ore)
            {
                // Склад переработки — два места по 30: под руду и под готовое.
                var resource = tables.Ores[ore].Resource;
                Assert.Equal(
                    $"{type}: {Show([ore, resource])} {Show(new Dictionary<string, double> { [ore] = 30, [resource] = 30 })} 60",
                    $"{type}: {Show(spec.Accepts)} {Show(spec.Limits)} {spec.Inventory}");
            }
            if (spec.Assembles is { } product)
            {
                // Цех держит сырьё на четыре сборки и 30 готового.
                var limits = tables.Products[product].Recipe.ToDictionary(pair => pair.Key, pair => pair.Value * stockedCycles);
                limits[product] = 30;
                Assert.Equal(
                    $"{type}: {Show(limits)} {Show(limits.Keys)} {limits.Values.Sum().ToString(CultureInfo.InvariantCulture)}",
                    $"{type}: {Show(spec.Limits)} {Show(spec.Accepts)} {spec.Inventory?.ToString(CultureInfo.InvariantCulture)}");
            }
            if (spec.Stores)
                Assert.Equal($"{type}: 1", $"{type}: {spec.Accepts.Length}");
            if (spec.Pipe is not null)
                Assert.Equal($"{type}: {pipeReach}", $"{type}: {spec.Expand}");
            if (spec.Ammo)
                Assert.Equal($"{type}: 120 [ammo]", $"{type}: {spec.Inventory} {Show(spec.Accepts)}");
        }
    }

    [Fact(DisplayName = "места в меню строителя не повторяются; у каждого ресурса — своя руда")]
    public void BuilderMenuPlacesAreUniqueAndEveryResourceHasItsOre()
    {
        var tables = Tables();
        var menu = tables.Buildings.Values.Where(spec => spec.Menu is not null).Select(spec => spec.Menu!.Value).ToList();
        Assert.Equal(menu.Count, menu.Distinct().Count());
        Assert.Equal(tables.Resources.Keys.Order(StringComparer.Ordinal), tables.Ores.Values.Select(ore => ore.Resource).Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "доли месторождений в каждом биоме дают в сумме единицу")]
    public void DepositSharesInEveryBiomeSumToOne()
    {
        var tables = Tables();
        foreach (var biome in (string[])["erg", "saltFlats", "redWastes", "marsh"])
            Assert.Equal(1, tables.Deposits.Values.Sum(spec => spec.Biomes[biome]), 9);
    }
}
