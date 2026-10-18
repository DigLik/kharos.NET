using System.Runtime.CompilerServices;

namespace Kharos.Engine.Numerics;

/// <summary>
/// Арифметика по правилам JS там, где она расходится с .NET: округление, знак, приведение к int32. Нужна, чтобы
/// считать до бита как TS-эталон (<c>packages/shared</c>). Беззнаковый сдвиг <c>&gt;&gt;&gt;</c> и <c>Math.imul</c> в C#
/// свои: <c>&gt;&gt;&gt;</c> и умножение int без проверки переполнения.
/// </summary>
public static class JsMath
{
    /// <summary><c>Math.round</c>: половина — вверх, к +∞ (а не от нуля, как <see cref="Math.Round(double)"/>).</summary>
    public static double Round(double x)
    {
        double floor = Math.Floor(x);
        if (x - floor < 0.5) return floor;
        // Отрицательное, округлённое до нуля, в JS — −0.
        return floor == -1 ? -0.0 : floor + 1;
    }

    /// <summary><c>Math.sign</c>: у нуля — тот же ноль, у NaN — NaN.</summary>
    public static double Sign(double x) => x > 0 ? 1 : x < 0 ? -1 : x;

    /// <summary><c>ToInt32</c> из JS (как у <c>x | 0</c>): отбросить дробь, взять по модулю 2³², NaN и ∞ — 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToInt32(double x)
    {
        // Почти всегда число уже в пределах int: тогда это обычное отбрасывание дроби.
        if (x > -2147483649.0 && x < 2147483648.0) return (int)x;
        return Wrap(x);
    }

    /// <summary><c>ToUint32</c> из JS (как у <c>x &gt;&gt;&gt; 0</c>).</summary>
    public static uint ToUint32(double x) => (uint)ToInt32(x);

    /// <summary>Запись в <c>Uint8Array</c>: <c>ToInt32</c> и младший байт.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ToUint8(double x) => (byte)ToInt32(x);

    private static int Wrap(double x)
    {
        if (!double.IsFinite(x)) return 0;
        double truncated = Math.Truncate(x);
        // |x| ≥ 2⁵³ — целое; остаток от 2³² точен в double, fmod считает его без округления.
        double modulo = truncated % 4294967296.0;
        if (modulo < 0) modulo += 4294967296.0;
        return unchecked((int)(uint)modulo);
    }
}
