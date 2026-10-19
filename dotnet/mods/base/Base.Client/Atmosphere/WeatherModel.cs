using System.Text.Json;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Параметры погоды мира (WeatherOptions в weather.ts): сколько секунд игры длятся сутки (0 — время стоит), который час
/// в начале мира (0..24), меняются ли ветер и осадки (нет — всегда ясно и лёгкий ветер).
/// </summary>
internal readonly record struct WeatherOptions(double DayLength, double StartHour, bool Changes)
{
    /// <summary>DEFAULT_WEATHER: сутки — 20 минут игры, начало мира — девять утра, погода меняется.</summary>
    public static readonly WeatherOptions Default = new(1200, 9, true);

    /// <summary>
    /// Параметры поверх прежних из JSON-объекта weather (dayLength, startHour, changes); чего нет или что не того вида —
    /// остаётся прежним.
    /// </summary>
    public WeatherOptions With(JsonElement weather)
    {
        if (weather.ValueKind != JsonValueKind.Object)
            return this;
        var result = this;
        if (weather.TryGetProperty("dayLength", out var length) && length.ValueKind == JsonValueKind.Number)
            result = result with { DayLength = length.GetDouble() };
        if (weather.TryGetProperty("startHour", out var hour) && hour.ValueKind == JsonValueKind.Number)
            result = result with { StartHour = hour.GetDouble() };
        if (weather.TryGetProperty("changes", out var changes) && changes.ValueKind is JsonValueKind.True or JsonValueKind.False)
            result = result with { Changes = changes.GetBoolean() };
        return result;
    }

    /// <summary>
    /// Параметры партии из ответа инструмента Settings: запись games.json, поверх неё то, что задали хосту (host.weather).
    /// Не нашлось — <see cref="Default"/>.
    /// </summary>
    public static WeatherOptions Of(string settings)
    {
        var options = Default;
        if (string.IsNullOrEmpty(settings))
            return options;
        try
        {
            using var document = JsonDocument.Parse(settings);
            foreach (var place in (ReadOnlySpan<string>)["settings", "host"])
                if (document.RootElement.TryGetProperty(place, out var record) && record.ValueKind == JsonValueKind.Object && record.TryGetProperty("weather", out var weather))
                    options = options.With(weather);
        }
        catch (JsonException)
        {
            // Ответ не разобрать — погода по умолчанию.
        }
        return options;
    }
}

/// <summary>
/// Погода в миг: час суток (0..24), освещение (0 — ночь, 1 — полдень), ветер в тайлах в секунду (+x вправо, +y вниз) и
/// осадки (0 — ясно, 1 — буря).
/// </summary>
internal readonly record struct WeatherState(double Hour, double Light, double WindX, double WindY, double Precipitation);

/// <summary>
/// Погода мира — перенос weatherAt из weather.ts (sim-ts): смена дня и ночи и медленно бродящие ветер и осадки. Состояния
/// у неё нет — это функция параметров мира, зерна и времени, поэтому все игроки видят одну и ту же погоду. На игру не
/// влияет: свет сцены, осадки по биомам и ветер (его слышно в фоне).
/// </summary>
internal static class WeatherModel
{
    /// <summary>Освещение глубокой ночью: в темноте видны фары, огни зданий и вспышки боя.</summary>
    private const double NightLight = 0.1;

    /// <summary>За сколько секунд игры ветер и осадки заметно меняются: погода бродит медленно.</summary>
    private const double WindPeriod = 300, RainPeriod = 900;

    /// <summary>Сильнейший ветер, тайлов в секунду, и ветер, когда погода не меняется.</summary>
    private const double WindMax = 3;

    private const double CalmX = 1.5, CalmY = 0.4;

    /// <summary>Сколько времени идут осадки: шум выше этого порога — дождь или снег, ниже — ясно.</summary>
    private const double RainThreshold = 0.55;

    private static double Smooth(double t) => t * t * (3 - 2 * t);

    /// <summary>Гладкий шум Перлина по одной оси: от -1 до 1, одно и то же значение для одних t и канала.</summary>
    private static double Perlin(double t, double seed, double channel)
    {
        var i = Math.Floor(t);
        var f = t - i;
        static double Gradient(double cell, double channel, double seed) => ClientContext.Hash(cell, channel, seed) * 2 - 1;
        var a = Gradient(i, channel, seed) * f;
        var b = Gradient(i + 1, channel, seed) * (f - 1);
        // Наклоны в узлах не больше единицы, и сумма двух половинок держится в пределах ±0,5: растягиваем до ±1.
        return Math.Clamp((a + (b - a) * Smooth(f)) * 2, -1, 1);
    }

    /// <summary>Два слоя шума: крупные перемены и мелкая рябь поверх.</summary>
    private static double Drift(double t, double seed, double channel) => Perlin(t, seed, channel) * 0.75 + Perlin(t * 3.1, seed, channel + 7) * 0.25;

    /// <summary>Час суток (0..24) через seconds секунд игры от начала мира.</summary>
    public static double HourAt(WeatherOptions options, double seconds) =>
        ((options.StartHour + (options.DayLength > 0 ? seconds / options.DayLength * 24 : 0)) % 24 + 24) % 24;

    /// <summary>Погода мира с параметрами options и зерном местности seed через seconds секунд игры от его начала.</summary>
    public static WeatherState At(WeatherOptions options, double seed, double seconds)
    {
        var hour = HourAt(options, seconds);
        // Солнце: выше нуля — день; сумерки мягкие, полдень — ярче всего.
        var sun = Math.Sin((hour - 6) / 12 * Math.PI);
        var day = Smooth(Math.Clamp((sun + 0.2) / 0.6, 0, 1));
        var light = NightLight + (1 - NightLight) * day;
        if (!options.Changes)
            return new WeatherState(hour, light, CalmX, CalmY, 0);
        var rain = (Drift(seconds / RainPeriod, seed, 3) + 1) / 2;
        var precipitation = Math.Clamp((rain - RainThreshold) / (1 - RainThreshold) * 1.5, 0, 1);
        // В непогоду ветер крепче.
        var strength = WindMax * (0.5 + 0.5 * precipitation);
        return new WeatherState(
            hour,
            light * (1 - 0.35 * precipitation),
            Drift(seconds / WindPeriod, seed, 1) * strength,
            Drift(seconds / WindPeriod, seed, 2) * strength * 0.6,
            precipitation);
    }
}
