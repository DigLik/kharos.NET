using Kharos.Audio.Graph;

namespace Kharos.Audio.Dsp;

/// <summary>
/// Коэффициенты BiquadFilterNode по формулам спецификации Web Audio (Audio EQ Cookbook; у lowpass и highpass Q —
/// в децибелах), с краевыми случаями частоты 0 и Найквиста, как в Chromium.
/// </summary>
public static class Biquad
{
    /// <summary>Коэффициенты, делённые на a0: y = b0·x + b1·x₋₁ + b2·x₋₂ − a1·y₋₁ − a2·y₋₂.</summary>
    public readonly record struct Coefficients(double B0, double B1, double B2, double A1, double A2)
    {
        /// <summary>|H(e^{jω})| на частоте hz.</summary>
        public double Magnitude(double hz, double sampleRate)
        {
            var w = 2 * Math.PI * hz / sampleRate;
            double c1 = Math.Cos(w), s1 = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
            double nr = B0 + B1 * c1 + B2 * c2, ni = -(B1 * s1 + B2 * s2);
            double dr = 1 + A1 * c1 + A2 * c2, di = -(A1 * s1 + A2 * s2);
            return Math.Sqrt((nr * nr + ni * ni) / (dr * dr + di * di));
        }
    }

    /// <summary>Коэффициенты фильтра type на частоте frequency (Гц, уже с detune), добротности q и усилении gain (дБ).</summary>
    public static Coefficients Compute(FilterType type, double frequency, double q, double gain, double sampleRate)
    {
        var nyquist = sampleRate / 2;
        var f = Math.Clamp(frequency / nyquist, 0, 1);
        var a = Math.Pow(10, gain / 40);
        if (type is FilterType.Lowpass or FilterType.Highpass)
        {
            if (f >= 1)
                return type == FilterType.Lowpass ? Pass(1) : Pass(0);
            if (f <= 0)
                return type == FilterType.Lowpass ? Pass(0) : Pass(1);
        }
        var w0 = Math.PI * f;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        switch (type)
        {
            case FilterType.Lowpass:
            {
                var alpha = sin / (2 * Math.Pow(10, q / 20));
                return Normalize((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
            }
            case FilterType.Highpass:
            {
                var alpha = sin / (2 * Math.Pow(10, q / 20));
                return Normalize((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
            }
            case FilterType.Bandpass:
            {
                if (f <= 0 || f >= 1)
                    return Pass(0);
                if (q <= 0)
                    return Pass(1);
                var alpha = sin / (2 * q);
                return Normalize(alpha, 0, -alpha, 1 + alpha, -2 * cos, 1 - alpha);
            }
            case FilterType.Lowshelf:
            {
                if (f >= 1)
                    return Pass(a * a);
                if (f <= 0)
                    return Pass(1);
                var alpha = sin / 2 * Math.Sqrt(2); // наклон S = 1: √((A + 1/A)(1/S − 1) + 2) = √2
                var k = 2 * Math.Sqrt(a) * alpha;
                return Normalize(
                    a * ((a + 1) - (a - 1) * cos + k),
                    2 * a * ((a - 1) - (a + 1) * cos),
                    a * ((a + 1) - (a - 1) * cos - k),
                    (a + 1) + (a - 1) * cos + k,
                    -2 * ((a - 1) + (a + 1) * cos),
                    (a + 1) + (a - 1) * cos - k);
            }
            case FilterType.Highshelf:
            {
                if (f >= 1)
                    return Pass(1);
                if (f <= 0)
                    return Pass(a * a);
                var alpha = sin / 2 * Math.Sqrt(2); // наклон S = 1: √((A + 1/A)(1/S − 1) + 2) = √2
                var k = 2 * Math.Sqrt(a) * alpha;
                return Normalize(
                    a * ((a + 1) + (a - 1) * cos + k),
                    -2 * a * ((a - 1) + (a + 1) * cos),
                    a * ((a + 1) + (a - 1) * cos - k),
                    (a + 1) - (a - 1) * cos + k,
                    2 * ((a - 1) - (a + 1) * cos),
                    (a + 1) - (a - 1) * cos - k);
            }
            case FilterType.Peaking:
            {
                if (f <= 0 || f >= 1)
                    return Pass(1);
                if (q <= 0)
                    return Pass(a * a);
                var alpha = sin / (2 * q);
                return Normalize(1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a);
            }
            case FilterType.Notch:
            {
                if (f <= 0 || f >= 1)
                    return Pass(1);
                if (q <= 0)
                    return Pass(0);
                var alpha = sin / (2 * q);
                return Normalize(1, -2 * cos, 1, 1 + alpha, -2 * cos, 1 - alpha);
            }
            default:
            {
                if (f <= 0 || f >= 1)
                    return Pass(1);
                if (q <= 0)
                    return Pass(-1);
                var alpha = sin / (2 * q);
                return Normalize(1 - alpha, -2 * cos, 1 + alpha, 1 + alpha, -2 * cos, 1 - alpha);
            }
        }
    }

    private static Coefficients Pass(double gain) => new(gain, 0, 0, 0, 0);

    private static Coefficients Normalize(double b0, double b1, double b2, double a0, double a1, double a2) =>
        new(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
}
