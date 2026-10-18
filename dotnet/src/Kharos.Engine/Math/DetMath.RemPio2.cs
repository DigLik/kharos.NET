#if KHAROS_MOD_SDK
namespace Kharos.Mod.Sdk;
#else
namespace Kharos.Engine.Numerics;
#endif

// Приведение аргумента к [−π/4, π/4]: musl __rem_pio2 и __rem_pio2_large (Payne–Hanek) для огромных |x|.
public static partial class DetMath
{
    /// <summary>1.5·2⁵²: прибавить и отнять — округлить до целого в режиме к ближайшему.</summary>
    private const double ToInt = 6755399441055744.0;
    private const double Pio4 = 7.85398163397448278999e-01;
    /// <summary>53 бита 2/π.</summary>
    private const double InvPio2 = 6.36619772367581382433e-01;
    /// <summary>π/2 тремя частями по 33 бита и хвосты: pio2_1 + pio2_1t ≈ π/2 и т. д.</summary>
    private const double Pio2_1 = 1.57079632673412561417e+00;
    private const double Pio2_1t = 6.07710050650619224932e-11;
    private const double Pio2_2 = 6.07710050630396597660e-11;
    private const double Pio2_2t = 2.02226624879595063154e-21;
    private const double Pio2_3 = 2.02226624871116645580e-21;
    private const double Pio2_3t = 8.47842766036889956997e-32;
    private const double TwoPow24 = 16777216.0;
    private const double TwoPowMinus24 = 5.960464477539063e-08;

    /// <summary>x = n·π/2 + (y0 + y1), |y0 + y1| ≤ π/4. Возвращает n. Вызывать при |x| > π/4, x конечен.</summary>
    private static int RemPio2(double x, out double y0, out double y1)
    {
        ulong bits = (ulong)BitConverter.DoubleToInt64Bits(x);
        bool negative = bits >> 63 != 0;
        uint ix = (uint)(bits >> 32) & 0x7fffffff;

        if (ix <= 0x400f6a7a)
        {
            // |x| ≲ 5π/4. Около π/2 и π вычитание съедает знаки — туда, где приведение точнее.
            if ((ix & 0xfffff) != 0x921fb)
            {
                if (ix <= 0x4002d97c) return Near(x, 1, negative, out y0, out y1);
                return Near(x, 2, negative, out y0, out y1);
            }
        }
        else if (ix <= 0x401c463b)
        {
            // |x| ≲ 9π/4.
            if (ix <= 0x4015fdbc)
            {
                if (ix != 0x4012d97c) return Near(x, 3, negative, out y0, out y1);
            }
            else if (ix != 0x401921fb)
            {
                return Near(x, 4, negative, out y0, out y1);
            }
        }
        else if (ix >= 0x413921fb)
        {
            // |x| ≥ 2²⁰·π/2.
            return Large(x, bits, ix, negative, out y0, out y1);
        }

        // Средние |x|: n = rint(x·2/π), вычитание в два-три приёма.
        double fn = x * InvPio2 + ToInt - ToInt;
        int n = (int)fn;
        double r = x - fn * Pio2_1;
        double w = fn * Pio2_1t;
        // Нужно только при округлении не к ближайшему; оставлено, как в musl.
        if (r - w < -Pio4)
        {
            n--;
            fn--;
            r = x - fn * Pio2_1;
            w = fn * Pio2_1t;
        }
        else if (r - w > Pio4)
        {
            n++;
            fn++;
            r = x - fn * Pio2_1;
            w = fn * Pio2_1t;
        }
        y0 = r - w;
        int ey = Exponent(y0);
        int ex = (int)(ix >> 20);
        if (ex - ey > 16)
        {
            // Второй приём: 118 бит.
            double t = r;
            w = fn * Pio2_2;
            r = t - w;
            w = fn * Pio2_2t - ((t - r) - w);
            y0 = r - w;
            ey = Exponent(y0);
            if (ex - ey > 49)
            {
                // Третий: 151 бит, хватает на всё.
                t = r;
                w = fn * Pio2_3;
                r = t - w;
                w = fn * Pio2_3t - ((t - r) - w);
                y0 = r - w;
            }
        }
        y1 = (r - y0) - w;
        return n;
    }

    /// <summary>Малые n: одно вычитание n·π/2, хватает 85 бит.</summary>
    private static int Near(double x, int n, bool negative, out double y0, out double y1)
    {
        double head = n * Pio2_1;
        double tail = n * Pio2_1t;
        if (!negative)
        {
            double z = x - head;
            y0 = z - tail;
            y1 = (z - y0) - tail;
            return n;
        }
        else
        {
            double z = x + head;
            y0 = z + tail;
            y1 = (z - y0) + tail;
            return -n;
        }
    }

