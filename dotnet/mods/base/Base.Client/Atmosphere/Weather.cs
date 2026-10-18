using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Погода мира — перенос atmosphere/weather.luau (weather.ts): смена дня и ночи и медленно бродящие ветер и осадки.
/// Состояния у неё нет — это функция параметров мира и времени игры, поэтому все игроки видят одну и ту же погоду. На
/// игру не влияет: свет сцены, осадки по биомам и ветер (его слышно в фоне). Час и настроение музыки кладутся в общую
/// память (<see cref="Shared.HourKey"/>, <see cref="Shared.MoodKey"/>) — их читают часы панели и звук.
/// </summary>
internal sealed class Weather(BaseState state)
{
    /// <summary>Сколько секунд игры длятся сутки; 0 — время стоит.</summary>
    private static readonly double DayLength = 1200;

    /// <summary>Который час в начале мира, от 0 до 24.</summary>
    private const double StartHour = 9;

    /// <summary>Меняются ли ветер и осадки; false — всегда ясно и лёгкий ветер.</summary>
    private static readonly bool Changes = true;

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
        return Math.Clamp((a + (b - a) * Smooth(f)) * 2, -1, 1);
    }

    /// <summary>Два слоя шума: крупные перемены и мелкая рябь поверх.</summary>
    private static double Drift(double t, double seed, double channel) => Perlin(t, seed, channel) * 0.75 + Perlin(t * 3.1, seed, channel + 7) * 0.25;

    /// <summary>Час суток (0..24) через seconds секунд игры.</summary>
    public static double HourAt(double seconds) =>
        ((StartHour + (DayLength > 0 ? seconds / DayLength * 24 : 0)) % 24 + 24) % 24;

    public void Update(ClientContext c)
    {
        var seconds = c.Clock;
        var hour = HourAt(seconds);
        // Солнце: выше нуля — день; сумерки мягкие, полдень — ярче всего.
        var sun = Math.Sin((hour - 6) / 12 * Math.PI);
        var day = Smooth(Math.Clamp((sun + 0.2) / 0.6, 0, 1));
        var light = NightLight + (1 - NightLight) * day;
        double wx = CalmX, wy = CalmY, precipitation = 0;
        if (Changes)
        {
            var seed = state.Data.LandSeed(c.Game);
            var rain = (Drift(seconds / RainPeriod, seed, 3) + 1) / 2;
            precipitation = Math.Clamp((rain - RainThreshold) / (1 - RainThreshold) * 1.5, 0, 1);
            // В непогоду ветер крепче.
            var strength = WindMax * (0.5 + 0.5 * precipitation);
            light *= 1 - 0.35 * precipitation;
            wx = Drift(seconds / WindPeriod, seed, 1) * strength;
            wy = Drift(seconds / WindPeriod, seed, 2) * strength * 0.6;
        }
        var atmosphere = c.Atmosphere;
        atmosphere.Light = (float)light;
        atmosphere.WindX = (float)wx;
        atmosphere.WindY = (float)wy;
        atmosphere.Precipitation = (float)precipitation;
        c.Shared[Shared.HourKey] = hour;
        c.Shared[Shared.MoodKey] = light < 0.5 ? "night" : "calm";
    }
}
