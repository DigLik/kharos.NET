namespace Kharos.Audio.Dsp;

/// <summary>Комплексное БПФ по основанию 2 на месте: действительная и мнимая части — раздельные массивы float.</summary>
internal sealed class Fft
{
    private readonly int _size;
    private readonly int[] _reverse;
    private readonly float[] _cos;
    private readonly float[] _sin;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentException("размер БПФ — степень двойки", nameof(size));
        _size = size;
        _reverse = new int[size];
        var bits = System.Numerics.BitOperations.Log2((uint)size);
        for (var i = 0; i < size; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++)
                r |= ((i >> b) & 1) << (bits - 1 - b);
            _reverse[i] = r;
        }
        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (var i = 0; i < size / 2; i++)
        {
            _cos[i] = (float)Math.Cos(2 * Math.PI * i / size);
            _sin[i] = (float)-Math.Sin(2 * Math.PI * i / size);
        }
    }

    public int Size => _size;

    /// <summary>Прямое преобразование: X[k] = Σ x[n]·e^{−2πikn/N}.</summary>
    public void Forward(Span<float> re, Span<float> im) => Run(re, im, inverse: false);

    /// <summary>Обратное без деления на N.</summary>
    public void Inverse(Span<float> re, Span<float> im) => Run(re, im, inverse: true);

    private void Run(Span<float> re, Span<float> im, bool inverse)
    {
        var n = _size;
        for (var i = 0; i < n; i++)
        {
            var j = _reverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        var sign = inverse ? -1f : 1f;
        for (var length = 2; length <= n; length <<= 1)
        {
            var half = length >> 1;
            var stride = n / length;
            for (var start = 0; start < n; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var wr = _cos[k * stride];
                    var wi = sign * _sin[k * stride];
                    int a = start + k, b = a + half;
                    var tr = re[b] * wr - im[b] * wi;
                    var ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }
}
