using System.Text.Json;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Погода мира в клиенте — слой над <see cref="WeatherModel"/> (atmosphere/weather.luau, weather.ts): параметры мира
/// (сутки, начальный час, меняется ли погода) берутся из настроек партии, зерно — из местности партии; свет сцены, осадки по
/// биомам и ветер пишутся в c.Atmosphere. Час и настроение музыки кладутся в общую память (<see cref="Shared.HourKey"/>,
/// <see cref="Shared.MoodKey"/>) — их читают часы панели и звук.
/// </summary>
internal sealed class Weather(BaseState state)
{
    private WeatherOptions? _options;
    private double _seed;

    /// <summary>Час суток (0..24) через seconds секунд игры при параметрах по умолчанию (запасной ответ для панелей).</summary>
    public static double HourAt(double seconds) => WeatherModel.HourAt(WeatherOptions.Default, seconds);

    /// <summary>Параметры и зерно партии: читаются раз, когда клиент узнал настройки.</summary>
    private WeatherOptions Options(ClientContext c)
    {
        if (_options is { } known)
            return known;
        var settings = c.Settings();
        var options = WeatherOptions.Of(settings);
        _seed = state.Data.LandSeed(c.Game);
        var ready = false;
        // Хосту могли задать местность объектом с зерном (host.land.seed) — оно сильнее записи lands.json.
        try
        {
            using var document = JsonDocument.Parse(settings);
            ready = document.RootElement.TryGetProperty("game", out var game) && game.ValueKind == JsonValueKind.String && game.GetString() is { Length: > 0 };
            if (document.RootElement.TryGetProperty("host", out var host) && host.ValueKind == JsonValueKind.Object
                && host.TryGetProperty("land", out var land) && land.ValueKind == JsonValueKind.Object && land.TryGetProperty("seed", out var seed) && seed.ValueKind == JsonValueKind.Number)
                _seed = seed.GetDouble();
        }
        catch (JsonException)
        {
            // Настроек нет — зерно записи партии.
        }
        // Приветствия ещё нет (игра пуста) — не запоминаем: на следующем кадре настройки уже будут.
        if (ready)
            _options = options;
        return options;
    }

    public void Update(ClientContext c)
    {
        var weather = WeatherModel.At(Options(c), _seed, c.Clock);
        var atmosphere = c.Atmosphere;
        atmosphere.Light = (float)weather.Light;
        atmosphere.WindX = (float)weather.WindX;
        atmosphere.WindY = (float)weather.WindY;
        atmosphere.Precipitation = (float)weather.Precipitation;
        c.Shared[Shared.HourKey] = weather.Hour;
        c.Shared[Shared.MoodKey] = weather.Light < 0.5 ? "night" : "calm";
    }
}
