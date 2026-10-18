using System.Globalization;
using Kharos.Engine.Numerics;
using Kharos.Engine.Terrain;
using Kharos.Engine.Tests.Terrain;

namespace Kharos.Engine.Tests.Numerics;

/// <summary>JS-арифметика и хеш генератора против Bun (эталон js.json).</summary>
public sealed class JsMathTests
{
    [Fact]
    public void HashMatchesBun()
    {
        foreach (var entry in Golden.ReadJson("Math", "js.json").GetProperty("hash").EnumerateArray())
        {
            var values = entry.EnumerateArray().Select(value => value.GetDouble()).ToArray();
            int x = JsMath.ToInt32(values[0]);
            int y = JsMath.ToInt32(values[1]);
            int seed = JsMath.ToInt32(values[2]);
            Assert.Equal(values[3], TerrainGenerator.Hash(x, y, seed));
        }
        // Значения сняты в Bun (как в тесте Rust-порта).
        Assert.Equal(0.3876720452681184, TerrainGenerator.Hash(0, 0, 1337));
        Assert.Equal(0.8669962820131332, TerrainGenerator.Hash(-5, 7, 6340));
        Assert.Equal(0.9288620941806585, TerrainGenerator.Hash(123456, -654321, JsMath.ToInt32(4294967305)));
    }

    [Fact]
    public void RoundToInt32AndSignMatchBun()
    {
        var golden = Golden.ReadJson("Math", "js.json");
        foreach (var pair in golden.GetProperty("round").EnumerateArray())
            AssertBits(Bits(pair[1]), JsMath.Round(Bits(pair[0])));
        foreach (var pair in golden.GetProperty("toInt32").EnumerateArray())
            Assert.Equal(pair[1].GetInt32(), JsMath.ToInt32(Bits(pair[0])));
        foreach (var pair in golden.GetProperty("sign").EnumerateArray())
            AssertBits(Bits(pair[1]), JsMath.Sign(Bits(pair[0])));
    }

    [Fact]
    public void RoundingAndSignFollowJs()
    {
        Assert.Equal(-2.0, JsMath.Round(-2.5));
        Assert.Equal(3.0, JsMath.Round(2.5));
        Assert.Equal(0.0, JsMath.Round(0.49999999999999994));
        Assert.True(double.IsNegative(JsMath.Round(-0.4)));
        Assert.Equal(0.0, JsMath.Sign(0.0));
        Assert.Equal(-1.0, JsMath.Sign(-3.0));
        Assert.Equal(-1, JsMath.ToInt32(4294967295));
        Assert.Equal(0, JsMath.ToInt32(double.NaN));
        Assert.Equal((byte)44, JsMath.ToUint8(300));
        Assert.Equal(4294967295u, JsMath.ToUint32(-1));
    }

    private static double Bits(System.Text.Json.JsonElement hex) =>
        BitConverter.Int64BitsToDouble(long.Parse(hex.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static void AssertBits(double expected, double actual) =>
        Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual) || (double.IsNaN(expected) && double.IsNaN(actual)),
            $"ждали {expected:R}, получили {actual:R}");
}