    private static int Exponent(double value) => (int)((ulong)BitConverter.DoubleToInt64Bits(value) >> 52) & 0x7ff;

    /// <summary>Огромные |x|: z = |x|·2^(23 − ilogb x) режется на три числа по 24 бита и идёт в Payne–Hanek.</summary>
    private static int Large(double x, ulong bits, uint ix, bool negative, out double y0, out double y1)
    {
        double z = BitConverter.Int64BitsToDouble((long)((bits & (ulong.MaxValue >> 12)) | ((ulong)(0x3ff + 23) << 52)));
        Span<double> tx = stackalloc double[3];
        int i;
        for (i = 0; i < 2; i++)
        {
            tx[i] = (int)z;
            z = (z - tx[i]) * TwoPow24;
        }
        tx[i] = z;
        // Нулевые хвосты не нужны; первый член не ноль.
        while (tx[i] == 0.0) i--;
        int n = RemPio2Large(tx[..(i + 1)], out double ty0, out double ty1, (int)(ix >> 20) - (0x3ff + 23));
        if (negative)
        {
            y0 = -ty0;
            y1 = -ty1;
            return -n;
        }
        y0 = ty0;
        y1 = ty1;
        return n;
    }

    /// <summary>2/π по 24 бита: хватает для любого double.</summary>
    private static ReadOnlySpan<int> Ipio2 =>
    [
        0xA2F983, 0x6E4E44, 0x1529FC, 0x2757D1, 0xF534DD, 0xC0DB62,
        0x95993C, 0x439041, 0xFE5163, 0xABDEBB, 0xC561B7, 0x246E3A,
        0x424DD2, 0xE00649, 0x2EEA09, 0xD1921C, 0xFE1DEB, 0x1CB129,
        0xA73EE8, 0x8235F5, 0x2EBB44, 0x84E99C, 0x7026B4, 0x5F7E41,
        0x3991D6, 0x398353, 0x39F49C, 0x845F8B, 0xBDF928, 0x3B1FF8,
        0x97FFDE, 0x05980F, 0xEF2F11, 0x8B5A0A, 0x6D1F6D, 0x367ECF,
        0x27CB09, 0xB74F46, 0x3F669E, 0x5FEA2D, 0x7527BA, 0xC7EBE5,
        0xF17B3D, 0x0739F7, 0x8A5292, 0xEA6BFB, 0x5FB11F, 0x8D5D08,
        0x560330, 0x46FC7B, 0x6BABF0, 0xCFBC20, 0x9AF436, 0x1DA9E3,
        0x91615E, 0xE61B08, 0x659985, 0x5F14A0, 0x68408D, 0xFFD880,
        0x4D7327, 0x310606, 0x1556CA, 0x73A8C9, 0x60E27B, 0xC08C6B,
    ];

    /// <summary>π/2 по 24 бита.</summary>
    private static ReadOnlySpan<double> PIo2 =>
    [
        1.57079625129699707031e+00,
        7.54978941586159635335e-08,
        5.39030252995776476554e-15,
        3.28200341580791294123e-22,
        1.27065575308067607349e-29,
        1.22933308981111328932e-36,
        2.73370053816464559624e-44,
        2.16741683877804819444e-51,
    ];

