using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kharos.Server;

/// <summary>Настройки не прочитались или противоречат друг другу.</summary>
public sealed class SettingsException(string message) : Exception(message);

/// <summary>Настройки TLS (apps/server/tls.ts). Режим auto пока не поддерживается.</summary>
public sealed record TlsSettings(string Mode, string Domain, string Email, string Cert, string Key, string Dir, int ChallengePort, bool Staging)
{
    public static readonly string[] Modes = ["off", "auto", "files"];
}

/// <summary>
/// Настройки сервера из settings.json (apps/server/settings.ts): ключи те же. Файл накладывается на умолчания: берутся
/// только известные ключи того же вида, про остальное — предупреждение. Переменные PORT, SAVE, SETTINGS сильнее файла.
/// Что нынешний хост .NET не умеет, принимается и пропускается с пометкой в <see cref="Notes"/>.
/// </summary>
public sealed class ServerSettings
{
    /// <summary>Порт по умолчанию (protocol.ts: DEFAULT_PORT).</summary>
    public const int DefaultPort = 8787;

    /// <summary>Сколько КБ в секунду шлётся одному игроку по умолчанию (host.ts: DEFAULT_SEND_RATE).</summary>
    public const int DefaultSendRate = 256;

    public int Port { get; private init; }
    public string Lang { get; private init; } = "ru";
    public string Password { get; private init; } = "";
    public string Admin { get; private init; } = "";
    public string Save { get; private init; } = "save.kharos";
    public int Size { get; private init; }
    public bool Fog { get; private init; }

    /// <summary>generator.seed: зерно хоста (HostOptions.Seed).</summary>
    public ulong Seed { get; private init; }

    public int SendRate { get; private init; }
    public IReadOnlyList<string> Mods { get; private init; } = [];
    public ConnectLimits Limits { get; private init; } = new();
    public TlsSettings Tls { get; private init; } = null!;

    /// <summary>Разобранное дерево целиком (для записи и для ключей, которые хост пока не читает).</summary>
    public JsonObject Tree { get; private init; } = null!;

    /// <summary>Что из настроек не действует в этой сборке, по-русски: для журнала при запуске.</summary>
    public IReadOnlyList<string> Notes { get; private init; } = [];

    /// <summary>Настройки по умолчанию; зерно у каждого нового сервера своё.</summary>
    public static JsonObject Defaults(int? seed = null) => new()
    {
        ["port"] = DefaultPort,
        ["lang"] = "ru",
        ["password"] = "",
        ["admin"] = "",
        ["save"] = "save.kharos",
        ["size"] = 1024,
        ["fog"] = true,
        ["generator"] = new JsonObject
        {
            ["seed"] = seed ?? Random.Shared.Next(),
            ["zoneScale"] = 56,
            ["zoneWarp"] = 30,
            ["swampLevel"] = 0.37,
            ["rockLevel"] = 0.59,
            ["biomeScale"] = 240,
            ["peakChance"] = 0.3,
            ["peakMinRadius"] = 1,
            ["peakMaxRadius"] = 2,
            ["doubleChance"] = 0.35,
            ["tripleChance"] = 0.15,
            ["saltSwampShift"] = -0.05,
            ["saltRockShift"] = 0.05,
            ["saltPeakFactor"] = 1,
            ["redSwampShift"] = -0.06,
            ["redRockShift"] = -0.07,
            ["redPeakFactor"] = 2.5,
            ["marshSwampShift"] = 0.09,
            ["marshRockShift"] = 0.04,
            ["marshPeakFactor"] = 0.5,
            ["tierStep"] = 0.065,
            ["cliffScale"] = 20,
            ["cliffShare"] = 0.35,
            ["depositChance"] = 0.95,
            ["depositRichness"] = 1,
        },
        ["weather"] = new JsonObject { ["dayLength"] = 1200, ["startHour"] = 9, ["changes"] = true },
        // Правила серверной сессии: DEFAULT_RULES с поправками SESSION_RULES (researchScale 1, researchGrowth 3, researchSlow 10, upgradeScale 5).
        ["rules"] = new JsonObject
        {
            ["repairSpeed"] = 2,
            ["repairCost"] = 0.5,
            ["repairPause"] = 3,
            ["techTree"] = true,
            ["vehicleSand"] = 0.1,
            ["vehicleSwamp"] = 0.8,
            ["heavySand"] = 0,
            ["heavySwamp"] = 0.5,
            ["infantrySand"] = 0,
            ["infantrySwamp"] = 0.5,
            ["infantryCliff"] = 0.7,
            ["frozenSwamp"] = 0.25,
            ["toxicSwamp"] = 0.01,
            ["offlineIncome"] = 0.2,
            ["offlineMinutes"] = 30,
            ["protection"] = 10,
            ["researchScale"] = 1,
            ["researchGrowth"] = 3,
            ["researchSlow"] = 10,
            ["upgradeScale"] = 5,
            ["depositRenewal"] = 15,
            ["armyLimit"] = 600,
            ["unitAmmo"] = true,
        },
        ["tls"] = new JsonObject
        {
            ["mode"] = "off",
            ["domain"] = "",
            ["email"] = "",
            ["cert"] = "",
            ["key"] = "",
            ["dir"] = "tls",
            ["challengePort"] = 80,
            ["staging"] = false,
        },
        ["limits"] = new JsonObject { ["connections"] = 8, ["connectsPerMinute"] = 30, ["newPlayersPerHour"] = 5 },
        ["sendRate"] = DefaultSendRate,
        ["mods"] = new JsonArray(),
    };

