using System.Text.Json;
using Base.Client;

namespace Kharos.Base.Tests;

/// <summary>Погода: сутки, непогода, параметры в сохранении (packages/tests/weather.test.ts).</summary>
public sealed class WeatherTests
{
    /// <summary>Зерно генератора по умолчанию (DEFAULT_CONFIG.seed в terrain.ts).</summary>
    private const double Seed = 1337;

    private static readonly WeatherOptions Default = WeatherOptions.Default;

    [Fact(DisplayName = "сутки: в полдень светло, в полночь темно; начальный час и длина суток — параметры мира")]
    public void DayIsBrightNightIsDarkAndHourParametersAreWorldOptions()
    {
        var world = new WeatherOptions(DayLength: 240, StartHour: 0, Changes: true);
        Assert.Equal(0, WeatherModel.At(world, Seed, 0).Hour);
        Assert.Equal(12, WeatherModel.At(world, Seed, 120).Hour, 2);
        Assert.Equal(0, WeatherModel.At(world, Seed, 240).Hour, 2);
        var calm = world with { Changes = false };
        Assert.True(WeatherModel.At(calm, Seed, 120).Light > 0.95);
        Assert.True(WeatherModel.At(calm, Seed, 0).Light < 0.15);
        // Время стоит.
        Assert.Equal(15, WeatherModel.At(new WeatherOptions(DayLength: 0, StartHour: 15, Changes: true), Seed, 5000).Hour);
    }

    [Fact(DisplayName = "непогода бродит медленно и одинаково у всех, а без перемен всегда ясно")]
    public void WeatherDriftsSlowlyAndIsTheSameForEveryoneAndCalmWorldIsAlwaysClear()
    {
        var samples = Enumerable.Range(0, 2000).Select(i => WeatherModel.At(Default, Seed, i * 10)).ToArray();
        Assert.Contains(samples, weather => weather.Precipitation > 0.2);
        Assert.Contains(samples, weather => weather.Precipitation == 0);
        for (var i = 1; i < samples.Length; i++)
        {
            Assert.True(Math.Abs(samples[i].Precipitation - samples[i - 1].Precipitation) < 0.1);
            Assert.True(Math.Abs(samples[i].WindX - samples[i - 1].WindX) < 0.5);
        }
        Assert.Equal(WeatherModel.At(Default, Seed, 777), WeatherModel.At(Default with { }, Seed, 777));
        Assert.NotEqual(WeatherModel.At(Default, Seed + 1, 777).WindX, WeatherModel.At(Default, Seed, 777).WindX);
        var calm = Default with { Changes = false };
        Assert.All(Enumerable.Range(0, 500), i => Assert.Equal(0, WeatherModel.At(calm, Seed, i * 40).Precipitation));
    }

    [Fact(DisplayName = "параметры погоды переживают сохранение")]
    public void WeatherOptionsSurviveSave()
    {
        Assert.SkipWhen(Sim.Unavailable(Sim.DefaultMode) is { } reason, Sim.Unavailable(Sim.DefaultMode) ?? "");
        using var sim = Sim.Create(new SimOptions { Settings = new { weather = new { startHour = 21, dayLength = 600 } } });
        using var copy = sim.ThroughSave();
        // Настройки партии хранятся в сохранении как есть и доходят до клиента приветствием (инструмент Settings, host).
        Assert.NotNull(copy.Host.Settings);
        var host = JsonDocument.Parse(copy.Host.Settings!).RootElement;
        Assert.Equal(WeatherOptions.Default with { StartHour = 21, DayLength = 600 }, WeatherOptions.Default.With(host.GetProperty("weather")));
        var asClientSees = $"{{\"game\":\"test\",\"settings\":null,\"host\":{host.GetRawText()}}}";
        Assert.Equal(new WeatherOptions(600, 21, true), WeatherOptions.Of(asClientSees));
    }

    [Fact(DisplayName = "погода клиента совпадает с погодой TS до знака")]
    public void ClientWeatherMatchesTsValues()
    {
        // Значения weatherAt из packages/sim-ts (startHour 21, dayLength 600): зерно, секунды, час, свет, ветер, осадки.
        (double Seed, double Seconds, double Hour, double Light, double WindX, double WindY, double Precipitation)[] golden =
        [
            (7, 0, 21, 0.1, 0, 0, 0),
            (7, 123.5, 1.9400000000000013, 0.08530174471118353, -0.6953912957327317, -0.31240699792579024, 0.419950151109042),
            (7, 900, 9, 1, -0.19143246600050515, -0.0035631346494332887, 0),
            (7, 1500.25, 9.010000000000005, 0.9590350364837705, 0.034777036202172776, -0.06320132456600387, 0.11704275290351268),
            (7, 4000, 13, 1, 0.576425466462281, 0.10150649522224298, 0),
            (7, 7777.7, 20.107999999999947, 0.1, -0.13296553342883485, 0.22920193167270006, 0),
            (12345, 123.5, 1.9400000000000013, 0.1, 0.3178321232183295, -0.5260841614469213, 0),
            (12345, 1500.25, 9.010000000000005, 0.9132336159242558, -0.0356791520648618, 0.22945967181788995, 0.24790395450212632),
            (12345, 4000, 13, 0.8333167746232234, 0.7875308600192025, -0.1274478016213974, 0.47623778679079043),
            (12345, 7777.7, 20.107999999999947, 0.07377744087415586, -0.07205512333322975, -0.20408654041493232, 0.7492159750241183),
            (99991, 123.5, 1.9400000000000013, 0.1, 0.2522339933818314, -0.1518463984177946, 0),
            (99991, 900, 9, 1, -0.12715774577809513, 0.05126401354663076, 0),
            (99991, 1500.25, 9.010000000000005, 1, -0.04677721658938409, -0.012660877487368157, 0),
            (99991, 4000, 13, 1, -0.29395680611573705, -0.12208822011533707, 0),
            (99991, 7777.7, 20.107999999999947, 0.1, 0.04756338117509794, -0.01562624841998, 0),
        ];
        var options = new WeatherOptions(DayLength: 600, StartHour: 21, Changes: true);
        foreach (var (seed, seconds, hour, light, windX, windY, precipitation) in golden)
        {
            var weather = WeatherModel.At(options, seed, seconds);
            var where = $"зерно {seed}, {seconds} с";
            Assert.True(Math.Abs(weather.Hour - hour) < 1e-9, where + ": час");
            Assert.True(Math.Abs(weather.Light - light) < 1e-9, where + $": свет {weather.Light} против {light}");
            Assert.True(Math.Abs(weather.WindX - windX) < 1e-9, where + $": ветер x {weather.WindX} против {windX}");
            Assert.True(Math.Abs(weather.WindY - windY) < 1e-9, where + ": ветер y");
            Assert.True(Math.Abs(weather.Precipitation - precipitation) < 1e-9, where + ": осадки");
        }
    }
}
