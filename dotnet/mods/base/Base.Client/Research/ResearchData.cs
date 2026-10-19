using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Base.Shared.Data;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;

namespace Base.Client;

/// <summary>
/// Данные дерева технологий на клиенте (research.json, правила партии): те же таблицы и правила, что у хоста
/// (Core.Rules: запись default из rules.json, поверх — поля rules партии), чтобы цены и сроки совпали с хостом.
/// </summary>
internal sealed class ResearchData
{
    /// <summary>Какое исследование открывает покрытие (PAVE_RESEARCH): значок покрытия → тема.</summary>
    private static readonly (string Kind, string Topic)[] Paves = [("foundation", "foundation"), ("road", "roads"), ("bridge", "bridges")];

    /// <summary>Тема земляных работ (DIG_RESEARCH) и что она открывает (DIG_KINDS).</summary>
    private const string DigTopic = "earthworks";
    private static readonly string[] DigKinds = ["peak", "cliff"];

    public BaseTables Tables { get; private init; } = new BaseTables();
    public RulesSpec Rules { get; private init; } = new();

    /// <summary>Что открывает тема: здания, юниты, покрытие (unlockedBy и PAVE_RESEARCH).</summary>
    private readonly Dictionary<string, (string[] Buildings, string[] Units, string[] Paves)> _unlocks = [];

    public IReadOnlyDictionary<string, TopicSpec> Topics => Tables.Research;

    public static ResearchData Load(LoadContext ctx)
    {
        var tables = BaseTables.Load(file => ctx.TryData(file, out var bytes) ? bytes.ToArray() : null);
        var data = new ResearchData { Tables = tables, Rules = RulesOf(ctx) };
        foreach (var topic in tables.Research.Keys)
        {
            var paves = Paves.Where(pave => pave.Topic == topic).Select(pave => pave.Kind).Concat(topic == DigTopic ? DigKinds : []).ToArray();
            data._unlocks[topic] =
            (
                [.. tables.Buildings.Where(pair => pair.Value.Research == topic).Select(pair => pair.Key)],
                [.. tables.Units.Where(pair => pair.Value.Research == topic).Select(pair => pair.Key)],
                paves
            );
        }
        return data;
    }

    public (string[] Buildings, string[] Units, string[] Paves) Unlocks(string topic) =>
        _unlocks.TryGetValue(topic, out var found) ? found : ([], [], []);

    /// <summary>Правила партии, как их собирает хост: default из rules.json, поверх — rules из записи партии.</summary>
    private static RulesSpec RulesOf(LoadContext ctx)
    {
        JsonObject rules = [];
        try
        {
            if (ctx.TryData("rules", out var bytes) && JsonNode.Parse(bytes.ToArray()) is JsonObject all && all["default"] is JsonObject found)
                rules = (JsonObject)found.DeepClone();
            if (!ctx.GameData.IsEmpty && JsonNode.Parse(ctx.GameData.ToArray()) is JsonObject game && game["rules"] is JsonObject over)
                foreach (var (key, value) in over)
                    rules[key] = value?.DeepClone();
            return JsonSerializer.Deserialize(Encoding.UTF8.GetBytes("{\"x\":" + rules.ToJsonString() + "}"), BaseJson.Default.DictionaryStringRulesSpec)?.GetValueOrDefault("x") ?? new RulesSpec();
        }
        catch (JsonException)
        {
            return new RulesSpec();
        }
    }
}
