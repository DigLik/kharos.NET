using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Base.Shared.Data;
using Kharos.Mod.Sdk;

namespace Base.Host.Kernel;

/// <summary>
/// Общее ядра для всех разделов (Б1): таблицы data/*.json, правила партии (rules.json поверх — настройки партии
/// <c>rules</c>), запись партии с настройками, номера видов (Proto). Ставится в <see cref="BaseMod.Load"/>; у экземпляра
/// мода — своя статика (своя ALC в CLR, свой модуль в wasm), так что это не общее состояние партий.
/// </summary>
internal static class Core
{
    private static readonly Dictionary<string, int> ProtoIds = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, string> ProtoNames = [];

    /// <summary>Таблицы data/*.json после патчей модов.</summary>
    public static BaseTables Tables { get; private set; } = new();

    /// <summary>Правила партии: rules.json (default) и поверх — настройки партии <c>rules</c>.</summary>
    public static RulesSpec Rules { get; private set; } = new();

    /// <summary>Партия: запись games.json с настройками хоста поверх.</summary>
    public static GameSpec Game { get; private set; } = new();

    /// <summary>Имя партии (запись games.json).</summary>
    public static string GameName { get; private set; } = "";

    /// <summary>Длина тика партии, секунды (TICK в TS): по ней тики превращаются в секунды и обратно.</summary>
    public const double Step = 0.05;

    public static void Load(BaseTables tables, LoadContext ctx)
    {
        Tables = tables;
        GameName = ctx.Game;
        ProtoIds.Clear();
        ProtoNames.Clear();
        JsonNode? game = null;
        if (!ctx.GameData.IsEmpty)
        {
            try
            {
                game = JsonNode.Parse(ctx.GameData);
            }
            catch (JsonException)
            {
                game = null;
            }
        }
        Game = game is JsonObject ? Wrapped(game.ToJsonString(), BaseJson.Default.DictionaryStringGameSpec) ?? new GameSpec() : tables.Games.GetValueOrDefault(ctx.Game) ?? new GameSpec();
        // Правила: запись default из rules.json, поверх — поля настройки rules.
        var rules = ctx.TryData("rules", out var bytes) && JsonNode.Parse(bytes) is JsonObject all && all["default"] is JsonObject found ? (JsonObject)found.DeepClone() : new JsonObject();
        if (game?["rules"] is JsonObject over)
            foreach (var (key, value) in over)
                rules[key] = value?.DeepClone();
        Rules = Wrapped(rules.ToJsonString(), BaseJson.Default.DictionaryStringRulesSpec) ?? new RulesSpec();
        S.Load(ctx);
    }

    /// <summary>Запись таблицы из одного объекта JSON (типы таблиц сгенерированы словарями «имя → запись»).</summary>
    private static T? Wrapped<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<Dictionary<string, T>> info) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(Encoding.UTF8.GetBytes("{\"x\":" + json + "}"), info)?.GetValueOrDefault("x");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ===== Виды =====

    /// <summary>Номер вида «раздел/имя» (Proto.id); нет — -1. Спрашивает движок один раз на вид.</summary>
    public static int Proto(string section, string name)
    {
        var path = section + "/" + name;
        if (!ProtoIds.TryGetValue(path, out var id))
        {
            id = Tools.ProtoId(path);
            ProtoIds[path] = id;
            if (id >= 0)
                ProtoNames[id] = path;
        }
        return id;
    }

    /// <summary>Номер вида юнита (units.json).</summary>
    public static int UnitProto(string type) => Proto("units", type);

    /// <summary>Номер вида здания (buildings.json).</summary>
    public static int BuildingProto(string type) => Proto("buildings", type);

    /// <summary>«Раздел/имя» вида по номеру; нет — null.</summary>
    public static string? ProtoPath(int id)
    {
        if (id < 0)
            return null;
        if (!ProtoNames.TryGetValue(id, out var path))
        {
            path = Tools.ProtoName(id);
            if (path is null)
                return null;
            ProtoNames[id] = path;
            ProtoIds[path] = id;
        }
        return path;
    }

    /// <summary>Имя вида в разделе section по номеру; вид другого раздела или нет — null.</summary>
    public static string? ProtoName(int id, string section)
    {
        var path = ProtoPath(id);
        return path is not null && path.Length > section.Length && path.StartsWith(section, StringComparison.Ordinal) && path[section.Length] == '/'
            ? path[(section.Length + 1)..]
            : null;
    }

    // ===== Мир =====

    /// <summary>Радиус круга, накрывающего всю карту (Size — полусторона), с запасом.</summary>
    public static double Reach => ((Game.Size ?? 4096) + 64) * 1.5;

    /// <summary>Все сущности с Position и этими компонентами, по возрастанию номеров (мир до вызова).</summary>
    public static uint[] All(params ReadOnlySpan<ComponentRef> components) => Tools.Near(0, 0, Reach, components);

    // ===== Время =====

    /// <summary>Сколько тиков в seconds секундах (Math.round(seconds / step) в TS).</summary>
    public static int Ticks(double seconds) => (int)Math.Floor(seconds / Step + 0.5);

    /// <summary>Пришла ли очередь сущности делать то, что делается раз в ticks тиков (onTurn в common.ts).</summary>
    public static bool OnTurn(ulong tick, uint entity, int ticks) => ticks <= 1 || (tick + entity) % (ulong)ticks == 0;
}