    /// <summary>
    /// Накладывает прочитанное на умолчания. Берёт только известные ключи того же вида, что и по умолчанию; про остальное
    /// пишет в <paramref name="warnings"/>, чтобы опечатка не пропала молча.
    /// </summary>
    public static JsonObject Merge(JsonObject defaults, JsonNode? source, List<string> warnings)
    {
        JsonObject MergeObject(JsonObject baseObject, JsonNode? value, string path)
        {
            if (value is not JsonObject given)
            {
                warnings.Add($"{(path.Length == 0 ? "настройки" : path)}: ожидался объект");
                return (JsonObject)baseObject.DeepClone();
            }
            var result = new JsonObject();
            foreach (var (key, item) in baseObject)
                result[key] = item?.DeepClone();
            foreach (var (key, item) in given)
            {
                var name = path.Length == 0 ? key : $"{path}.{key}";
                if (!baseObject.TryGetPropertyValue(key, out var current))
                    warnings.Add($"{name}: неизвестный параметр");
                else if (current is JsonArray)
                {
                    // Списки в настройках — строк.
                    if (item is JsonArray list && list.All(entry => entry is JsonValue v && v.GetValueKind() == JsonValueKind.String))
                        result[key] = list.DeepClone();
                    else
                        warnings.Add($"{name}: ожидался список строк");
                }
                else if (current is JsonObject inner)
                    result[key] = MergeObject(inner, item, name);
                else if (item is not JsonValue scalar || Same(scalar.GetValueKind()) != Same(((JsonValue)current!).GetValueKind()))
                    warnings.Add($"{name}: ожидалось {Word(((JsonValue)current!).GetValueKind())}");
                else
                    result[key] = item.DeepClone();
            }
            return result;
        }
        return MergeObject(defaults, source, "");
    }

