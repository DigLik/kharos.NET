using System.Diagnostics;
using Kharos.Protocol;

namespace Kharos.Host.Tests;

/// <summary>Одиночная игра: хост в своём потоке на 20 Гц, клиент — реплика через транспорт в памяти.</summary>
public class LocalGameTests
{
    [Fact]
    public void HostThreadServesReplicaAndStops()
    {
        Assert.SkipUnless(HelloGame.Built, HelloGame.NotBuilt);
        var creator = 0;
        using var game = LocalGame.Start(() =>
        {
            creator = Environment.CurrentManagedThreadId;
            return new Host(HelloGame.Content());
        });
        Assert.NotEqual(Environment.CurrentManagedThreadId, creator);
        Assert.True(game.Running);

        var replica = new Replica();
        var clock = Stopwatch.StartNew();
        while (!replica.Ready && clock.ElapsedMilliseconds < 5000)
        {
            replica.Pump(game.Client);
            Thread.Sleep(5);
        }
        Assert.True(replica.Ready);
        Assert.Equal(1u, replica.Player);
        Assert.Equal(3, HelloGame.Units(replica.World!, 1).Count);

        game.Client.Send(DeltaCodec.Order("hello.spawn", HelloGame.Spawn(20, 5, 5)));
        while (HelloGame.Units(replica.World!, 1).Count < 23 && clock.ElapsedMilliseconds < 5000)
        {
            replica.Pump(game.Client);
            Thread.Sleep(10);
        }
        Assert.Equal(23, HelloGame.Units(replica.World!, 1).Count);

        // 20 Гц: за ~0,5 с — около десяти тиков.
        var ticks = game.Ticks;
        Thread.Sleep(500);
        var passed = game.Ticks - ticks;
        Assert.InRange(passed, 3, 16);
        Assert.True(game.AverageTickMs < 50, $"тик {game.AverageTickMs} мс");

        game.Stop();
        Assert.False(game.Running);
        Assert.Null(game.Failure);
        replica.Pump(game.Client);
        Assert.True(game.Client.Closed);
        Assert.Empty(game.Host!.Errors);
    }

    [Fact]
    public void StartFailureSurfacesOnCaller()
    {
        var error = Assert.Throws<HostException>(() => LocalGame.Start(() => new Host(HelloGame.Content(), new HostOptions { Game = "nope" })));
        Assert.Contains("nope", error.Message);
    }

    [Fact]
    public void ClientClosingStopsHost()
    {
        Assert.SkipUnless(HelloGame.Built, HelloGame.NotBuilt);
        using var game = LocalGame.Start(() => new Host(HelloGame.Content()), step: TimeSpan.FromMilliseconds(5));
        game.Client.Dispose();
        var clock = Stopwatch.StartNew();
        while (game.Running && clock.ElapsedMilliseconds < 3000)
            Thread.Sleep(5);
        Assert.False(game.Running);
    }
}
