using System.Buffers.Binary;
using System.Security.Cryptography;
using Kharos.Engine.Numerics;
using Kharos.Engine.Tests.Terrain;

namespace Kharos.Engine.Tests.Numerics;

/// <summary>DetMath.Atan2 (перенос musl) против Math.atan2 в Bun (эталон atan2.bin) и закреплённая сумма битов.</summary>
public sealed class Atan2Tests(ITestOutputHelper output)
{
    /// <summary>
    /// SHA-256 битов DetMath.Atan2 на парах atan2.bin. Одна на всех ОС и процессорах (в этом смысл DetMath); меняется
    /// только с входами (bun dotnet/tools/golden/atan2.ts) или с самой DetMath. Эталон снят Bun на Windows (ucrt): 4694 из
    /// 5503 до бита, остальные — в пределах ulp.
    /// </summary>
    private const string Fingerprint = "0ddb296c48aadc50cf787026a16c92087ddbd402ad00ab77211e15f4ac961eea";

    private static (double[] Y, double[] X, double[] Atan2) Read()
    {
        var bytes = File.ReadAllBytes(Golden.PathOf("Math", "atan2.bin"));
        Assert.Equal(0x3254414bu, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        var (y, x, atan2) = (new double[count], new double[count], new double[count]);
        for (var i = 0; i < count; i++)
        {
            var at = 8 + i * 24;
            y[i] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(at));
            x[i] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(at + 8));
            atan2[i] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(at + 16));
        }
        return (y, x, atan2);
    }

    [Fact]
    public void Atan2WithinUlpOfBun()
    {
        var golden = Read();
        var exact = 0;
        for (var i = 0; i < golden.Y.Length; i++)
        {
            var actual = DetMath.Atan2(golden.Y[i], golden.X[i]);
            var expected = golden.Atan2[i];
            if (Same(expected, actual))
                exact++;
            else
                Assert.True(Ulps(expected, actual) <= 1, $"Atan2({golden.Y[i]:R}, {golden.X[i]:R}): Bun {expected:R}, DetMath {actual:R}");
        }
        output.WriteLine($"Atan2: {exact} из {golden.Y.Length} совпали с Bun до бита, остальные — в пределах ulp");
    }

    [Fact]
    public void Atan2FingerprintIsStable()
    {
        var golden = Read();
        var bits = new byte[golden.Y.Length * 8];
        for (var i = 0; i < golden.Y.Length; i++)
            BinaryPrimitives.WriteDoubleLittleEndian(bits.AsSpan(i * 8), DetMath.Atan2(golden.Y[i], golden.X[i]));
        var actual = Convert.ToHexStringLower(SHA256.HashData(bits));
        output.WriteLine(actual);
        Assert.Equal(Fingerprint, actual);
    }

    [Fact]
    public void SpecialValuesFollowJs()
    {
        Assert.True(double.IsNaN(DetMath.Atan2(double.NaN, 1)));
        Assert.True(double.IsNaN(DetMath.Atan2(1, double.NaN)));
        Assert.True(double.IsNegative(DetMath.Atan2(-0.0, 1)));
        Assert.Equal(Math.PI, DetMath.Atan2(0.0, -1));
        Assert.Equal(-Math.PI, DetMath.Atan2(-0.0, -0.0));
        Assert.Equal(Math.PI / 2, DetMath.Atan2(1, 0));
        Assert.Equal(-3 * Math.PI / 4, DetMath.Atan2(double.NegativeInfinity, double.NegativeInfinity));
        Assert.Equal(Math.PI / 4, DetMath.Atan2(1, 1));
        Assert.Equal(DetMath.Atan(0.75), DetMath.Atan2(0.75, 1));
    }

    private static bool Same(double expected, double actual) =>
        BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual) || (double.IsNaN(expected) && double.IsNaN(actual));

    private static long Ulps(double expected, double actual)
    {
        if (double.IsNaN(expected) || double.IsNaN(actual))
            return long.MaxValue;
        static long Ordered(double value)
        {
            var bits = BitConverter.DoubleToInt64Bits(value);
            return bits < 0 ? long.MinValue - bits : bits;
        }
        return Math.Abs(Ordered(expected) - Ordered(actual));
    }
}
