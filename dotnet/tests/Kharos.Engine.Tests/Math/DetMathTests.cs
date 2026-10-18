using System.Buffers.Binary;
using System.Security.Cryptography;
using Kharos.Engine.Numerics;
using Kharos.Engine.Tests.Terrain;

namespace Kharos.Engine.Tests.Numerics;

/// <summary>DetMath против Math.* в Bun (эталон math.bin) и против самой себя на всех платформах (закреплённая сумма).</summary>
public sealed class DetMathTests(ITestOutputHelper output)
{
    /// <summary>
    /// SHA-256 битов Sin и Cos DetMath на углах math.bin. Одинакова на всех ОС и процессорах — в этом и смысл DetMath;
    /// меняется только вместе с входами (перегенерация эталонов) или с самой DetMath. Снята на Windows x64; точность тех
    /// же значений сверена с mpmath: ошибка не больше 0.67 ulp, как и обещает musl.
    /// </summary>
    private const string SinCosFingerprint = "dd3c044c2db16120339402fae675d6c26f82d719da384d0a91ed3ea94e580854";

    private static (double[] Angles, double[] Sin, double[] Cos, double[] A, double[] B, double[] Hypot) Read()
    {
        var bytes = File.ReadAllBytes(Golden.PathOf("Math", "math.bin"));
        Assert.Equal(0x48544d4bu, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        int angles = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        int pairs = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        int at = 12;
        double Next()
        {
            double value = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(at));
            at += 8;
            return value;
        }
        var x = new double[angles];
        var sin = new double[angles];
        var cos = new double[angles];
        for (int i = 0; i < angles; i++) (x[i], sin[i], cos[i]) = (Next(), Next(), Next());
        var a = new double[pairs];
        var b = new double[pairs];
        var hypot = new double[pairs];
        for (int i = 0; i < pairs; i++) (a[i], b[i], hypot[i]) = (Next(), Next(), Next());
        return (x, sin, cos, a, b, hypot);
    }

    [Fact]
    public void HypotMatchesBunBitForBit()
    {
        var golden = Read();
        for (int i = 0; i < golden.A.Length; i++)
        {
            double actual = DetMath.Hypot(golden.A[i], golden.B[i]);
            Assert.True(Same(golden.Hypot[i], actual), $"Hypot({golden.A[i]:R}, {golden.B[i]:R}): Bun {golden.Hypot[i]:R}, DetMath {actual:R}");
        }
    }

    [Fact]
    public void SinCosWithinUlpOfBun()
    {
        var golden = Read();
        int exact = 0;
        int total = 0;
        for (int i = 0; i < golden.Angles.Length; i++)
        {
            double x = golden.Angles[i];
            foreach (var (expected, actual, name) in new[] { (golden.Sin[i], DetMath.Sin(x), "Sin"), (golden.Cos[i], DetMath.Cos(x), "Cos") })
            {
                total++;
                if (Same(expected, actual)) exact++;
                else Assert.True(Ulps(expected, actual) <= 1, $"{name}({x:R}): Bun {expected:R}, DetMath {actual:R}");
            }
        }
        output.WriteLine($"Sin и Cos: {exact} из {total} совпали с Bun до бита, остальные — в пределах ulp");
    }

    [Fact]
    public void SinCosFingerprintIsStable()
    {
        var golden = Read();
        var bits = new byte[golden.Angles.Length * 16];
        for (int i = 0; i < golden.Angles.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(bits.AsSpan(i * 16), DetMath.Sin(golden.Angles[i]));
            BinaryPrimitives.WriteDoubleLittleEndian(bits.AsSpan(i * 16 + 8), DetMath.Cos(golden.Angles[i]));
        }
        Assert.Equal(SinCosFingerprint, Convert.ToHexStringLower(SHA256.HashData(bits)));
    }

    [Fact]
    public void SpecialValues()
    {
        Assert.True(double.IsNaN(DetMath.Sin(double.NaN)));
        Assert.True(double.IsNaN(DetMath.Sin(double.PositiveInfinity)));
        Assert.True(double.IsNaN(DetMath.Cos(double.NegativeInfinity)));
        Assert.True(double.IsNegative(DetMath.Sin(-0.0)));
        Assert.Equal(1.0, DetMath.Cos(-0.0));
        Assert.Equal(5.0, DetMath.Hypot(3, -4));
        Assert.Equal(double.PositiveInfinity, DetMath.Hypot(double.NaN, double.NegativeInfinity));
        Assert.True(double.IsNaN(DetMath.Hypot(double.NaN, 0)));
        Assert.False(double.IsNegative(DetMath.Hypot(-0.0, -0.0)));
    }

    private static bool Same(double expected, double actual) =>
        BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual) || (double.IsNaN(expected) && double.IsNaN(actual));

    private static long Ulps(double expected, double actual)
    {
        if (double.IsNaN(expected) || double.IsNaN(actual)) return long.MaxValue;
        // Упорядочить биты так, чтобы соседние double шли подряд и через ноль.
        static long Ordered(double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            return bits < 0 ? long.MinValue - bits : bits;
        }
        return Math.Abs(Ordered(expected) - Ordered(actual));
    }
}
