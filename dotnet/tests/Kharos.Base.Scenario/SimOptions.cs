using System.Text.Json;
using System.Text.Json.Nodes;
using Kharos.Plugins.Game;

namespace Kharos.Base.Scenario;

/// <summary>
/// Параметры сценария — то же, что <c>createSim(options)</c> в TS: размер, местность, правила, туман. Всё это идёт
/// настройками партии поверх записи games.json (<c>Host.Settings</c>), игра по умолчанию — <c>test</c> (128², без
/// стартового набора: всё ставит тест).
/// </summary>
public sealed class SimOptions
{
    /// <summary>Запись games.json; по умолчанию <c>test</c>.</summary>
    public string Game { get; init; } = "test";

    /// <summary>Сторона карты в тайлах; null — как у игры (<c>test</c>: 128).</summary>
    public int? Size { get; init; }

    /// <summary>
    /// Местность: имя из data/lands.json (<c>"default"</c>), либо объект параметров генератора (анонимный тип, словарь,
    /// JsonNode: <c>new { blank = true }</c>). null — как у игры.
    /// </summary>
    public object? Land { get; init; }

    /// <summary>Правила партии поверх rules.json: <c>new { techTree = false, protection = 0 }</c>. null — по умолчанию.</summary>
    public object? Rules { get; init; }

    /// <summary>Туман войны; null — как у игры.</summary>
    public bool? Fog { get; init; }

    /// <summary>Прочие настройки партии (объект JSON), сливаются поверх перечисленных выше.</summary>
    public object? Settings { get; init; }

    /// <summary>Зерно случайного ряда.</summary>
    public ulong Seed { get; init; } = 1;

    /// <summary>Игроки, которых нужно ввести в партию; по умолчанию 1 и 2.</summary>
    public IReadOnlyList<uint> Players { get; init; } = [1, 2];

    /// <summary>Режим кода мода; null — <c>KHAROS_MODS</c> (wasm | clr), не задана — CLR.</summary>
    public ModMode? Mode { get; init; }

    /// <summary>Предел вызова мода в wasm, мс (в CLR пределов нет). По умолчанию вольный: тесты проверяют логику, не время.</summary>
    public int CallTimeoutMs { get; init; } = 10_000;

    /// <summary>Ошибка системы или мода роняет <see cref="Sim.Advance"/> исключением <see cref="SimErrorException"/>. Тесту, который ждёт ошибку, — false.</summary>
    public bool ThrowOnError { get; init; } = true;

    /// <summary>
    /// Правки (<c>edit.*</c>) применяются нулевым по времени тиком (<c>Scheduler.Step(0)</c>): мир не стареет, а номер
    /// новой сущности известен сразу. false — настоящим тиком (0,05 с на каждую правку).
    /// </summary>
    public bool ZeroTimeEdits { get; init; } = true;

    /// <summary>Настройки партии объектом JSON; null — менять нечего.</summary>
    internal JsonElement? ToSettings()
    {
        var root = new JsonObject();
        if (Size is { } size)
            root["size"] = size;
        if (Fog is { } fog)
            root["fog"] = fog;
        if (Land is { } land)
            root["land"] = land as JsonNode ?? JsonSerializer.SerializeToNode(land);
        if (Rules is { } rules)
            root["rules"] = rules as JsonNode ?? JsonSerializer.SerializeToNode(rules);
        if (Settings is { } extra && (extra as JsonNode ?? JsonSerializer.SerializeToNode(extra)) is JsonObject more)
            foreach (var (key, value) in more)
                root[key] = value?.DeepClone();
        return root.Count == 0 ? null : JsonDocument.Parse(root.ToJsonString()).RootElement.Clone();
    }
}

/// <summary>Мод base не поднимается в этом режиме: нет host.wasm или Base.Host.dll. Причина — в сообщении.</summary>
public sealed class SimUnavailableException(string message) : Exception(message);

/// <summary>Ошибки систем и модов за тик: что и где упало.</summary>
public sealed class SimErrorException(IReadOnlyList<string> errors) : Exception("ошибки хоста:\n  " + string.Join("\n  ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Сигнал клиентам, увиденный на тике: адресаты (To — игроки, At — место; оба null — всем) и payload MemoryPack.</summary>
public sealed record SimSignal(ulong Tick, string Name, byte[] Payload, uint[]? To, (double X, double Y)? At);
