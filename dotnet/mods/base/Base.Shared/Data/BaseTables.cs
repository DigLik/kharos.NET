using System.Text.Json;
using System.Text.Json.Serialization;

namespace Base.Shared.Data;

/// <summary>
/// Генерация кода JSON для таблиц (AOT, без отражения): имена полей — camelCase, как в TS. Свойства записей — set, не
/// init: с init генератор ставит полю, которого нет в JSON, default вместо значения по умолчанию из класса.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Dictionary<string, UnitSpec>))]
[JsonSerializable(typeof(Dictionary<string, BuildingSpec>))]
[JsonSerializable(typeof(Dictionary<string, WeaponSpec>))]
[JsonSerializable(typeof(Dictionary<string, TurretSpec>))]
[JsonSerializable(typeof(Dictionary<string, ResourceSpec>))]
[JsonSerializable(typeof(Dictionary<string, OreSpec>))]
[JsonSerializable(typeof(Dictionary<string, ProductSpec>))]
[JsonSerializable(typeof(Dictionary<string, TopicSpec>))]
[JsonSerializable(typeof(Dictionary<string, DepositSpec>))]
[JsonSerializable(typeof(Dictionary<string, RewardSpec>))]
[JsonSerializable(typeof(Dictionary<string, LayerRecord>))]
[JsonSerializable(typeof(Dictionary<string, RulesSpec>))]
[JsonSerializable(typeof(Dictionary<string, ChassisSpec>))]
[JsonSerializable(typeof(Dictionary<string, GameSpec>))]
[JsonSerializable(typeof(Dictionary<string, LandSpec>))]
public sealed partial class BaseJson : JsonSerializerContext;

/// <summary>
/// Таблицы base (data/*.json после патчей модов): «имя вида → запись», порядок записей — порядок видов в игре.
/// Константы модулей TS остаются константами C# (Base.Host); здесь — только данные.
/// </summary>
public sealed class BaseTables
{
    /// <summary>Файлы данных base по разделам (имя файла без .json).</summary>
    public static readonly string[] Files =
        ["units", "buildings", "weapons", "turrets", "resources", "ores", "products", "research", "deposits", "rewards", "layers", "rules", "moves", "games", "lands"];

    public Dictionary<string, UnitSpec> Units { get; private init; } = [];
    public Dictionary<string, BuildingSpec> Buildings { get; private init; } = [];
    public Dictionary<string, WeaponSpec> Weapons { get; private init; } = [];
    public Dictionary<string, TurretSpec> Turrets { get; private init; } = [];
    public Dictionary<string, ResourceSpec> Resources { get; private init; } = [];
    public Dictionary<string, OreSpec> Ores { get; private init; } = [];
    public Dictionary<string, ProductSpec> Products { get; private init; } = [];
    public Dictionary<string, TopicSpec> Research { get; private init; } = [];
    public Dictionary<string, DepositSpec> Deposits { get; private init; } = [];
    public Dictionary<string, RewardSpec> Rewards { get; private init; } = [];
    public Dictionary<string, LayerRecord> Layers { get; private init; } = [];
    public Dictionary<string, ChassisSpec> Moves { get; private init; } = [];
    public Dictionary<string, GameSpec> Games { get; private init; } = [];
    public Dictionary<string, LandSpec> Lands { get; private init; } = [];

    /// <summary>Правила по умолчанию: rules.json, запись default.</summary>
    public RulesSpec Rules { get; private init; } = new();

    /// <summary>Грузы по порядку столбцов Stock: ресурсы, изделия, руда.</summary>
    public IEnumerable<string> Goods => Resources.Keys.Concat(Products.Keys).Concat(Ores.Keys);

    /// <summary>
    /// Таблицы из файлов: <paramref name="file"/> даёт байты data/&lt;имя&gt;.json или null (нет файла — пустая
    /// таблица). Негодный JSON — <see cref="JsonException"/>.
    /// </summary>
    public static BaseTables Load(Func<string, ReadOnlyMemory<byte>?> file)
    {
        var json = BaseJson.Default;
        Dictionary<string, T> Read<T>(string name, System.Text.Json.Serialization.Metadata.JsonTypeInfo<Dictionary<string, T>> info) =>
            file(name) is { } bytes ? JsonSerializer.Deserialize(bytes.Span, info) ?? [] : [];
        return new BaseTables
        {
            Units = Read("units", json.DictionaryStringUnitSpec),
            Buildings = Read("buildings", json.DictionaryStringBuildingSpec),
            Weapons = Read("weapons", json.DictionaryStringWeaponSpec),
            Turrets = Read("turrets", json.DictionaryStringTurretSpec),
            Resources = Read("resources", json.DictionaryStringResourceSpec),
            Ores = Read("ores", json.DictionaryStringOreSpec),
            Products = Read("products", json.DictionaryStringProductSpec),
            Research = Read("research", json.DictionaryStringTopicSpec),
            Deposits = Read("deposits", json.DictionaryStringDepositSpec),
            Rewards = Read("rewards", json.DictionaryStringRewardSpec),
            Layers = Read("layers", json.DictionaryStringLayerRecord),
            Moves = Read("moves", json.DictionaryStringChassisSpec),
            Games = Read("games", json.DictionaryStringGameSpec),
            Lands = Read("lands", json.DictionaryStringLandSpec),
            Rules = Read("rules", json.DictionaryStringRulesSpec).GetValueOrDefault("default") ?? new RulesSpec(),
        };
    }
}