    /// <summary>
    /// musl __rem_pio2_large для точности double (prec = 1, jk = 4): x — входные 24-битные куски, e0 — порядок
    /// первого. Возвращает n mod 8 и остаток y0 + y1.
    /// </summary>
    private static int RemPio2Large(ReadOnlySpan<double> x, out double y0, out double y1, int e0)
    {
        const int jk = 4;
        const int jp = jk;
        Span<int> iq = stackalloc int[20];
        Span<double> f = stackalloc double[20];
        Span<double> fq = stackalloc double[20];
        Span<double> q = stackalloc double[20];
        ReadOnlySpan<int> ipio2 = Ipio2;
        ReadOnlySpan<double> pio2 = PIo2;

        int jx = x.Length - 1;
        int jv = (e0 - 3) / 24;
        if (jv < 0) jv = 0;
        int q0 = e0 - 24 * (jv + 1);

        // f[0..jx+jk], где f[jx+jk] = ipio2[jv+jk].
        int j = jv - jx;
        int m = jx + jk;
        for (int i = 0; i <= m; i++, j++) f[i] = j < 0 ? 0.0 : ipio2[j];

        double fw;
        for (int i = 0; i <= jk; i++)
        {
            fw = 0.0;
            for (j = 0; j <= jx; j++) fw += x[j] * f[jx + i - j];
            q[i] = fw;
        }

        int jz = jk;
        int n;
        int ih;
        double z;
        while (true)
        {
            // q[] в iq[] по 24 бита, в обратном порядке.
            int i = 0;
            j = jz;
            z = q[jz];
            for (; j > 0; i++, j--)
            {
                fw = (int)(TwoPowMinus24 * z);
                iq[i] = (int)(z - TwoPow24 * fw);
                z = q[j - 1] + fw;
            }

            z = ScaleB(z, q0);
            z -= 8.0 * Math.Floor(z * 0.125);
            n = (int)z;
            z -= n;
            ih = 0;
            if (q0 > 0)
            {
                i = iq[jz - 1] >> (24 - q0);
                n += i;
                iq[jz - 1] -= i << (24 - q0);
                ih = iq[jz - 1] >> (23 - q0);
            }
            else if (q0 == 0)
            {
                ih = iq[jz - 1] >> 23;
            }
            else if (z >= 0.5)
            {
                ih = 2;
            }

            if (ih > 0)
            {
                // q > 0.5: берём 1 − q.
                n += 1;
                int carry = 0;
                for (i = 0; i < jz; i++)
                {
                    j = iq[i];
                    if (carry == 0)
                    {
                        if (j != 0)
                        {
                            carry = 1;
                            iq[i] = 0x1000000 - j;
                        }
                    }
                    else
                    {
                        iq[i] = 0xffffff - j;
                    }
                }
                if (q0 == 1) iq[jz - 1] &= 0x7fffff;
                else if (q0 == 2) iq[jz - 1] &= 0x3fffff;
                if (ih == 2)
                {
                    z = 1.0 - z;
                    if (carry != 0) z -= ScaleB(1.0, q0);
                }
            }

            // Нужен ли пересчёт с большим числом членов.
            if (z == 0)
            {
                j = 0;
                for (i = jz - 1; i >= jk; i--) j |= iq[i];
                if (j == 0)
                {
                    int k = 1;
                    while (iq[jk - k] == 0) k++;
                    for (i = jz + 1; i <= jz + k; i++)
                    {
                        f[jx + i] = ipio2[jv + i];
                        fw = 0.0;
                        for (j = 0; j <= jx; j++) fw += x[j] * f[jx + i - j];
                        q[i] = fw;
                    }
                    jz += k;
                    continue;
                }
            }
            break;
        }

        // Отбросить нулевые члены или разбить z на 24-битные.
        if (z == 0.0)
        {
            jz -= 1;
            q0 -= 24;
            while (iq[jz] == 0)
            {
                jz--;
                q0 -= 24;
            }
        }
        else
        {
            z = ScaleB(z, -q0);
            if (z >= TwoPow24)
            {
                fw = (int)(TwoPowMinus24 * z);
                iq[jz] = (int)(z - TwoPow24 * fw);
                jz += 1;
                q0 += 24;
                iq[jz] = (int)fw;
            }
            else
            {
                iq[jz] = (int)z;
            }
        }

        // Куски в double.
        fw = ScaleB(1.0, q0);
        for (int i = jz; i >= 0; i--)
        {
            q[i] = fw * iq[i];
            fw *= TwoPowMinus24;
        }

        // PIo2[0..jp] · q[jz..0].
        for (int i = jz; i >= 0; i--)
        {
            fw = 0.0;
            for (int k = 0; k <= jp && k <= jz - i; k++) fw += pio2[k] * q[i + k];
            fq[jz - i] = fw;
        }

        // Сжать fq[] в y0 + y1.
        fw = 0.0;
        for (int i = jz; i >= 0; i--) fw += fq[i];
        y0 = ih == 0 ? fw : -fw;
        fw = fq[0] - fw;
        for (int i = 1; i <= jz; i++) fw += fq[i];
        y1 = ih == 0 ? fw : -fw;
        return n & 7;
    }

    /// <summary>musl scalbn: x·2ⁿ одним умножением на степень двойки (с поправками у краёв порядка).</summary>
    private static double ScaleB(double x, int n)
    {
        double y = x;
        if (n > 1023)
        {
            y *= TwoPow1023;
            n -= 1023;
            if (n > 1023)
            {
                y *= TwoPow1023;
                n -= 1023;
                if (n > 1023) n = 1023;
            }
        }
        else if (n < -1022)
        {
            // Итоговое n < −53, чтобы не округлять дважды в денормалах.
            y *= TwoPowMinus1022 * TwoPow53;
            n += 1022 - 53;
            if (n < -1022)
            {
                y *= TwoPowMinus1022 * TwoPow53;
                n += 1022 - 53;
                if (n < -1022) n = -1022;
            }
        }
        return y * BitConverter.Int64BitsToDouble((long)(0x3ff + n) << 52);
    }

    private const double TwoPow1023 = 8.98846567431158e+307;
    private const double TwoPowMinus1022 = 2.2250738585072014e-308;
    private const double TwoPow53 = 9007199254740992.0;
}
