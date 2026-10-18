#if KHAROS_MOD_SDK
namespace Kharos.Mod.Sdk;
#else
namespace Kharos.Engine.Numerics;
#endif

/// <summary>
/// Детерминированная математика: одинаковый результат до бита на любой ОС и любом процессоре (docs/dotnet.md, §1).
/// <see cref="Math.Sin(double)"/> и родня в .NET идут в библиотеку C платформы и расходятся в последнем знаке, поэтому
/// земля считает только этим. Синус и косинус — перенос musl (он же fdlibm, он же крейт <c>libm</c> Rust-порта), только
/// сложения и умножения double в заданном порядке, без FMA. Корень, floor и модуль в IEEE 754 точны — их берём у .NET.
/// Те же файлы собираются в Kharos.Mod.Sdk (пространство имён Kharos.Mod.Sdk): моды в wasm и в CLR считают те же биты.
/// </summary>
public static partial class DetMath
{
    // Ядра на [−π/4, π/4]: многочлены musl __sin и __cos. y — хвост аргумента после приведения.
    private const double S1 = -1.66666666666666324348e-01;
    private const double S2 = 8.33333333332248946124e-03;
    private const double S3 = -1.98412698298579493134e-04;
    private const double S4 = 2.75573137070700676789e-06;
    private const double S5 = -2.50507602534068634195e-08;
    private const double S6 = 1.58969099521155010221e-10;
    private const double C1 = 4.16666666666666019037e-02;
    private const double C2 = -1.38888888888741095749e-03;
    private const double C3 = 2.48015872894767294178e-05;
    private const double C4 = -2.75573143513906633035e-07;
    private const double C5 = 2.08757232129817482790e-09;
    private const double C6 = -1.13596475577881948265e-11;

    /// <summary>Синус: musl <c>sin</c>, ошибка меньше ulp.</summary>
    public static double Sin(double x)
    {
        uint ix = HighWord(x) & 0x7fffffff;
        // |x| ≲ π/4: без приведения.
        if (ix <= 0x3fe921fb)
        {
            // |x| < 2⁻²⁶: sin x = x до последнего знака.
            if (ix < 0x3e500000) return x;
            return KernelSin(x, 0, false);
        }
        // sin(NaN или ∞) — NaN: входной как есть, у ∞ — константа. В musl тут x − x, но NaN от процессора разный: x64 ставит
        // бит знака (0xFFF8…), ARM — нет (0x7FF8…). double.NaN в .NET — 0xFFF8…, как x64.
        if (ix >= 0x7ff00000) return double.IsNaN(x) ? x : double.NaN;
        int n = RemPio2(x, out double y0, out double y1);
        return (n & 3) switch
        {
            0 => KernelSin(y0, y1, true),
            1 => KernelCos(y0, y1),
            2 => -KernelSin(y0, y1, true),
            _ => -KernelCos(y0, y1),
        };
    }

    /// <summary>Косинус: musl <c>cos</c>, ошибка меньше ulp.</summary>
    public static double Cos(double x)
    {
        uint ix = HighWord(x) & 0x7fffffff;
        if (ix <= 0x3fe921fb)
        {
            // |x| < 2⁻²⁷·√2: cos x = 1.
            if (ix < 0x3e46a09e) return 1.0;
            return KernelCos(x, 0);
        }
        if (ix >= 0x7ff00000) return double.IsNaN(x) ? x : double.NaN;
        int n = RemPio2(x, out double y0, out double y1);
        return (n & 3) switch
        {
            0 => KernelCos(y0, y1),
            1 => -KernelSin(y0, y1, true),
            2 => -KernelCos(y0, y1),
            _ => KernelSin(y0, y1, true),
        };
    }

    /// <summary>
    /// √(a² + b²) в double, с масштабом степенью двойки на крайних порядках, чтобы квадраты не переполнялись и не
    /// уходили в денормалы. Так считает <c>Math.hypot</c> двух чисел в Bun (сверено на эталонах); если сумма квадратов
    /// точна, как у разностей, кратных 0.5, в генераторе земли, результат округлён корректно — как у любой схемы.
    /// </summary>
    public static double Hypot(double a, double b)
    {
        if (double.IsInfinity(a) || double.IsInfinity(b)) return double.PositiveInfinity;
        if (double.IsNaN(a) || double.IsNaN(b)) return double.NaN;
        a = Math.Abs(a);
        b = Math.Abs(b);
        if (a < b) (a, b) = (b, a);
        // b² меньше половины ulp от a²: корень из a² — снова a.
        if (b == 0 || b < a * TwoPowMinus60) return a;
        double scale = a > TwoPow500 ? TwoPowMinus600 : b < TwoPowMinus500 ? TwoPow600 : 1;
        a *= scale;
        b *= scale;
        return Math.Sqrt(a * a + b * b) / scale;
    }

    // Степени двойки: в C# нет шестнадцатеричных литералов double, десятичные ниже — точные.
    private const double TwoPowMinus60 = 8.673617379884035e-19;
    private const double TwoPow500 = 3.273390607896142e+150;
    private const double TwoPowMinus500 = 3.054936363499605e-151;
    private const double TwoPow600 = 4.149515568880993e+180;
    private const double TwoPowMinus600 = 2.409919865102884e-181;

    private static uint HighWord(double x) => (uint)((ulong)BitConverter.DoubleToInt64Bits(x) >> 32);

    /// <summary>musl __sin: sin(x + y) при |x| ≤ π/4; tail — учитывать ли хвост y.</summary>
    private static double KernelSin(double x, double y, bool tail)
    {
        double z = x * x;
        double w = z * z;
        double r = S2 + z * (S3 + z * S4) + z * w * (S5 + z * S6);
        double v = z * x;
        if (!tail) return x + v * (S1 + z * r);
        return x - ((z * (0.5 * y - v * r) - y) - v * S1);
    }

    /// <summary>musl __cos: cos(x + y) при |x| ≤ π/4.</summary>
    private static double KernelCos(double x, double y)
    {
        double z = x * x;
        double w = z * z;
        double r = z * (C1 + z * (C2 + z * C3)) + w * w * (C4 + z * (C5 + z * C6));
        double hz = 0.5 * z;
        w = 1.0 - hz;
        return w + (((1.0 - w) - hz) + (z * r - x * y));
    }
}
