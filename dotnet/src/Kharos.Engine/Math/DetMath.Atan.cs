#if KHAROS_MOD_SDK
namespace Kharos.Mod.Sdk;
#else
namespace Kharos.Engine.Numerics;
#endif

// Арктангенс: перенос musl atan и atan2 (fdlibm). Те же сложения и умножения double в том же порядке, без FMA: движок и
// моды (в wasm и в CLR) получают одни и те же биты.
public static partial class DetMath
{
    // atan(0.5), atan(1), atan(1.5), atan(∞) — старшая и младшая части.
    private static readonly double[] AtanHi = [4.63647609000806093515e-01, 7.85398163397448278999e-01, 9.82793723247329054082e-01, 1.57079632679489655800e+00];
    private static readonly double[] AtanLo = [2.26987774529616870924e-17, 3.06161699786838301793e-17, 1.39033110312309984516e-17, 6.12323399573676603587e-17];

    private const double AT0 = 3.33333333333329318027e-01;
    private const double AT1 = -1.99999999998764832476e-01;
    private const double AT2 = 1.42857142725034663711e-01;
    private const double AT3 = -1.11111104054623557880e-01;
    private const double AT4 = 9.09088713343650656196e-02;
    private const double AT5 = -7.69187620504482999495e-02;
    private const double AT6 = 6.66107313738753120669e-02;
    private const double AT7 = -5.83357013379057348645e-02;
    private const double AT8 = 4.97687799461593236017e-02;
    private const double AT9 = -3.65315727442169155270e-02;
    private const double AT10 = 1.62858201153657823623e-02;

    private const double Pi = 3.1415926535897931160E+00;
    private const double PiLo = 1.2246467991473531772E-16;

    /// <summary>Арктангенс: musl <c>atan</c>, ошибка меньше ulp.</summary>
    public static double Atan(double x)
    {
        uint ix = HighWord(x);
        bool negative = ix >> 31 != 0;
        ix &= 0x7fffffff;
        int id;
        if (ix >= 0x44100000)
        {
            // |x| ≥ 2⁶⁶ или NaN: ±π/2 (в musl — atanhi[3] + 2⁻¹²⁰, то же число).
            if (double.IsNaN(x)) return x;
            return negative ? -AtanHi[3] : AtanHi[3];
        }
        if (ix < 0x3fdc0000)
        {
            // |x| < 0.4375; совсем малые — сами себе.
            if (ix < 0x3e400000) return x;
            id = -1;
        }
        else
        {
            x = Math.Abs(x);
            if (ix < 0x3ff30000)
            {
                if (ix < 0x3fe60000)
                {
                    // 7/16 ≤ |x| < 11/16.
                    id = 0;
                    x = (2.0 * x - 1.0) / (2.0 + x);
                }
                else
                {
                    // 11/16 ≤ |x| < 19/16.
                    id = 1;
                    x = (x - 1.0) / (x + 1.0);
                }
            }
            else if (ix < 0x40038000)
            {
                // |x| < 2.4375.
                id = 2;
                x = (x - 1.5) / (1.0 + 1.5 * x);
            }
            else
            {
                // 2.4375 ≤ |x| < 2⁶⁶.
                id = 3;
                x = -1.0 / x;
            }
        }
        double z = x * x;
        double w = z * z;
        // Сумма aT[i]·z^(i+1): нечётные и чётные члены порознь.
        double s1 = z * (AT0 + w * (AT2 + w * (AT4 + w * (AT6 + w * (AT8 + w * AT10)))));
        double s2 = w * (AT1 + w * (AT3 + w * (AT5 + w * (AT7 + w * AT9))));
        if (id < 0) return x - x * (s1 + s2);
        z = AtanHi[id] - (x * (s1 + s2) - AtanLo[id] - x);
        return negative ? -z : z;
    }

    /// <summary>Угол точки (x, y): musl <c>atan2(y, x)</c>; особые значения — как у <c>Math.atan2</c> в JS.</summary>
    public static double Atan2(double y, double x)
    {
        if (double.IsNaN(x) || double.IsNaN(y)) return double.NaN;
        ulong xb = (ulong)BitConverter.DoubleToInt64Bits(x);
        ulong yb = (ulong)BitConverter.DoubleToInt64Bits(y);
        uint ix = (uint)(xb >> 32), lx = (uint)xb;
        uint iy = (uint)(yb >> 32), ly = (uint)yb;
        // x = 1.
        if (((ix - 0x3ff00000) | lx) == 0) return Atan(y);
        // 2·знак(x) + знак(y).
        uint m = ((iy >> 31) & 1) | ((ix >> 30) & 2);
        ix &= 0x7fffffff;
        iy &= 0x7fffffff;
        if ((iy | ly) == 0)
        {
            return m switch
            {
                0 or 1 => y,
                2 => Pi,
                _ => -Pi,
            };
        }
        if ((ix | lx) == 0) return (m & 1) != 0 ? -Pi / 2 : Pi / 2;
        if (ix == 0x7ff00000)
        {
            if (iy == 0x7ff00000)
            {
                return m switch
                {
                    0 => Pi / 4,
                    1 => -Pi / 4,
                    2 => 3 * Pi / 4,
                    _ => -3 * Pi / 4,
                };
            }
            return m switch
            {
                0 => 0.0,
                1 => -0.0,
                2 => Pi,
                _ => -Pi,
            };
        }
        // |y/x| > 2⁶⁴.
        if (ix + (64 << 20) < iy || iy == 0x7ff00000) return (m & 1) != 0 ? -Pi / 2 : Pi / 2;
        // |y/x| < 2⁻⁶⁴ при x < 0 — без ложного исчезновения порядка.
        double z = (m & 2) != 0 && iy + (64 << 20) < ix ? 0 : Atan(Math.Abs(y / x));
        return m switch
        {
            0 => z,
            1 => -z,
            2 => Pi - (z - PiLo),
            _ => (z - PiLo) - Pi,
        };
    }
}
