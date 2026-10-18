using System.Net;

namespace Kharos.Server.Tests;

/// <summary>Привратник по адресам и ведро жетонов (packages/host/src/limits.ts).</summary>
public class LimitsTests
{
    [Fact]
    public void BucketRefillsAtRateUpToBurst()
    {
        long now = 0;
        var bucket = new TokenBucket(2, 3, () => now);
        Assert.True(bucket.Take());
        Assert.True(bucket.Take());
        Assert.True(bucket.Take());
        Assert.False(bucket.Take());
        now += 500; // +1 жетон
        Assert.True(bucket.Take());
        Assert.False(bucket.Take());
        now += 60_000; // не больше burst
        Assert.True(bucket.Take());
        Assert.True(bucket.Take());
        Assert.True(bucket.Take());
        Assert.False(bucket.Take());
    }

    [Fact]
    public void GateCapsConnectionsPerAddress()
    {
        var gate = new ConnectGate(new ConnectLimits(Connections: 2, ConnectsPerMinute: 0, NewPlayersPerHour: 0));
        for (var i = 0; i < 2; i++)
        {
            Assert.True(gate.Admit("1.1.1.1"));
            gate.Opened("1.1.1.1");
        }
        Assert.False(gate.Admit("1.1.1.1"));
        Assert.True(gate.Admit("2.2.2.2"));
        gate.Closed("1.1.1.1");
        Assert.True(gate.Admit("1.1.1.1"));
    }

    [Fact]
    public void GateCapsAttemptsPerMinute()
    {
        long now = 0;
        var gate = new ConnectGate(new ConnectLimits(Connections: 0, ConnectsPerMinute: 3, NewPlayersPerHour: 0), () => now);
        Assert.True(gate.Admit("a"));
        Assert.True(gate.Admit("a"));
        Assert.True(gate.Admit("a"));
        Assert.False(gate.Admit("a"));
        Assert.True(gate.Admit("b"));
        now += 20_000; // 3 в минуту = 1 за 20 секунд
        Assert.True(gate.Admit("a"));
        Assert.False(gate.Admit("a"));
    }

    [Fact]
    public void GateCapsNewcomersPerHour()
    {
        long now = 0;
        var gate = new ConnectGate(new ConnectLimits(Connections: 0, ConnectsPerMinute: 0, NewPlayersPerHour: 2), () => now);
        Assert.True(gate.Newcomer("a"));
        Assert.True(gate.Newcomer("a"));
        Assert.False(gate.Newcomer("a"));
        Assert.True(gate.Newcomer("b"));
        now += 1_800_000; // полчаса — один жетон
        Assert.True(gate.Newcomer("a"));
        Assert.False(gate.Newcomer("a"));
    }

    [Fact]
    public void ZeroMeansNoLimit()
    {
        var gate = new ConnectGate(new ConnectLimits(0, 0, 0));
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(gate.Admit("a"));
            gate.Opened("a");
            Assert.True(gate.Newcomer("a"));
        }
    }

    [Fact]
    public void ForwardedForIsTrustedOnlyFromLocalProxy()
    {
        var loopback = IPAddress.Loopback;
        var outside = IPAddress.Parse("203.0.113.9");
        Assert.Equal("5.6.7.8", ClientAddress.Of(loopback, "9.9.9.9, 5.6.7.8"));
        Assert.Equal("5.6.7.8", ClientAddress.Of(IPAddress.IPv6Loopback, "5.6.7.8"));
        Assert.Equal("5.6.7.8", ClientAddress.Of(IPAddress.Parse("::ffff:127.0.0.1"), "5.6.7.8"));
        Assert.Equal("127.0.0.1", ClientAddress.Of(loopback, null));
        Assert.Equal("127.0.0.1", ClientAddress.Of(loopback, ""));
        // Чужой клиент заголовку не хозяин.
        Assert.Equal("203.0.113.9", ClientAddress.Of(outside, "5.6.7.8"));
        Assert.Equal("203.0.113.9", ClientAddress.Of(IPAddress.Parse("::ffff:203.0.113.9"), null));
        Assert.Equal("unknown", ClientAddress.Of(null, null));
    }
}
