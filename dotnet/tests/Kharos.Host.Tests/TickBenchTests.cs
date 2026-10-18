using System.Diagnostics;
using System.Globalization;
using Kharos.Protocol;

namespace Kharos.Host.Tests;

/// <summary>
/// Замер тика целиком: 10 тыс. юнитов hello через хост — приказы, ходовая и теснота в wasm (с инструментом Separate),
/// обзор, дельта для клиента и её применение репликой. Пишет время в вывод теста; падает, только если тик за бюджетом.
/// Явный: под общим прогоном всех сборок тестов машина занята, и медиана тика ничего не говорит. Запуск:
/// <c>tests/Kharos.Host.Tests/bin/Release/net11.0/Kharos.Host.Tests -class Kharos.Host.Tests.TickBenchTests -explicit only</c>.
/// </summary>
public class TickBenchTests
{
    [Fact(Explicit = true)]
    public void TenThousandUnitsFitTickBudget()
    {
        using var host = HelloGame.Create();
        host.Start();
        // Без бюджета байт: замер — сколько весит тик целиком.
        var session = new HostSession(host, new HostSessionOptions { SendRate = 0 });
        var (hostEnd, clientEnd) = MemoryTransport.Pair();
        session.Connect(hostEnd, 1);
        var replica = new Replica();
        replica.Pump(clientEnd);
        for (var k = 0; k < 10; k++)
            host.Send(1, "hello.spawn", HelloGame.Spawn(1000, (k % 5 - 2) * 60, (k / 5 - 0.5) * 80, spread: 50));
        session.Step();
        replica.Pump(clientEnd);
        Assert.Equal(10_003, HelloGame.Units(host.World, 1).Count);

        // Разогрев, затем замер.
        for (var k = 0; k < 20; k++)
        {
            session.Step();
            replica.Pump(clientEnd);
        }
        const int ticks = 100;
        var steps = new double[ticks];
        var applies = new double[ticks];
        double hostSteps = 0, broadcasts = 0;
        long bytes = 0;
        var timings = host.Scheduler.Timings().ToDictionary(t => t.Name);
        for (var k = 0; k < ticks; k++)
        {
            var started = Stopwatch.GetTimestamp();
            session.PumpOrders();
            host.Step();
            var stepped = Stopwatch.GetTimestamp();
            session.Broadcast();
            var ended = Stopwatch.GetTimestamp();
            steps[k] = Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds;
            hostSteps += Stopwatch.GetElapsedTime(started, stepped).TotalMilliseconds;
            broadcasts += Stopwatch.GetElapsedTime(stepped, ended).TotalMilliseconds;
            bytes += session.Connections[0].LastDeltaBytes;
            started = Stopwatch.GetTimestamp();
            replica.Pump(clientEnd);
            applies[k] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var output = TestContext.Current.TestOutputHelper;
        string F(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
        Array.Sort(steps);
        output?.WriteLine($"тик хоста (приказы, системы, обзор, дельта): среднее {F(steps.Average())} мс, медиана {F(steps[ticks / 2])}, 95% {F(steps[ticks * 95 / 100])}, наибольший {F(steps[^1])}");
        output?.WriteLine($"  из них шаг хоста {F(hostSteps / ticks)} мс, рассылка (сборка и кодирование дельты) {F(broadcasts / ticks)} мс");
        output?.WriteLine($"реплика: среднее {F(applies.Average())} мс; дельта: {bytes / ticks / 1024} КБ на тик");
        foreach (var timing in host.Scheduler.Timings())
        {
            var before = timings[timing.Name];
            output?.WriteLine($"  {timing.Name}: сборка {F((timing.Gather - before.Gather) / ticks)}, система {F((timing.Run - before.Run) / ticks)}, возврат {F((timing.Scatter - before.Scatter) / ticks)}, команды {F((timing.Commands - before.Commands) / ticks)} мс на тик");
        }
        NetAssert.Same(host, replica);
        var stateless = DeltaCodec.Encode(session.LastDelta!, host.World, host.Layers, new FullView(1), host.Format);
        output?.WriteLine($"  последняя рассылка: {session.Connections[0].LastDeltaBytes / 1024} КБ, та же дельта без памяти о зрителе — {stateless.Length / 1024} КБ");
        host.Step();
        var delta = Stopwatch.GetTimestamp();
        var taken = host.TakeDelta();
        var build = Stopwatch.GetElapsedTime(delta).TotalMilliseconds;
        delta = Stopwatch.GetTimestamp();
        var encoded = DeltaCodec.Encode(taken, host.World, host.Layers, new FullView(1), host.Format);
        output?.WriteLine($"  сборка дельты {F(build)} мс, кодирование {F(Stopwatch.GetElapsedTime(delta).TotalMilliseconds)} мс, {encoded.Length / 1024} КБ, полей {taken.Fields.Count}");
        var exact = DeltaCodec.Encode(taken, host.World, host.Layers, new FullView(1), NetFormat.Exact(host.World.Schema));
        output?.WriteLine($"  та же дельта без квантования (f64): {exact.Length / 1024} КБ; с квантованием — {encoded.Length * 100 / exact.Length}%");

        // С бюджетом сервера (256 КБ/с): сколько уходит и сколько ждёт очереди.
        session.Connections[0].SendRate = 256 * 1024;
        double budgeted = 0, sent = 0, deferred = 0;
        for (var k = 0; k < 40; k++)
        {
            session.PumpOrders();
            host.Step();
            var started = Stopwatch.GetTimestamp();
            session.Broadcast();
            budgeted += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            sent += session.Connections[0].LastDeltaBytes;
            deferred += session.Deferred;
            replica.Pump(clientEnd);
        }
        output?.WriteLine($"с бюджетом 256 КБ/с: рассылка {F(budgeted / 40)} мс, {sent / 40 / 1024:0.0} КБ на тик, отложено в среднем {deferred / 40:0} сущностей");
        Assert.Empty(host.Errors);
        // Общие машины CI — не замер: там время только печатается.
        if (Environment.GetEnvironmentVariable("CI") != "true")
            Assert.True(steps[ticks / 2] < 50, $"медиана тика {steps[ticks / 2]} мс — за бюджетом 50 мс");
    }
}