    /// <summary>Число из узла: у значений, созданных из int и из разбора файла, разные внутренние виды.</summary>
    private static double Number(JsonNode node) => double.Parse(node.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>true и false в JSON — разные виды, а для настройки это один: boolean.</summary>
    private static JsonValueKind Same(JsonValueKind kind) => kind == JsonValueKind.False ? JsonValueKind.True : kind;

    private static string Word(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Number => "number",
        JsonValueKind.String => "string",
        _ => "boolean",
    };

    /// <summary>
    /// Читает настройки: файл (создаётся со всеми умолчаниями, если его нет), затем переменные окружения.
    /// Предупреждения (неизвестные ключи, не тот вид) — в <paramref name="log"/>.
    /// </summary>
    public static ServerSettings Load(string? path, Func<string, string?> env, Action<string> log)
    {
        path = env("SETTINGS") is { Length: > 0 } fromEnv ? fromEnv : path ?? "settings.json";
        var defaults = Defaults();
        JsonObject tree;
        if (!File.Exists(path))
        {
            Write(path, defaults);
            log($"настройки по умолчанию записаны в {path}");
            tree = defaults;
        }
        else
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(File.ReadAllBytes(path));
            }
            catch (JsonException error)
            {
                throw new SettingsException($"{path}: не JSON ({error.Message})");
            }
            var warnings = new List<string>();
            tree = Merge(defaults, parsed, warnings);
            foreach (var warning in warnings)
                log($"{path}: {warning} — пропущено");
        }
        return From(tree, defaults, env, log);
    }

    /// <summary>Типизированные настройки из слитого дерева; env — переменные окружения (PORT, SAVE).</summary>
    public static ServerSettings From(JsonObject tree, JsonObject defaults, Func<string, string?> env, Action<string>? log = null)
    {
        int Int(JsonObject o, JsonObject fallback, string key, int min, int max, string path)
        {
            var value = Number(o[key]!);
            if (value < min || value > max || value != Math.Floor(value))
            {
                log?.Invoke($"{path}: {value}? ожидалось целое от {min} до {max} — пропущено");
                value = Number(fallback[key]!);
            }
            return (int)value;
        }
        var limits = (JsonObject)tree["limits"]!;
        var tls = (JsonObject)tree["tls"]!;
        var port = Int(tree, defaults, "port", 0, 65535, "port");
        // Переменные окружения сильнее файла: так удобнее в docker и systemd.
        if (int.TryParse(env("PORT"), out var envPort) && envPort is > 0 and <= 65535)
            port = envPort;
        var save = env("SAVE") is { Length: > 0 } envSave ? envSave : tree["save"]!.GetValue<string>();
        var mode = tls["mode"]!.GetValue<string>();
        if (!TlsSettings.Modes.Contains(mode))
            throw new SettingsException($"tls.mode: {mode}? ожидалось {string.Join(", ", TlsSettings.Modes)}");
        return new ServerSettings
        {
            Port = port,
            Lang = tree["lang"]!.GetValue<string>(),
            Password = tree["password"]!.GetValue<string>(),
            Admin = tree["admin"]!.GetValue<string>(),
            Save = save,
            Size = Int(tree, defaults, "size", 1, int.MaxValue, "size"),
            Fog = tree["fog"]!.GetValue<bool>(),
            Seed = (ulong)Math.Max(0, Number(((JsonObject)tree["generator"]!)["seed"]!)),
            SendRate = Int(tree, defaults, "sendRate", 0, int.MaxValue, "sendRate"),
            Mods = [.. ((JsonArray)tree["mods"]!).Select(entry => entry!.GetValue<string>())],
            Limits = new ConnectLimits(
                Int(limits, (JsonObject)defaults["limits"]!, "connections", 0, int.MaxValue, "limits.connections"),
                Int(limits, (JsonObject)defaults["limits"]!, "connectsPerMinute", 0, int.MaxValue, "limits.connectsPerMinute"),
                Int(limits, (JsonObject)defaults["limits"]!, "newPlayersPerHour", 0, int.MaxValue, "limits.newPlayersPerHour")),
            Tls = new TlsSettings(
                mode, tls["domain"]!.GetValue<string>(), tls["email"]!.GetValue<string>(),
                tls["cert"]!.GetValue<string>(), tls["key"]!.GetValue<string>(), tls["dir"]!.GetValue<string>(),
                Int(tls, (JsonObject)defaults["tls"]!, "challengePort", 1, 65535, "tls.challengePort"), tls["staging"]!.GetValue<bool>()),
            Tree = tree,
            Notes = Ignored(tree, defaults, mode),
        };
    }

    /// <summary>Ключи, которые нынешний хост не применяет, — если их значения не умолчания: оператор должен знать, что они молчат.</summary>
    private static List<string> Ignored(JsonObject tree, JsonObject defaults, string tlsMode)
    {
        var notes = new List<string>();
        void Differs(string key, string why)
        {
            if (!JsonNode.DeepEquals(tree[key], defaults[key]))
                notes.Add($"{key}: {why}");
        }
        Differs("size", "размер карты задаёт партия мода (games.json), настройка не действует");
        Differs("fog", "туман войны задаёт партия мода (games.json), настройка не действует");
        Differs("weather", "погоду ведёт мод, настройка не действует");
        Differs("rules", "правила задают данные модов, настройка не действует");
        Differs("lang", "тексты сервера пока только русские, настройка не действует");
        if (tree["generator"] is JsonObject generator && defaults["generator"] is JsonObject generatorDefaults)
            foreach (var (key, value) in generator)
                if (key != "seed" && !JsonNode.DeepEquals(value, generatorDefaults[key]))
                {
                    notes.Add("generator: из генератора действует только seed (зерно хоста), остальное задаёт местность партии мода (lands.json)");
                    break;
                }
        if (tlsMode == "files")
            foreach (var key in new[] { "domain", "email", "dir", "challengePort", "staging" })
                if (!JsonNode.DeepEquals(tree["tls"]![key], defaults["tls"]![key]))
                {
                    notes.Add($"tls.{key}: нужен только режиму auto, который ещё не поддерживается");
                    break;
                }
        return notes;
    }

    /// <summary>Пишет настройки через временный файл.</summary>
    public static void Write(string path, JsonObject tree)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            tree.WriteTo(writer);
        stream.Write("\n"u8);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, stream.ToArray());
        File.Move(temporary, path, overwrite: true);
    }
}
