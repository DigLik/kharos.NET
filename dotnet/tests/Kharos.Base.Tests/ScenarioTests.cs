using System.Diagnostics;
using System.Globalization;
using System.Text;
using Kharos.Content;
using Kharos.Engine.World;
using KHost = Kharos.Host.Host;
using KHostOptions = Kharos.Host.HostOptions;

namespace Kharos.Base.Tests;

/// <summary>Партия целиком: детерминизм, правка данных чужим модом, время тика.</summary>
public class ScenarioTests
{
    /// <summary>Сценарий двух игроков: заказы, приказы, отмены, 300 тиков. Возвращает сохранение мира.</summary>
    private static byte[] Play(ulong seed)
    {
        using var host = BaseGame.Create(seed);
        host.Start();
        host.Join(1);
        host.Join(2);
        var factory = BaseGame.Factory(host, 1);
        var other = BaseGame.Factory(host, 2);
        var mine = BaseGame.Units(host, 1);
        var theirs = BaseGame.Units(host, 2);
        BaseGame.Produce(host, 1, factory);
        BaseGame.Produce(host, 1, factory);
        BaseGame.Produce(host, 2, other);
        BaseGame.Rally(host, 1, factory, 15, -9);
        BaseGame.Move(host, 1, 40, 25, mine[0], mine[1], mine[2]);
        BaseGame.Move(host, 2, 30, 5, theirs[0], theirs[1]);
        BaseGame.Run(host, 100);
        BaseGame.Cancel(host, 1, factory);
        BaseGame.Move(host, 2, -20, 10, theirs[0], theirs[1], theirs[2]);
        BaseGame.Produce(host, 2, other);
        BaseGame.Run(host, 200);
        Assert.Empty(host.Errors);
        return WorldSave.Save(host.World);
    }

    [Fact]
    public void SameSeedGivesIdenticalWorldBytes()
    {
        Assert.SkipUnless(BaseGame.Built, BaseGame.NotBuilt);
        var first = Play(7);
        var second = Play(7);
        Assert.True(first.AsSpan().SequenceEqual(second), "мир разошёлся на тех же приказах");
        Assert.True(first.Length > 1000);
    }

    [Fact]
    public void RulesComeFromDataSoAnotherModCanTuneThem()
    {
        Assert.SkipUnless(BaseGame.Built, BaseGame.NotBuilt);
        // Мод без кода правит данные: багги медленнее и дешевле, больше стартовых юнитов и кредитов.
        var tweak = new ModPackage("tweak",
            """{ "name": "tweak", "version": "1", "requires": ["base"] }"""u8,
            new Dictionary<string, byte[]>
            {
                ["units"] = Encoding.UTF8.GetBytes("""{ "buggy": { "speed": 2.5, "cost": 100, "time": 2 } }"""),
                ["games"] = Encoding.UTF8.GetBytes("""{ "skirmish": { "credits": 400, "start": { "buggy": 4 } } }"""),
            });
        using var host = BaseGame.Create(content: GameContent.Load([ModPackage.Read(BaseGame.ModDir), tweak]));
        host.Start();
        host.Join(1);
        var units = BaseGame.Units(host, 1);
        Assert.Equal(4, units.Count);
        Assert.All(units, e => Assert.Equal(2.5, BaseGame.Num(host, e, "Drive", "speed")));
        Assert.Equal(400, BaseGame.Credits(host, 1));
        var factory = BaseGame.Factory(host, 1);
        BaseGame.Produce(host, 1, factory);
        BaseGame.Step(host);
        Assert.Equal(300, BaseGame.Credits(host, 1));
        BaseGame.Run(host, BaseGame.TicksFor(2) - 1);
        Assert.Equal(5, BaseGame.Units(host, 1).Count);
        Assert.Empty(host.Errors);
    }

    [Fact]
    public void TickTimeOfOnePlayerSkirmish()
    {
        Assert.SkipUnless(BaseGame.Built, BaseGame.NotBuilt);
        using var host = BaseGame.Create();
        host.Start();
        host.Join(1);
        var mine = BaseGame.Units(host, 1);
        BaseGame.Move(host, 1, 40, 25, [.. mine]);
        BaseGame.Run(host, 20);
        const int ticks = 200;
        var times = new double[ticks];
        for (var tick = 0; tick < ticks; tick++)
        {
            if (tick % 50 == 0)
                BaseGame.Move(host, 1, (tick % 100 == 0 ? -1 : 1) * 30, 20, [.. mine]);
            var started = Stopwatch.GetTimestamp();
            host.Step();
            times[tick] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            host.TakeDelta();
        }
        Array.Sort(times);
        string F(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);
        TestContext.Current.TestOutputHelper?.WriteLine($"тик skirmish, 1 игрок (завод, 3 багги, 3 турели, {BaseGame.With(host, "Deposit").Count} месторождений): среднее {F(times.Average())} мс, медиана {F(times[ticks / 2])}, 95% {F(times[ticks * 95 / 100])}, наибольший {F(times[^1])}");
        Assert.True(times.Average() < 5, $"тик {times.Average()} мс");
        foreach (var timing in host.Scheduler.Timings())
            TestContext.Current.TestOutputHelper?.WriteLine($"  {timing.Name}: {timing}");
        Assert.Empty(host.Errors);
    }
}
